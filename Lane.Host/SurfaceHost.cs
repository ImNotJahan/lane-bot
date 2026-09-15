using Lane.Core.Identity;
using Lane.Core.Surfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Host;

/// <summary>
/// Supervises one surface instance.
///
/// The surface is *built* here rather than handed in already constructed. That matters:
/// construction is where a missing token or a bad endpoint shows up, and building it in
/// the composition root would let one misconfigured surface abort the whole process before
/// any of the others got to start. A Discord bot with no token should cost you Discord,
/// not the terminal and the API as well.
/// </summary>
public sealed class SurfaceHost(
    Func<ISurface> build,
    SurfaceId id,
    ILogger<SurfaceHost> log) : IHostedService, IAsyncDisposable
{
    private ISurface? _surface;

    public SurfaceId Id => id;

    public bool Running => _surface is not null;

    /// <summary>Null until it has started, and if starting failed.</summary>
    public ISurface? Surface => _surface;

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            _surface = build();

            await _surface.StartAsync(ct).ConfigureAwait(false);

            log.LogInformation("Surface {Surface} started", id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down before the connection finished is not a failure worth alarming
            // about — it is what happens whenever the process is stopped promptly.
            log.LogInformation("Surface {Surface} was still starting when shutdown began", id);

            _surface = null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Surface {Surface} failed to start and will be unavailable", id);

            _surface = null;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_surface is null) return;

        try
        {
            await _surface.StopAsync(ct).ConfigureAwait(false);

            log.LogInformation("Surface {Surface} stopped", id);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Surface {Surface} did not stop cleanly", id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_surface is not null) await _surface.DisposeAsync().ConfigureAwait(false);
    }
}
