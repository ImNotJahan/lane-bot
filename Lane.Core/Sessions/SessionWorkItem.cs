using Lane.Core.Identity;
using Lane.Core.Messages;

namespace Lane.Core.Sessions;

/// <summary>Something that happened on a surface and wants Lane's attention.</summary>
public sealed record InboundEvent
{
    public required SessionId   Session { get; init; }
    public required Participant Author  { get; init; }
    public required LaneMessage Message { get; init; }

    /// <summary>The platform's own id for this message, where there is one.</summary>
    public string? ExternalId { get; init; }

    /// <summary>False for traffic Lane should remember but never reply to — other bots, system notices.</summary>
    public bool RequiresResponse { get; init; } = true;

    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum ControlAction { Cancel, Flush, Close }

/// <summary>
/// The unit of work on a session's queue. Everything that can make Lane act in a
/// conversation arrives here, which is what makes the ordering guarantee possible.
/// </summary>
public abstract record SessionWorkItem
{
    /// <summary>A message arrived. Consecutive inbounds coalesce into one turn.</summary>
    public sealed record Inbound(InboundEvent Event) : SessionWorkItem;

    /// <summary>
    /// Say this, without consulting the model. The monologue's <c>speak_to_session</c> posts
    /// one of these rather than writing to a channel directly — that is the single rule that
    /// keeps a volunteered thought from cutting across an in-flight reply.
    /// </summary>
    public sealed record Speak(string Text, DeliveryTarget Target, string Reason) : SessionWorkItem;

    /// <summary>Take a turn here with no new message — a follow-up in a conversation gone quiet.</summary>
    public sealed record Nudge(string Reason) : SessionWorkItem;

    public sealed record Control(ControlAction Action, string Reason) : SessionWorkItem;
}

[Flags]
public enum TurnKind
{
    None = 0,

    /// <summary>Replying to something that arrived in a session.</summary>
    Respond = 1 << 0,

    /// <summary>Lane's global thought loop. Has no session.</summary>
    Monologue = 1 << 1,

    /// <summary>Delivering text Lane already decided to say. No model call.</summary>
    Directive = 1 << 2,

    All = Respond | Monologue | Directive
}

public enum SessionState { Idle, Batching, Running, Speaking }

/// <summary>One unit of work handed from a session pump to the turn pipeline.</summary>
public sealed record TurnRequest
{
    public required Session  Session { get; init; }
    public required TurnKind Kind    { get; init; }

    /// <summary>The coalesced messages this turn is answering. Empty for Nudge and Directive.</summary>
    public IReadOnlyList<InboundEvent> Incoming { get; init; } = [];

    /// <summary>For <see cref="TurnKind.Directive"/>: the text to deliver verbatim.</summary>
    public string? DirectiveText { get; init; }

    public DeliveryTarget Target { get; init; } = DeliveryTarget.Primary;
    public string Reason { get; init; } = "";
}

/// <summary>
/// Runs one turn. Sessions depend on this rather than on the pipeline itself, so the
/// pump stays testable without a model, tools, or memory.
/// </summary>
public interface ITurnExecutor
{
    Task ExecuteAsync(TurnRequest request, CancellationToken ct);
}
