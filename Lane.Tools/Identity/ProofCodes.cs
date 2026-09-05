using System.Collections.Concurrent;

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
/// codes single-use and consumed even by a failed attempt, one outstanding per claimant, and
/// a lookup forgiving enough that a code retyped differently — or said out loud and
/// transcribed — still finds its way home.
///
/// What the code <em>looks like</em> is not shared, because that depends on how it travels:
/// see <see cref="ProofCodeShape"/>.
/// </summary>
/// <typeparam name="TKey">Who asked, for the purpose of replacing their previous code.</typeparam>
/// <typeparam name="TSubject">What the code stands for once redeemed.</typeparam>
internal sealed class ProofCodes<TKey, TSubject>(
    TimeProvider time,
    TimeSpan lifetime,
    ProofCodeShape? shape = null,
    int capacity = 32)
    where TKey : notnull
    where TSubject : notnull
{
    private readonly ProofCodeShape _shape = shape ?? ProofCodeShape.Typed;

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

        // Two forms, and the difference matters: one is shown to a person, the other is what
        // comes back after a keyboard or a microphone has had its way with it.
        string code;

        do
        {
            code = _shape.New();
        }
        while (_pending.ContainsKey(_shape.Key(code)));

        _pending[_shape.Key(code)] = new Claim(key, subject, time.GetUtcNow() + lifetime);

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

        if (_pending.TryRemove(_shape.Key(code), out Claim? claim))
        {
            (key, subject) = (claim.Key, claim.Subject);
            return true;
        }

        (key, subject) = (default!, default!);
        return false;
    }

    /// <summary>How many claims are waiting that <paramref name="exclude"/> did not stage.</summary>
    public int Outstanding(TKey exclude)
    {
        Prune();

        return _pending.Count(pending =>
            !EqualityComparer<TKey>.Default.Equals(pending.Value.Key, exclude));
    }

    /// <summary>
    /// Redeems the one claim outstanding, for when there is no code to present — see
    /// <see cref="IdentityToolOptions.RequireProof"/>.
    ///
    /// Claims held by <paramref name="exclude"/> are passed over, because the caller's own
    /// staged claim is never the one they are completing; that is what lets a tool tell its
    /// two halves apart when neither carries a code.
    ///
    /// Refuses when more than one is in flight rather than picking. Without a code there is
    /// nothing to distinguish them by, and guessing would attach one person's account — or
    /// voice — to another's, which is the exact harm the code exists to prevent and the one
    /// thing trusting people does not make acceptable. <paramref name="outstanding"/> says
    /// how many were in the running so the caller can explain itself.
    /// </summary>
    public bool TryRedeemSole(TKey exclude, out TKey key, out TSubject subject, out int outstanding)
    {
        Prune();

        List<string> candidates =
        [
            .. _pending
                .Where(pending => !EqualityComparer<TKey>.Default.Equals(pending.Value.Key, exclude))
                .Select(pending => pending.Key)
        ];

        outstanding = candidates.Count;

        if (outstanding == 1 && _pending.TryRemove(candidates[0], out Claim? claim))
        {
            (key, subject) = (claim.Key, claim.Subject);
            return true;
        }

        (key, subject) = (default!, default!);
        return false;
    }

    private void Prune()
    {
        DateTimeOffset now = time.GetUtcNow();

        foreach ((string code, Claim claim) in _pending)
            if (claim.Expires <= now) _pending.TryRemove(code, out _);
    }
}
