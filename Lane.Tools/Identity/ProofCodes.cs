using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Lane.Tools.Identity;

/// <summary>
/// Short-lived codes that prove somebody is in two places, without a model ever having to
/// take their word for it.
///
/// The pattern is the same wherever it is used: Lane hands a code to whoever is speaking on
/// one channel, and the same person repeats it on the other. Each half can only ever act on
/// the participant actually present in that turn, so the worst a model can do with a tool
/// built on this is offer a code to the person in front of it.
///
/// Shared between linking an account and claiming a voice because the mechanism is
/// identical, and the details it gets right are the sort that quietly rot in a second copy:
/// codes single-use and consumed even by a failed attempt, one outstanding per claimant, no
/// glyphs that get misread, and a normalisation forgiving enough that a code retyped without
/// the hyphen — or said out loud and transcribed — still works.
/// </summary>
/// <typeparam name="TKey">Who asked, for the purpose of replacing their previous code.</typeparam>
/// <typeparam name="TSubject">What the code stands for once redeemed.</typeparam>
internal sealed class ProofCodes<TKey, TSubject>(TimeProvider time, TimeSpan lifetime, int capacity = 32)
    where TKey : notnull
    where TSubject : notnull
{
    // No ambiguous glyphs: these are read off one screen and typed — or said out loud —
    // into another, and "was that an O or a zero" is the whole failure mode.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Claim> _pending = new(StringComparer.Ordinal);

    private sealed record Claim(TKey Key, TSubject Subject, DateTimeOffset Expires);

    public TimeSpan Lifetime => lifetime;

    /// <summary>
    /// A fresh code, or null when too many are already in flight.
    ///
    /// Any code this key already had is dropped rather than kept alongside: somebody who
    /// mistyped once should not leave a live code behind them.
    /// </summary>
    public string? Issue(TKey key, TSubject subject)
    {
        Prune();

        foreach ((string existing, Claim claim) in _pending)
            if (EqualityComparer<TKey>.Default.Equals(claim.Key, key)) _pending.TryRemove(existing, out _);

        if (_pending.Count >= capacity) return null;

        string code = NewCode();

        _pending[code] = new Claim(key, subject, time.GetUtcNow() + lifetime);

        return code;
    }

    /// <summary>
    /// Consumes a code and returns what it stood for, or false if it was never issued or has
    /// expired. Consumed whatever the caller does next — a code that survives a failed
    /// attempt is one somebody else can still try.
    /// </summary>
    public bool TryRedeem(string code, out TKey key, out TSubject subject)
    {
        Prune();

        if (_pending.TryRemove(Normalise(code), out Claim? claim))
        {
            (key, subject) = (claim.Key, claim.Subject);
            return true;
        }

        (key, subject) = (default!, default!);
        return false;
    }

    private string NewCode()
    {
        string code;

        do
        {
            code = RandomNumberGenerator.GetString(Alphabet, 8);
        }
        while (_pending.ContainsKey(code));

        return code[..4] + "-" + code[4..];
    }

    /// <summary>
    /// Case, spacing and the hyphen are all things a person retypes differently — and a code
    /// said out loud comes back with none of them.
    /// </summary>
    public static string Normalise(string code)
    {
        StringBuilder sb = new();

        foreach (char c in code.ToUpperInvariant())
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);

        string bare = sb.ToString();

        return bare.Length == 8 ? bare[..4] + "-" + bare[4..] : bare;
    }

    private void Prune()
    {
        DateTimeOffset now = time.GetUtcNow();

        foreach ((string code, Claim claim) in _pending)
            if (claim.Expires <= now) _pending.TryRemove(code, out _);
    }
}
