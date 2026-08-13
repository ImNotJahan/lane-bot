using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Kernel;

/// <summary>
/// The only way into Lane. Surfaces submit what they heard; the monologue posts what it
/// wants said. Neither can reach a channel directly, which is what keeps conversations
/// from bleeding into one another.
/// </summary>
public interface IAgentKernel
{
    ValueTask SubmitAsync(InboundEvent evt, CancellationToken ct = default);

    /// <summary>Queues work into a session — used by the monologue and by the API's inject endpoint.</summary>
    ValueTask PostAsync(SessionId session, SessionWorkItem item, CancellationToken ct = default);

    /// <summary>Barge-in. Returns false when the session has no turn running.</summary>
    ValueTask<bool> CancelTurnAsync(SessionId session, string reason);
}

public sealed class AgentKernel(ISessionRegistry sessions, ILogger<AgentKernel> log) : IAgentKernel
{
    public ValueTask SubmitAsync(InboundEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!sessions.TryGet(evt.Session, out Session? session))
        {
            // The surface is responsible for calling GetOrCreate with a descriptor first;
            // the kernel cannot invent one, since only the surface knows the memory group.
            log.LogWarning("Dropping event for unknown session {Session}", evt.Session);
            return ValueTask.CompletedTask;
        }

        return session.PostAsync(new SessionWorkItem.Inbound(evt), ct);
    }

    public ValueTask PostAsync(SessionId session, SessionWorkItem item, CancellationToken ct = default)
    {
        if (!sessions.TryGet(session, out Session? found))
        {
            log.LogWarning("Cannot post to unknown session {Session}", session);
            return ValueTask.CompletedTask;
        }

        return found.PostAsync(item, ct);
    }

    public ValueTask<bool> CancelTurnAsync(SessionId session, string reason)
    {
        if (!sessions.TryGet(session, out Session? found)) return ValueTask.FromResult(false);

        found.CancelCurrentTurn(reason);
        return ValueTask.FromResult(true);
    }
}
