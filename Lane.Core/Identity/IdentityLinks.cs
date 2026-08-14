using System.Collections.Concurrent;
using Lane.Core.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Identity;

/// <summary>
/// Links agreed at runtime, as opposed to the map an operator configured.
///
/// Kept apart from <see cref="IdentityResolver"/>'s configured map on purpose: configuration
/// is authoritative and cannot be overwritten by anything said in a conversation, and the
/// two sets stay separately auditable — "who did an operator link" is a different question
/// from "who linked themselves".
/// </summary>
public interface IIdentityLinks
{
    /// <summary>
    /// Called for every inbound message, so it reads from memory and never from the store.
    /// </summary>
    string? GlobalIdFor(ParticipantId account);

    /// <summary>Whether this global id is already somebody, which is asked before minting one.</summary>
    bool IsPerson(string globalUserId);

    /// <summary>
    /// False when nothing durable is backing this. A link that vanishes at the next restart
    /// fragments the memory it was made to join, so the tool refuses rather than pretending.
    /// </summary>
    bool Durable { get; }

    ValueTask LinkAsync(string globalUserId, IReadOnlyList<ParticipantId> accounts, CancellationToken ct);
}

/// <summary>Used when no durable store is configured: nothing is linked, and nothing can be.</summary>
public sealed class NullIdentityLinks : IIdentityLinks
{
    public static NullIdentityLinks Instance { get; } = new();

    public string? GlobalIdFor(ParticipantId account) => null;

    public bool IsPerson(string globalUserId) => false;

    public bool Durable => false;

    public ValueTask LinkAsync(string globalUserId, IReadOnlyList<ParticipantId> accounts, CancellationToken ct) =>
        throw new NotSupportedException("No durable store is configured for identity links.");
}

/// <summary>
/// Runtime links kept in the key-value store, read into memory at startup.
///
/// Loaded once rather than read per message because <see cref="IIdentityResolver.Resolve"/>
/// is synchronous and on the path of every message that arrives. Started before any surface,
/// so the first message of a run already resolves to the right person.
/// </summary>
public sealed class IdentityLinkStore(IKeyValueStore store, ILogger<IdentityLinkStore> log)
    : IIdentityLinks, IHostedService
{
    private const string Prefix = "identity:";

    private static readonly ScopeKey Scope = new("global");

    private readonly ConcurrentDictionary<string, string> _links = new(StringComparer.OrdinalIgnoreCase);

    public bool Durable => true;

    public async Task StartAsync(CancellationToken ct)
    {
        IReadOnlyList<string> keys = await store.ListKeysAsync(Scope, Prefix, ct).ConfigureAwait(false);

        foreach (string key in keys)
        {
            string? globalId = await store.GetAsync<string>(Scope, key, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(globalId)) _links[key[Prefix.Length..]] = globalId;
        }

        if (!_links.IsEmpty) log.LogInformation("Loaded {Count} identity links agreed at runtime", _links.Count);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public string? GlobalIdFor(ParticipantId account) => _links.GetValueOrDefault(account.ToString());

    public bool IsPerson(string globalUserId) =>
        _links.Values.Contains(globalUserId, StringComparer.OrdinalIgnoreCase);

    public async ValueTask LinkAsync(
        string globalUserId, IReadOnlyList<ParticipantId> accounts, CancellationToken ct)
    {
        // Written before the in-memory map is updated: a link that is live for this run but
        // absent after a restart would split the memory it was made to join, whereas one
        // stored but not yet live simply takes effect on the next message.
        foreach (ParticipantId account in accounts)
            await store.SetAsync(Scope, Prefix + account, globalUserId, ct).ConfigureAwait(false);

        foreach (ParticipantId account in accounts) _links[account.ToString()] = globalUserId;

        log.LogInformation("Linked {Accounts} as {GlobalId}",
            string.Join(", ", accounts.Select(a => a.ToString())), globalUserId);
    }
}
