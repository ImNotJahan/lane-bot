using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Lane.Nodes.Portal;

/// <summary>Outstanding sign-in challenges and the bearer tokens issued for them, in memory only.</summary>
internal sealed class PortalSignIns(TimeProvider time, TimeSpan lifetime)
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _challenges = new();
    private readonly ConcurrentDictionary<string, (string KeyId, DateTimeOffset Expires)> _tokens = new();

    public byte[] NewChallenge()
    {
        Prune();

        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        _challenges[Base64Url.EncodeToString(challenge)] = time.GetUtcNow() + ChallengeLifetime;

        return challenge;
    }

    /// <summary>Consumes <paramref name="challenge"/> (base64url); false if it was never issued, already used or expired.</summary>
    public bool Redeem(string challenge) =>
        _challenges.TryRemove(challenge, out DateTimeOffset expires) && expires > time.GetUtcNow();

    public string Issue(string keyId)
    {
        Prune();

        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = (keyId, time.GetUtcNow() + lifetime);

        return token;
    }

    public string? Resolve(string? token) =>
        token is not null && _tokens.TryGetValue(token, out var entry) && entry.Expires > time.GetUtcNow() ? entry.KeyId : null;

    public void Revoke(string token) => _tokens.TryRemove(token, out _);

    private void Prune()
    {
        DateTimeOffset now = time.GetUtcNow();

        foreach ((string challenge, DateTimeOffset expires) in _challenges)
            if (expires <= now) _challenges.TryRemove(challenge, out _);

        foreach ((string token, (string _, DateTimeOffset expires)) in _tokens)
            if (expires <= now) _tokens.TryRemove(token, out _);
    }
}
