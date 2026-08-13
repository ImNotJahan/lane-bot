using Lane.Core.Agent;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Core.Tools;

namespace Lane.Surfaces.Api.Streaming;

/// <summary>
/// Pushes a turn's text into the hub as the model writes it, so a client app can show the
/// reply appearing rather than waiting for the whole thing.
/// </summary>
public sealed class ApiStreamObserver(SessionId session, TurnStreamHub hub) : IAgentObserver
{
    public ValueTask OnTextAsync(string delta, CancellationToken ct)
    {
        if (delta.Length > 0) hub.Publish(session, new TurnStreamEvent.Delta(delta));

        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolStartAsync(string name, CancellationToken ct)
    {
        hub.Publish(session, new TurnStreamEvent.ToolStarted(name));

        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolEndAsync(string name, ToolResult result, CancellationToken ct)
    {
        hub.Publish(session, new TurnStreamEvent.ToolFinished(name, result.IsError));

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Nothing to flush, and deliberately no "done" here: the run finishing is not the turn
    /// finishing. Delivery and persistence still have to happen, and a client told the reply
    /// was over before it was delivered would race its own history endpoint.
    /// </summary>
    public ValueTask OnFinishedAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Offers a streaming observer only for conversations someone is actually watching.
///
/// This is registered in the kernel's container rather than built by the surface, because
/// the turn pipeline is constructed long before any HTTP request arrives. A turn with no
/// watcher gets nothing and costs nothing — including staying on the non-streaming model
/// call it would otherwise have used.
/// </summary>
public sealed class ApiStreamObserverFactory(TurnStreamHub hub) : IAgentObserverFactory
{
    public IAgentObserver? Create(Session session, TurnKind kind)
    {
        if (kind == TurnKind.Directive) return null;

        return hub.IsWatched(session.Id) ? new ApiStreamObserver(session.Id, hub) : null;
    }
}
