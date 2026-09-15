using Lane.Core.Credits;
using Microsoft.Data.Sqlite;

namespace Lane.Memory.Sqlite;

public sealed class SqliteCreditLedger(LaneDatabase database) : ICreditLedger
{
    public async ValueTask<long> BalanceAsync(string account, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = Command(connection, null, "SELECT balance FROM credit_accounts WHERE account = $a");

        command.Parameters.AddWithValue("$a", account);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as long? ?? 0;
    }

    public async ValueTask<long> EarnAsync(string account, long amount, string? memo, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        await using SqliteConnection  connection  = database.Open();
        await using SqliteTransaction transaction = connection.BeginTransaction();

        long balance = await DepositAsync(connection, transaction, account, amount, ct).ConfigureAwait(false);
        await AppendAsync(connection, transaction, account, amount, CreditEntryKind.Earned, null, memo, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return balance;
    }

    public async ValueTask<long> SpendAsync(string account, long amount, string? memo, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        await using SqliteConnection  connection  = database.Open();
        await using SqliteTransaction transaction = connection.BeginTransaction();

        long balance = await WithdrawAsync(connection, transaction, account, amount, ct).ConfigureAwait(false);
        await AppendAsync(connection, transaction, account, -amount, CreditEntryKind.Spent, null, memo, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return balance;
    }

    public async ValueTask<long> TransferAsync(string from, string to, long amount, string? memo, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        if (string.Equals(from, to, StringComparison.Ordinal))
            throw new ArgumentException("An account cannot transfer credits to itself.", nameof(to));

        await using SqliteConnection  connection  = database.Open();
        await using SqliteTransaction transaction = connection.BeginTransaction();

        long balance = await WithdrawAsync(connection, transaction, from, amount, ct).ConfigureAwait(false);
        await DepositAsync(connection, transaction, to, amount, ct).ConfigureAwait(false);

        await AppendAsync(connection, transaction, from, -amount, CreditEntryKind.Sent,     to,   memo, ct).ConfigureAwait(false);
        await AppendAsync(connection, transaction, to,    amount, CreditEntryKind.Received, from, memo, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return balance;
    }

    public async ValueTask<IReadOnlyList<CreditEntry>> HistoryAsync(string account, int limit, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = Command(connection, null,
            """
            SELECT id, account, amount, kind, counterparty, memo, at FROM credit_entries
            WHERE account = $a ORDER BY id DESC LIMIT $l
            """);

        command.Parameters.AddWithValue("$a", account);
        command.Parameters.AddWithValue("$l", limit);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        List<CreditEntry> entries = [];

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            entries.Add(new CreditEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                Enum.Parse<CreditEntryKind>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));

        return entries;
    }

    private static async Task<long> DepositAsync(
        SqliteConnection connection, SqliteTransaction transaction, string account, long amount, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            """
            INSERT INTO credit_accounts (account, balance) VALUES ($a, $n)
            ON CONFLICT (account) DO UPDATE SET balance = balance + $n
            RETURNING balance
            """);

        command.Parameters.AddWithValue("$a", account);
        command.Parameters.AddWithValue("$n", amount);

        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    internal static async Task<long> WithdrawAsync(
        SqliteConnection connection, SqliteTransaction transaction, string account, long amount, CancellationToken ct)
    {
        await using SqliteCommand withdraw = Command(connection, transaction,
            "UPDATE credit_accounts SET balance = balance - $n WHERE account = $a AND balance >= $n RETURNING balance");

        withdraw.Parameters.AddWithValue("$a", account);
        withdraw.Parameters.AddWithValue("$n", amount);

        if (await withdraw.ExecuteScalarAsync(ct).ConfigureAwait(false) is long balance) return balance;

        await using SqliteCommand current = Command(connection, transaction, "SELECT balance FROM credit_accounts WHERE account = $a");
        current.Parameters.AddWithValue("$a", account);

        throw new InsufficientCreditsException(account, await current.ExecuteScalarAsync(ct).ConfigureAwait(false) as long? ?? 0, amount);
    }

    internal static async Task AppendAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string account, long amount, CreditEntryKind kind, string? counterparty, string? memo, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            """
            INSERT INTO credit_entries (account, amount, kind, counterparty, memo, at)
            VALUES ($a, $n, $k, $c, $m, $t)
            """);

        command.Parameters.AddWithValue("$a", account);
        command.Parameters.AddWithValue("$n", amount);
        command.Parameters.AddWithValue("$k", kind.ToString());
        command.Parameters.AddWithValue("$c", (object?)counterparty ?? DBNull.Value);
        command.Parameters.AddWithValue("$m", (object?)memo ?? DBNull.Value);
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        return command;
    }
}
