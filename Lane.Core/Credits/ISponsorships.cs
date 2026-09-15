using System.Security.Cryptography;
using System.Text;

namespace Lane.Core.Credits;

public enum SponsoredKind { DiscordChannel, ApiClient }

/// <summary>One identity paying for Lane to be reachable somewhere.</summary>
/// <param name="Target">A Discord channel id, or a sponsored API client id.</param>
/// <param name="DailyLimit">Most credits this sponsor spends on the target per UTC day; null for no limit.</param>
/// <param name="SpentToday">Credits spent on the current UTC day.</param>
public sealed record Sponsorship(
    string         Account,
    SponsoredKind  Kind,
    string         Target,
    long?          DailyLimit,
    long           SpentToday,
    long           SpentTotal,
    DateTimeOffset Since)
{
    public bool AllowsToday(long amount) => DailyLimit is null || SpentToday + amount <= DailyLimit;
}

public sealed record SponsoredApiClient(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt)
{
    /// <summary>Lowercase hex SHA-256 of the key, which is all that is stored.</summary>
    public static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}

/// <summary>
/// Who sponsors which Discord channels and API clients. Several accounts may sponsor one target; each charge goes to the
/// sponsor charged least recently that has the balance and daily allowance for it.
/// </summary>
public interface ISponsorships
{
    /// <summary>Starts sponsoring, or changes the daily limit of an existing sponsorship. True when newly started.</summary>
    ValueTask<bool> SponsorAsync(string account, SponsoredKind kind, string target, long? dailyLimit, CancellationToken ct);

    /// <summary>False when the account was not sponsoring the target.</summary>
    ValueTask<bool> WithdrawAsync(string account, SponsoredKind kind, string target, CancellationToken ct);

    ValueTask<IReadOnlyList<Sponsorship>> ListAsync(CancellationToken ct);

    /// <summary>Spends <paramref name="amount"/> from the next sponsor able to pay. Returns that account, or null when none can.</summary>
    ValueTask<string?> ChargeAsync(SponsoredKind kind, string target, long amount, CancellationToken ct);

    /// <summary>False when a client with that id, ignoring case, already exists.</summary>
    ValueTask<bool> CreateApiClientAsync(string id, string name, string keyHash, string createdBy, CancellationToken ct);

    /// <summary>Ignores case.</summary>
    ValueTask<SponsoredApiClient?> FindApiClientAsync(string id, CancellationToken ct);

    ValueTask<SponsoredApiClient?> FindApiClientByKeyAsync(string key, CancellationToken ct);

    ValueTask<IReadOnlyList<SponsoredApiClient>> ListApiClientsAsync(CancellationToken ct);
}

/// <summary>What surfaces consult for anything outside their configuration. Never throws.</summary>
public interface ISponsoredAccess
{
    /// <summary>Charges one request to the target's sponsors. False when it has none able to pay.</summary>
    ValueTask<bool> TryChargeAsync(SponsoredKind kind, string target, CancellationToken ct);

    ValueTask<SponsoredApiClient?> FindApiClientAsync(string key, CancellationToken ct);
}
