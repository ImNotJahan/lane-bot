using Lane.Core.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Memory;

/// <summary>
/// Writes dirty handler state back on a timer, and releases instances that have gone idle.
///
/// The timer is what coalesces writes: a busy conversation touching its window twenty times
/// costs one save, not twenty. v2 called <c>File.WriteAllText</c> on the whole of memory
/// from both the message path and the monologue loop, unsynchronised, on every turn.
/// </summary>
public sealed class MemoryFlushService(
    MemoryHandlerPool pool,
    IOptions<MemoryOptions> options,
    ILogger<MemoryFlushService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MemoryOptions settings = options.Value;

        using PeriodicTimer timer = new(settings.FlushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    // Maintenance first: it is what makes state dirty, and doing it before
                    // the flush means the result is written in the same tick.
                    await pool.MaintainAsync(stoppingToken).ConfigureAwait(false);

                    await pool.FlushAsync(stoppingToken).ConfigureAwait(false);
                    await pool.EvictIdleAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed flush is retried on the next tick; the loop must survive it.
                    log.LogError(ex, "Memory flush failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            // The important one: everything since the last tick would otherwise be lost.
            try
            {
                // No maintenance on the way out — a model call during shutdown would just
                // be cancelled, and the buffered messages are saved either way.
                await pool.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                log.LogInformation("Memory flushed on shutdown");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Final memory flush failed; recent state may be lost");
            }
        }
    }
}
