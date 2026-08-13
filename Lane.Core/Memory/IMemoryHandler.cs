using System.Text.Json.Nodes;
using Lane.Core.Messages;

namespace Lane.Core.Memory;

public sealed record MemoryWrite(LaneMessage Message, MemoryContext Context, ScopeKey Key);

public sealed record MemoryQuery
{
    public required MemoryContext Context { get; init; }
    public required ScopeKey      Key     { get; init; }

    /// <summary>The message being answered, for handlers that search by similarity.</summary>
    public LaneMessage? Cue { get; init; }

    /// <summary>
    /// Restricts recall to certain kinds. Replaces v2's <c>ForThoughts</c> boolean, which
    /// forced two near-identical sliding-window configurations to express one distinction.
    /// </summary>
    public MessageKind[]? Kinds { get; init; }

    public int Limit { get; init; } = 50;
}

public sealed record MemoryRecall(IReadOnlyList<LaneMessage> Messages, string? RenderedText = null)
{
    public static MemoryRecall Empty { get; } = new([]);
}

/// <summary>
/// One store of remembered things, at one scope.
///
/// An implementation is never shared across scope keys — the pool creates one instance per
/// (handler id, scope key) and serialises access to it, so implementations may hold plain
/// mutable state without synchronising.
/// </summary>
public interface IMemoryHandler : IAsyncDisposable
{
    /// <summary>The configured id, unique per handler entry: "recent", "thoughts", "summary".</summary>
    string Id { get; }

    MemoryScope Scope { get; }
    MemorySlot  Slot  { get; }

    /// <summary>Sort order within a slot. Lower comes first.</summary>
    int Order { get; }

    /// <summary>Heading for the rendered block. Null renders the text unlabelled.</summary>
    string? SectionTitle { get; }

    ValueTask RememberAsync(MemoryWrite write, CancellationToken ct);

    ValueTask<MemoryRecall> RecallAsync(MemoryQuery query, CancellationToken ct);

    /// <summary>Null when there is nothing worth persisting.</summary>
    ValueTask<JsonNode?> SaveStateAsync(CancellationToken ct);

    /// <summary>
    /// Restores previously saved state. Implementations must tolerate older shapes rather
    /// than throwing — v2's deserialisation threw on any mismatch, which turned a schema
    /// change into a process that would not start.
    /// </summary>
    ValueTask LoadStateAsync(JsonNode state, CancellationToken ct);
}

/// <summary>Everything needed to construct one configured handler instance.</summary>
public sealed record MemoryHandlerOptions
{
    public required string      Id    { get; init; }
    public required string      Type  { get; init; }
    public MemoryScope          Scope { get; init; } = MemoryScope.Session;
    public MemorySlot           Slot  { get; init; } = MemorySlot.Inline;
    public int                  Order { get; init; }
    public string?              SectionTitle { get; init; }

    /// <summary>Never evicted while idle. Set for handlers whose warm state is expensive.</summary>
    public bool Pinned { get; init; }

    /// <summary>
    /// Confines the handler to 1:1 conversations. The guard for User-scoped memory: recalling
    /// what someone said in a DM into a busy channel is a disclosure, not a bug.
    /// </summary>
    public bool DirectSessionsOnly { get; init; }

    public int MaxMessages { get; init; } = 20;

    /// <summary>Restricts what this handler stores and returns.</summary>
    public MessageKind[]? Kinds { get; init; }

    /// <summary>
    /// Prompt template for handlers that think about what they hold. Falls back to a
    /// built-in instruction when the template is absent, so a bare checkout still works.
    /// </summary>
    public string PromptName { get; init; } = "";

    /// <summary>Upper bound on what a handler keeps, where that differs from MaxMessages.</summary>
    public int MaxEntries { get; init; } = 20;
}

/// <summary>Builds one handler instance for one scope key.</summary>
public interface IMemoryHandlerFactory
{
    /// <summary>The config discriminator: "SlidingWindow", "Summary", "Rag".</summary>
    string TypeName { get; }

    IMemoryHandler Create(MemoryHandlerOptions options, ScopeKey key, IServiceProvider services);
}
