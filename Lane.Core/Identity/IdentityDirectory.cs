using System.Collections.Concurrent;
using Lane.Core.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Identity;

/// <summary>
/// What has been established about people in conversation, as opposed to what an operator
/// configured: which accounts are one person, and what that person asked to be called.
///
/// Kept apart from <see cref="IdentityResolver"/>'s configured map on purpose. Configuration
/// is authoritative and cannot be overwritten by anything said in a conversation, and the
/// two sets stay separately auditable — "who did an operator link" is a different question
/// from "who linked themselves".
/// </summary>
public interface IIdentityDirectory
{
    /// <summary>
    /// Called for every inbound message, so it reads from memory and never from the store.
    /// </summary>
    string? GlobalIdFor(ParticipantId account);

    /// <summary>Whether this global id is already somebody, which is asked before minting one.</summary>
    bool IsPerson(string globalUserId);

    /// <summary>
    /// The name this person asked to be called, keyed by <see cref="Participant.StableKey"/>
    /// — the person where they are linked, the account where they are not.
    /// </summary>
    string? NameFor(string stableKey);

    /// <summary>
    /// Whether somebody else has already chosen this name. Attribution in a prompt is by
    /// name, so two people answering to one is somebody being quoted as somebody else.
    /// </summary>
    bool NameIsTaken(string name, string exceptFor);

    /// <summary>
    /// False when nothing durable is backing this. A link or a name that vanishes at the
    /// next restart is worse than one that was never offered, so the tools refuse instead.
    /// </summary>
    bool Durable { get; }

    ValueTask LinkAsync(string globalUserId, IReadOnlyList<ParticipantId> accounts, CancellationToken ct);

    /// <summary>Records a chosen name, or forgets it when <paramref name="name"/> is null.</summary>
    ValueTask SetNameAsync(string stableKey, string? name, CancellationToken ct);
}

/// <summary>Used when no durable store is configured: nothing is known, and nothing can be.</summary>
public sealed class NullIdentityDirectory : IIdentityDirectory
{
    public static NullIdentityDirectory Instance { get; } = new();

    public string? GlobalIdFor(ParticipantId account) => null;

    public bool IsPerson(string globalUserId) => false;

    public string? NameFor(string stableKey) => null;

    public bool NameIsTaken(string name, string exceptFor) => false;

    public bool Durable => false;

    public ValueTask LinkAsync(string globalUserId, IReadOnlyList<ParticipantId> accounts, CancellationToken ct) =>
        throw new NotSupportedException("No durable store is configured for identity links.");

    public ValueTask SetNameAsync(string stableKey, string? name, CancellationToken ct) =>
        throw new NotSupportedException("No durable store is configured for chosen names.");
}

/// <summary>
/// The directory kept in the key-value store, read into memory at startup.
///
/// Loaded once rather than read per message because <see cref="IIdentityResolver.Resolve"/>
/// is synchronous and on the path of every message that arrives. Started before any surface,
/// so the first message of a run already resolves to the right person, under the right name.
/// </summary>
public sealed class IdentityDirectory(IKeyValueStore store, ILogger<IdentityDirectory> log)
    : IIdentityDirectory, IHostedService
{
    // Not "identity:name:" — that would be swept up by the prefix scan for links, and one
    // person's chosen name would load as somebody's global id.
    private const string LinkPrefix = "identity:";
    private const string NamePrefix = "identity-name:";

    private static readonly ScopeKey Scope = new("global");

    private readonly ConcurrentDictionary<string, string> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public bool Durable => true;

    public async Task StartAsync(CancellationToken ct)
    {
        await LoadAsync(LinkPrefix, _links, ct).ConfigureAwait(false);
        await LoadAsync(NamePrefix, _names, ct).ConfigureAwait(false);

        if (!_links.IsEmpty || !_names.IsEmpty)
            log.LogInformation("Loaded {Links} identity link(s) and {Names} chosen name(s)",
                _links.Count, _names.Count);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public string? GlobalIdFor(ParticipantId account) => _links.GetValueOrDefault(account.ToString());

    public bool IsPerson(string globalUserId) =>
        _links.Values.Contains(globalUserId, StringComparer.OrdinalIgnoreCase);

    public string? NameFor(string stableKey) => _names.GetValueOrDefault(stableKey);

    public async ValueTask LinkAsync(
        string globalUserId, IReadOnlyList<ParticipantId> accounts, CancellationToken ct)
    {
        // Written before the in-memory map is updated: a link that is live for this run but
        // absent after a restart would split the memory it was made to join, whereas one
        // stored but not yet live simply takes effect on the next message.
        foreach (ParticipantId account in accounts)
            await store.SetAsync(Scope, LinkPrefix + account, globalUserId, ct).ConfigureAwait(false);

        foreach (ParticipantId account in accounts) _links[account.ToString()] = globalUserId;

        log.LogInformation("Linked {Accounts} as {GlobalId}",
            string.Join(", ", accounts.Select(a => a.ToString())), globalUserId);
    }

    public async ValueTask SetNameAsync(string stableKey, string? name, CancellationToken ct)
    {
        if (name is null)
        {
            await store.RemoveAsync(Scope, NamePrefix + stableKey, ct).ConfigureAwait(false);
            _names.TryRemove(stableKey, out _);

            log.LogInformation("Forgot the chosen name for {Person}", stableKey);
            return;
        }

        await store.SetAsync(Scope, NamePrefix + stableKey, name, ct).ConfigureAwait(false);
        _names[stableKey] = name;

        log.LogInformation("{Person} is called {Name} from now on", stableKey, name);
    }

    /// <summary>Names in use, so a second person cannot quietly adopt somebody else's.</summary>
    public bool NameIsTaken(string name, string exceptFor) =>
        _names.Any(entry => !string.Equals(entry.Key, exceptFor, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(entry.Value, name, StringComparison.OrdinalIgnoreCase));

    private async Task LoadAsync(string prefix, ConcurrentDictionary<string, string> into, CancellationToken ct)
    {
        IReadOnlyList<string> keys = await store.ListKeysAsync(Scope, prefix, ct).ConfigureAwait(false);

        foreach (string key in keys)
        {
            string? value = await store.GetAsync<string>(Scope, key, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(value)) into[key[prefix.Length..]] = value;
        }
    }
}
