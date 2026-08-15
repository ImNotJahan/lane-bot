using System.Collections.Concurrent;
using Lane.Core.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Sessions;

/// <summary>
/// What Lane has written down about a conversation, carried in her prompt every time she
/// speaks in it.
///
/// This is the counterpart to the scratchpad. A note is something she goes and reads; a
/// description is something she cannot help but see, which is what makes it the right place
/// for "this channel is the D&amp;D game and I am running it" and the wrong place for
/// anything she only needs occasionally — everything here is paid for on every turn.
/// </summary>
public interface ISessionDescriptions
{
    /// <summary>
    /// The description for this conversation, or null. Read while the persona is being
    /// built, so it comes from memory rather than the store.
    /// </summary>
    string? For(SessionDescriptor? session);

    /// <summary>
    /// False when nothing durable is backing this. A description that vanishes at the next
    /// restart is worse than one that was never offered, so the tool refuses instead — the
    /// same rule <see cref="Identity.IIdentityDirectory"/> follows.
    /// </summary>
    bool Durable { get; }

    /// <summary>
    /// Records a description against a memory group, or erases it when
    /// <paramref name="description"/> is null.
    /// </summary>
    ValueTask SetAsync(string memoryGroup, string? description, CancellationToken ct);
}

/// <summary>Used when no durable store is configured: nothing is written down, and nothing can be.</summary>
public sealed class NullSessionDescriptions : ISessionDescriptions
{
    public static NullSessionDescriptions Instance { get; } = new();

    public string? For(SessionDescriptor? session) => null;

    public bool Durable => false;

    public ValueTask SetAsync(string memoryGroup, string? description, CancellationToken ct) =>
        throw new NotSupportedException("No durable store is configured for session descriptions.");
}

/// <summary>
/// Descriptions kept in the key-value store, read into memory at startup.
///
/// Keyed by <see cref="SessionDescriptor.MemoryGroup"/> rather than by session id, so a
/// Discord text channel and the voice channel beside it — two sessions, one conversation —
/// are described once. That is the same key session-scoped memory uses, so what she
/// remembers of a conversation and what she has written about it never come apart.
///
/// Stored under one global scope with a prefix rather than under each session's own scope
/// because <see cref="IKeyValueStore.ListKeysAsync"/> enumerates within a scope: keys under
/// per-session scopes could be read one conversation at a time, but never swept up at
/// startup without already knowing every group that exists.
/// </summary>
public sealed class SessionDescriptions(IKeyValueStore store, ILogger<SessionDescriptions> log)
    : ISessionDescriptions, IHostedService
{
    private const string Prefix = "session-description:";

    private static readonly ScopeKey Scope = new("global");

    private readonly ConcurrentDictionary<string, string> _descriptions = new(StringComparer.Ordinal);

    public bool Durable => true;

    public async Task StartAsync(CancellationToken ct)
    {
        IReadOnlyList<string> keys = await store.ListKeysAsync(Scope, Prefix, ct).ConfigureAwait(false);

        foreach (string key in keys)
        {
            string? value = await store.GetAsync<string>(Scope, key, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(value)) _descriptions[key[Prefix.Length..]] = value;
        }

        if (!_descriptions.IsEmpty)
            log.LogInformation("Loaded {Count} session description(s)", _descriptions.Count);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public string? For(SessionDescriptor? session) =>
        session is null ? null : _descriptions.GetValueOrDefault(session.MemoryGroup);

    public async ValueTask SetAsync(string memoryGroup, string? description, CancellationToken ct)
    {
        if (description is null)
        {
            await store.RemoveAsync(Scope, Prefix + memoryGroup, ct).ConfigureAwait(false);
            _descriptions.TryRemove(memoryGroup, out _);

            log.LogInformation("Erased the description of {Group}", memoryGroup);
            return;
        }

        // Written before the in-memory copy is updated, for the same reason a link is: one
        // that is live for this run but gone after a restart is the confusing half-state.
        await store.SetAsync(Scope, Prefix + memoryGroup, description, ct).ConfigureAwait(false);
        _descriptions[memoryGroup] = description;

        log.LogInformation("Described {Group} as: {Description}", memoryGroup, description);
    }
}
