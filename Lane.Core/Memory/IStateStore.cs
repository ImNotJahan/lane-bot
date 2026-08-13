using System.Text.Json.Nodes;
using Lane.Core.Identity;
using Lane.Core.Messages;

namespace Lane.Core.Memory;

/// <summary>Durable state for one handler instance, keyed by (handler id, scope key).</summary>
public interface IStateStore
{
    ValueTask<JsonNode?> LoadAsync(string handlerId, ScopeKey key, CancellationToken ct);

    ValueTask SaveAsync(string handlerId, ScopeKey key, JsonNode state, int schemaVersion, CancellationToken ct);

    ValueTask DeleteAsync(string handlerId, ScopeKey key, CancellationToken ct);
}

public sealed record TranscriptQuery
{
    public SessionId? Session     { get; init; }
    public string?    MemoryGroup { get; init; }
    public string?    GlobalUserId { get; init; }

    /// <summary>Exclusive lower bound, for paging forward.</summary>
    public long AfterSequence { get; init; }

    public int Limit { get; init; } = 100;

    /// <summary>Newest first. The default for "show me the last N".</summary>
    public bool Descending { get; init; } = true;
}

/// <summary>
/// The durable log of everything said. Separate from memory handlers on purpose: handlers
/// hold whatever shape the prompt needs, while this stays a complete, queryable record —
/// the API's history endpoint and any future re-indexing read from here.
/// </summary>
public interface ITranscriptStore
{
    /// <summary>Appends and returns the assigned monotonic sequence.</summary>
    ValueTask<long> AppendAsync(LaneMessage message, CancellationToken ct);

    ValueTask<IReadOnlyList<LaneMessage>> ReadAsync(TranscriptQuery query, CancellationToken ct);
}

/// <summary>Small scoped values: book positions, schedules, per-session flags.</summary>
public interface IKeyValueStore
{
    ValueTask<T?> GetAsync<T>(ScopeKey scope, string key, CancellationToken ct);

    ValueTask SetAsync<T>(ScopeKey scope, string key, T value, CancellationToken ct);

    ValueTask RemoveAsync(ScopeKey scope, string key, CancellationToken ct);
}

/// <summary>Used until a real store is configured, and by tests that do not care about durability.</summary>
public sealed class NullStateStore : IStateStore
{
    public ValueTask<JsonNode?> LoadAsync(string handlerId, ScopeKey key, CancellationToken ct) =>
        ValueTask.FromResult<JsonNode?>(null);

    public ValueTask SaveAsync(string handlerId, ScopeKey key, JsonNode state, int schemaVersion, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask DeleteAsync(string handlerId, ScopeKey key, CancellationToken ct) => ValueTask.CompletedTask;
}

public sealed class NullTranscriptStore : ITranscriptStore
{
    public ValueTask<long> AppendAsync(LaneMessage message, CancellationToken ct) => ValueTask.FromResult(0L);

    public ValueTask<IReadOnlyList<LaneMessage>> ReadAsync(TranscriptQuery query, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<LaneMessage>>([]);
}
