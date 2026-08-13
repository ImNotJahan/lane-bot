using System.Collections.Concurrent;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Surfaces.Api.Streaming;

namespace Lane.Surfaces.Api;

/// <summary>Everything the surface holds for one open API conversation.</summary>
public sealed record ApiSessionEntry(
    SessionId Id,
    string    Key,
    string    ClientId,
    string    MemoryGroup,
    IDisposable Attachment)
{
    public bool BelongsTo(ApiClient client) =>
        string.Equals(ClientId, client.Id, StringComparison.OrdinalIgnoreCase) ||
        Key.StartsWith(ApiKeys.SharedPrefix, StringComparison.Ordinal);
}

/// <summary>
/// Turns a client's own name for a conversation into a session, and remembers what it
/// attached.
///
/// The namespacing is the cohesion guarantee at this surface: two apps that both call their
/// conversation "main" get two conversations, and neither can post into the other's. Sharing
/// one is possible but has to be asked for by name.
/// </summary>
public sealed class ApiSessionMap(
    SurfaceId surface,
    ISessionRegistry sessions,
    TurnStreamHub hub)
{
    private readonly ConcurrentDictionary<string, ApiSessionEntry> _open = new(StringComparer.Ordinal);

    /// <summary>Which surface these sessions belong to. Participants are namespaced by it.</summary>
    public SurfaceId Surface => surface;

    public IReadOnlyCollection<ApiSessionEntry> Open => [.. _open.Values];

    public SessionId IdFor(ApiClient client, string key, SessionKind kind = SessionKind.Api) =>
        new(surface, kind, client.LocalKeyFor(key));

    /// <summary>
    /// The memory group is deliberately independent of the session kind, so a client's text
    /// conversation and its voice socket are one conversation as far as Lane's memory is
    /// concerned — the same relationship a Discord channel has with the voice channel
    /// beside it.
    /// </summary>
    public string MemoryGroupFor(ApiClient client, string key) => $"{surface.Value}/{client.LocalKeyFor(key)}";

    public bool TryGet(SessionId id, out ApiSessionEntry? entry) => _open.TryGetValue(id.Value, out entry);

    /// <summary>Opens the conversation if it is new, and refreshes it if it is not.</summary>
    public ApiSessionEntry Ensure(
        ApiClient client,
        string key,
        SessionKind kind,
        Participant speaker,
        string? displayName = null,
        string? memoryGroup = null,
        bool? direct = null,
        ChannelCapabilities capabilities = ChannelCapabilities.Text)
    {
        SessionId id = IdFor(client, key, kind);

        string group = memoryGroup ?? MemoryGroupFor(client, key);

        // Idempotent: every request re-asserts the descriptor, which is how a participant
        // joining or a display name changing reaches the kernel without a separate call.
        sessions.GetOrCreate(new SessionDescriptor
        {
            Id           = id,
            DisplayName  = displayName ?? DefaultName(client, key, kind),
            MemoryGroup  = group,
            Capabilities = capabilities,

            // A conversation with exactly one human on the other end is where User-scoped
            // memory is safe to surface. A client relaying several people is not that.
            IsDirect          = direct ?? true,
            KnownParticipants = [speaker]
        });

        return _open.GetOrAdd(id.Value, _ => new ApiSessionEntry(
            id, key, client.Id, group,
            sessions.Attach(new ApiSessionChannel(id, hub, capabilities))));
    }

    /// <summary>Registers a channel the caller built — the voice socket owns its own.</summary>
    public ApiSessionEntry OpenWith(
        ApiClient client,
        string key,
        SessionKind kind,
        Participant speaker,
        ISessionChannel channel,
        string? displayName = null)
    {
        SessionId id = channel.Id;

        string group = MemoryGroupFor(client, key);

        sessions.GetOrCreate(new SessionDescriptor
        {
            Id                = id,
            DisplayName       = displayName ?? DefaultName(client, key, kind),
            MemoryGroup       = group,
            Capabilities      = channel.Capabilities,
            IsDirect          = true,
            KnownParticipants = [speaker]
        });

        ApiSessionEntry entry = new(id, key, client.Id, group, sessions.Attach(channel));

        _open[id.Value] = entry;

        return entry;
    }

    private static string DefaultName(ApiClient client, string key, SessionKind kind) =>
        kind == SessionKind.Voice ? $"{client.Name} voice ({key})" : $"{client.Name} ({key})";

    public async ValueTask CloseAsync(SessionId id, string reason)
    {
        if (!_open.TryRemove(id.Value, out ApiSessionEntry? entry)) return;

        // Closed before detaching, not after: closing drains whatever is still queued, and a
        // reply produced during that drain still needs somewhere to go.
        await sessions.CloseAsync(id, reason).ConfigureAwait(false);

        entry.Attachment.Dispose();
    }

    public async ValueTask CloseAllAsync(string reason)
    {
        foreach (ApiSessionEntry entry in _open.Values) await CloseAsync(entry.Id, reason).ConfigureAwait(false);
    }
}
