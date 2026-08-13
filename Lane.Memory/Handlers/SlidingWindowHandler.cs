using System.Text.Json;
using System.Text.Json.Nodes;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Serialization;

namespace Lane.Memory.Handlers;

/// <summary>
/// The last N messages, filtered by kind.
///
/// One configuration is the recent conversation window; another, filtered to thoughts and
/// scoped globally, is Lane's train of thought. v2 needed two near-identical handler types
/// distinguished by a <c>ForThoughts</c> boolean; here it is the same class with a
/// different <c>Kinds</c> filter.
///
/// Not thread-safe by design — the pool holds one instance per scope key and serialises
/// access to it.
/// </summary>
public sealed class SlidingWindowHandler(MemoryHandlerOptions options) : IMemoryHandler
{
    private readonly List<LaneMessage> _messages = [];
    private readonly HashSet<MessageKind>? _kinds = options.Kinds is { Length: > 0 }
        ? [.. options.Kinds]
        : null;

    public string      Id    => options.Id;
    public MemoryScope Scope => options.Scope;
    public MemorySlot  Slot  => options.Slot;
    public int         Order => options.Order;
    public string?     SectionTitle => options.SectionTitle;

    public ValueTask RememberAsync(MemoryWrite write, CancellationToken ct)
    {
        if (!Accepts(write.Message.Kind)) return ValueTask.CompletedTask;

        // The window is replayed as real conversation turns, so tool traffic is stripped
        // before it goes in — a trim that separated a tool call from its result would
        // poison every later request in this conversation.
        LaneMessage? storable = MemorySanitizer.ForMemory(write.Message);

        if (storable is null) return ValueTask.CompletedTask;

        _messages.Add(storable);

        int excess = _messages.Count - options.MaxMessages;
        if (excess > 0) _messages.RemoveRange(0, excess);

        return ValueTask.CompletedTask;
    }

    public ValueTask<MemoryRecall> RecallAsync(MemoryQuery query, CancellationToken ct)
    {
        IEnumerable<LaneMessage> selected = _messages;

        // The query may narrow further than the handler's own filter — a caller asking
        // only for thoughts out of a window that holds everything.
        if (query.Kinds is { Length: > 0 })
        {
            HashSet<MessageKind> wanted = [.. query.Kinds];
            selected = selected.Where(m => wanted.Contains(m.Kind));
        }

        List<LaneMessage> result = [.. selected];

        if (result.Count > query.Limit) result.RemoveRange(0, result.Count - query.Limit);

        return ValueTask.FromResult(new MemoryRecall(result));
    }

    public ValueTask<JsonNode?> SaveStateAsync(CancellationToken ct)
    {
        StoredMessage[] stored = [.. _messages.Select(LaneJson.ToStored)];

        JsonNode? node = JsonSerializer.SerializeToNode(
            new WindowState { Messages = stored }, LaneJson.Options);

        return ValueTask.FromResult(node);
    }

    public ValueTask LoadStateAsync(JsonNode state, CancellationToken ct)
    {
        WindowState? restored = state.Deserialize<WindowState>(LaneJson.Options);

        _messages.Clear();

        if (restored?.Messages is null) return ValueTask.CompletedTask;

        foreach (StoredMessage stored in restored.Messages)
        {
            LaneMessage message = LaneJson.FromStored(stored);

            // The kind filter may have been tightened since this was written; honour the
            // current configuration rather than reviving messages it no longer wants.
            if (Accepts(message.Kind)) _messages.Add(message);
        }

        int excess = _messages.Count - options.MaxMessages;
        if (excess > 0) _messages.RemoveRange(0, excess);

        return ValueTask.CompletedTask;
    }

    private bool Accepts(MessageKind kind) => _kinds is null || _kinds.Contains(kind);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class WindowState
    {
        public int V { get; init; } = 1;
        public StoredMessage[]? Messages { get; init; }
    }
}

public sealed class SlidingWindowFactory : IMemoryHandlerFactory
{
    public string TypeName => "SlidingWindow";

    public IMemoryHandler Create(MemoryHandlerOptions options, ScopeKey key, IServiceProvider services) =>
        new SlidingWindowHandler(options);
}
