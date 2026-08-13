using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Agent;

/// <summary>
/// Lets more than one thing watch a turn.
///
/// The reason this exists: a voice conversation held over the API wants both — audio, so
/// she can be heard, and text deltas, so the client app can show the words appearing.
/// Picking a single observer would silently give one of them nothing, and which one won
/// would depend on registration order.
///
/// One observer failing never costs the others their fragments, and never costs the reply.
/// </summary>
public sealed class CompositeAgentObserver(IReadOnlyList<IAgentObserver> observers, ILogger? log = null)
    : IAgentObserver
{
    /// <summary>Returns null for none, the observer itself for one, a composite for several.</summary>
    public static IAgentObserver? Of(IReadOnlyList<IAgentObserver> observers, ILogger? log = null) => observers.Count switch
    {
        0 => null,
        1 => observers[0],
        _ => new CompositeAgentObserver(observers, log)
    };

    public ValueTask OnTextAsync(string delta, CancellationToken ct) =>
        EachAsync(o => o.OnTextAsync(delta, ct), nameof(OnTextAsync));

    public ValueTask OnToolStartAsync(string name, CancellationToken ct) =>
        EachAsync(o => o.OnToolStartAsync(name, ct), nameof(OnToolStartAsync));

    public ValueTask OnToolEndAsync(string name, ToolResult result, CancellationToken ct) =>
        EachAsync(o => o.OnToolEndAsync(name, result, ct), nameof(OnToolEndAsync));

    public ValueTask OnFinishedAsync(CancellationToken ct) =>
        EachAsync(o => o.OnFinishedAsync(ct), nameof(OnFinishedAsync));

    /// <summary>
    /// Sequential rather than concurrent, deliberately. Observers are stateful — the voice
    /// one feeds a chunker whose output order is the order speech comes out in — and running
    /// them in parallel buys nothing when each call is a queue push.
    /// </summary>
    private async ValueTask EachAsync(Func<IAgentObserver, ValueTask> call, string stage)
    {
        foreach (IAgentObserver observer in observers)
        {
            try
            {
                await call(observer).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is this observer's business; the others still get the rest.
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "{Observer} threw during {Stage}", observer.GetType().Name, stage);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IAgentObserver observer in observers)
        {
            try { await observer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { log?.LogWarning(ex, "{Observer} threw on disposal", observer.GetType().Name); }
        }
    }
}
