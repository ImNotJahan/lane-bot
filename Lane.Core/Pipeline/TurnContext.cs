using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Sessions;

namespace Lane.Core.Pipeline;

/// <summary>The prompt and history a turn will run against, once memory has been consulted.</summary>
public sealed record AssembledContext(
    IReadOnlyList<PromptBlock> StableBlocks,
    IReadOnlyList<PromptBlock> VolatileBlocks,
    IReadOnlyList<LaneMessage> InlineMessages)
{
    public static AssembledContext Empty { get; } = new([], [], []);

    /// <summary>System blocks in cache order: stable (cached) first, volatile (uncached) last.</summary>
    public IReadOnlyList<PromptBlock> SystemBlocks => [.. StableBlocks, .. VolatileBlocks];
}

/// <summary>
/// Mutable state threaded through the stages of one turn. Stages read what earlier stages
/// wrote and add their own; nothing else is shared between them.
/// </summary>
public sealed class TurnContext
{
    public required Session  Session { get; init; }
    public required TurnKind Kind    { get; init; }

    /// <summary>The messages this turn is answering, already coalesced by the pump.</summary>
    public required IReadOnlyList<LaneMessage> Incoming { get; init; }

    /// <summary>For a directive turn: text Lane has already decided to say.</summary>
    public string? DirectiveText { get; init; }

    public DeliveryTarget Target { get; set; } = DeliveryTarget.Primary;

    public string Reason { get; init; } = "";

    public AssembledContext Context { get; set; } = AssembledContext.Empty;

    /// <summary>What the turn produced. Null until the agent stage runs.</summary>
    public TurnResult? Result { get; set; }

    /// <summary>Set by a policy stage to end the turn without replying. Memory writes still happen.</summary>
    public bool Suppressed { get; set; }

    public string? SuppressionReason { get; set; }

    /// <summary>Messages the turn wants persisted — set by the stages that produce them.</summary>
    public List<LaneMessage> Produced { get; } = [];

    /// <summary>Free-form bag for stage-to-stage handoff without widening this type.</summary>
    public Dictionary<string, object?> Items { get; } = [];

    public SessionDescriptor Descriptor => Session.Descriptor;
}

public sealed record TurnResult(
    string                     Text,
    IReadOnlyList<LaneMessage> NewMessages,
    StopReason                 Stop,
    TokenUsage                 Usage);
