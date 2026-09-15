using System.Security.Cryptography;
using System.Text;
using Lane.Core.Identity;

namespace Lane.Surfaces.Api;

/// <summary>
/// One authenticated caller.
/// </summary>
/// <param name="Participant">
/// The client app itself, as a speaker. A client that relays several humans overrides this
/// per message; this is who is talking when it does not.
/// </param>
public sealed record ApiClient(
    string Id,
    string Name,
    Participant Participant,
    bool CanObserveAllSessions)
{
    /// <summary>Set for clients created in the node portal, whose requests are paid for by their sponsors.</summary>
    public string? SponsoredId { get; init; }

    /// <summary>
    /// Session keys are namespaced by client, so two apps naming a conversation "main" get
    /// two conversations. Sharing one is opt-in, and looks like it.
    /// </summary>
    public string LocalKeyFor(string key) =>
        key.StartsWith(ApiKeys.SharedPrefix, StringComparison.Ordinal) ? key : $"{Id}/{key}";
}

public static class ApiKeys
{
    /// <summary>Prefix that puts a session outside any one client's namespace.</summary>
    public const string SharedPrefix = "shared:";

    /// <summary>
    /// Keys go in a URL path segment and end up in session ids, database rows and log lines.
    /// Restricting the alphabet is cheaper than escaping it correctly everywhere.
    /// </summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 96) return false;

        ReadOnlySpan<char> body = key.StartsWith(SharedPrefix, StringComparison.Ordinal)
            ? key.AsSpan(SharedPrefix.Length)
            : key;

        if (body.Length == 0) return false;

        foreach (char c in body)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) return false;

        return true;
    }
}

/// <summary>
/// Who is allowed to talk to Lane over HTTP, and as whom.
///
/// Keys are compared in constant time. The comparison is cheap and the alternative leaks
/// the key a character at a time to anyone willing to measure.
/// </summary>
public sealed class ApiClientRegistry
{
    private readonly Dictionary<string, ApiClient> _byId;
    private readonly List<(byte[] Key, ApiClient Client)> _keys = [];
    private readonly ApiClient? _anonymous;
    private readonly SurfaceId _surface;
    private readonly IIdentityResolver _identity;

    /// <summary>Prepended to sponsored client ids, so they never share a session namespace with a configured client.</summary>
    public const string SponsoredPrefix = "sponsored.";

    public ApiClientRegistry(
        SurfaceId surface,
        IReadOnlyList<ApiClientOptions> clients,
        bool allowAnonymous,
        IIdentityResolver identity)
    {
        _byId     = new Dictionary<string, ApiClient>(StringComparer.OrdinalIgnoreCase);
        _surface  = surface;
        _identity = identity;

        foreach (ApiClientOptions options in clients)
        {
            if (string.IsNullOrWhiteSpace(options.Id))
                throw new InvalidOperationException("An API client is missing its Id.");

            if (!ApiKeys.IsValid(options.Id))
                throw new InvalidOperationException(
                    $"API client id '{options.Id}' must be letters, digits, '-', '_' or '.'.");

            if (options.Id.StartsWith(SponsoredPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"API client id '{options.Id}' cannot start with '{SponsoredPrefix}'.");

            if (string.IsNullOrWhiteSpace(options.Key))
                throw new InvalidOperationException(
                    $"API client '{options.Id}' has no key. Set KeyRef, or remove the client.");

            ApiClient client = new(
                options.Id,
                string.IsNullOrWhiteSpace(options.Name) ? options.Id : options.Name,
                Speaker(surface, options, identity),
                options.CanObserveAllSessions);

            if (!_byId.TryAdd(client.Id, client))
                throw new InvalidOperationException($"Two API clients share the id '{client.Id}'.");

            _keys.Add((Encoding.UTF8.GetBytes(options.Key!), client));
        }

        if (!allowAnonymous) return;

        // Only ever reached on loopback; the surface refuses to start otherwise.
        _anonymous = new ApiClient(
            "anonymous", "anonymous",
            identity.Resolve(new ParticipantId(surface, "anonymous"), "anonymous"),
            CanObserveAllSessions: true);
    }

    public bool AllowsAnonymous => _anonymous is not null;

    public ApiClient? Anonymous => _anonymous;

    public int Count => _byId.Count;

    public ApiClient Sponsored(Lane.Core.Credits.SponsoredApiClient sponsored)
    {
        string id = SponsoredPrefix + sponsored.Id;

        return new ApiClient(id, sponsored.Name, _identity.Resolve(new ParticipantId(_surface, id), sponsored.Name), false)
        {
            SponsoredId = sponsored.Id
        };
    }

    /// <summary>
    /// An explicitly configured <c>GlobalUserId</c> wins over the identity map: the map is
    /// keyed on surface-local ids, and a client id is not a person's account.
    /// </summary>
    private static Participant Speaker(
        SurfaceId surface, ApiClientOptions options, IIdentityResolver identity)
    {
        ParticipantId id = new(surface, options.Id);

        string name = string.IsNullOrWhiteSpace(options.Name) ? options.Id : options.Name;

        return string.IsNullOrWhiteSpace(options.GlobalUserId)
            ? identity.Resolve(id, name)
            : new Participant(id, name, options.GlobalUserId);
    }

    public ApiClient? Authenticate(string? presented) => Match(presented) ?? _anonymous;

    /// <summary>The configured client holding this key, ignoring anonymous access.</summary>
    public ApiClient? Match(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;

        byte[] offered = Encoding.UTF8.GetBytes(presented);

        ApiClient? matched = null;

        // Every candidate is compared, and the loop does not break early: returning as soon
        // as one matches makes the response time depend on key order.
        foreach ((byte[] key, ApiClient client) in _keys)
            if (CryptographicOperations.FixedTimeEquals(key, offered)) matched = client;

        return matched;
    }
}
