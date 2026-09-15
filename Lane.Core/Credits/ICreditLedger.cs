namespace Lane.Core.Credits;

public enum CreditEntryKind { Earned, Sent, Received, Spent }

/// <param name="Amount">Positive when credits came in, negative when they went out.</param>
/// <param name="Counterparty">The other account of a transfer.</param>
public sealed record CreditEntry(
    long            Id,
    string          Account,
    long            Amount,
    CreditEntryKind Kind,
    string?         Counterparty,
    string?         Memo,
    DateTimeOffset  At);

public sealed class InsufficientCreditsException(string account, long balance, long requested)
    : InvalidOperationException($"Account {account} has {balance} credits but {requested} were needed.")
{
    public string Account   { get; } = account;
    public long   Balance   { get; } = balance;
    public long   Requested { get; } = requested;
}

/// <summary>
/// Whole-credit balances, keyed by node identity key id. Every change is atomic and recorded as an entry. Amounts must be
/// positive; a balance never goes below zero.
/// </summary>
public interface ICreditLedger
{
    /// <summary>Zero for an account that has never held credits.</summary>
    ValueTask<long> BalanceAsync(string account, CancellationToken ct);

    /// <summary>Creates credits in <paramref name="account"/>. Returns the new balance.</summary>
    ValueTask<long> EarnAsync(string account, long amount, string? memo, CancellationToken ct);

    /// <summary>Removes credits from <paramref name="account"/> in exchange for something. Returns the new balance.</summary>
    /// <exception cref="InsufficientCreditsException"/>
    ValueTask<long> SpendAsync(string account, long amount, string? memo, CancellationToken ct);

    /// <summary>Returns the sender's new balance.</summary>
    /// <exception cref="InsufficientCreditsException"/>
    /// <exception cref="ArgumentException"><paramref name="from"/> and <paramref name="to"/> are the same account.</exception>
    ValueTask<long> TransferAsync(string from, string to, long amount, string? memo, CancellationToken ct);

    /// <summary>Newest first.</summary>
    ValueTask<IReadOnlyList<CreditEntry>> HistoryAsync(string account, int limit, CancellationToken ct);
}
