using System.Globalization;
using Lane.Core.Credits;
using Microsoft.Data.Sqlite;

namespace Lane.Memory.Sqlite;

public sealed class SqliteSponsorships(LaneDatabase database, TimeProvider? time = null) : ISponsorships
{
    private const int SqliteConstraint = 19;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private string Today => _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask<bool> SponsorAsync(string account, SponsoredKind kind, string target, long? dailyLimit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (dailyLimit is < 0) throw new ArgumentOutOfRangeException(nameof(dailyLimit));

        await using SqliteConnection  connection  = database.Open();
        await using SqliteTransaction transaction = connection.BeginTransaction();

        bool exists;

        await using (SqliteCommand find = Command(connection, transaction,
            "SELECT 1 FROM sponsorships WHERE kind = $k AND target = $t AND account = $a"))
        {
            AddTarget(find, account, kind, target);
            exists = await find.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        }

        await using (SqliteCommand upsert = Command(connection, transaction,
            """
            INSERT INTO sponsorships (kind, target, account, daily_limit, since) VALUES ($k, $t, $a, $l, $s)
            ON CONFLICT (kind, target, account) DO UPDATE SET daily_limit = $l
            """))
        {
            AddTarget(upsert, account, kind, target);
            upsert.Parameters.AddWithValue("$l", (object?)dailyLimit ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$s", _time.GetUtcNow().ToUnixTimeMilliseconds());

            await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return !exists;
    }

    public async ValueTask<bool> WithdrawAsync(string account, SponsoredKind kind, string target, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = Command(connection, null,
            "DELETE FROM sponsorships WHERE kind = $k AND target = $t AND account = $a");

        AddTarget(command, account, kind, target);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async ValueTask<IReadOnlyList<Sponsorship>> ListAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = Command(connection, null,
            """
            SELECT account, kind, target, daily_limit, CASE WHEN spent_day = $d THEN spent_on_day ELSE 0 END, spent_total, since
            FROM sponsorships ORDER BY kind, target, since
            """);

        command.Parameters.AddWithValue("$d", Today);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        List<Sponsorship> sponsorships = [];

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            sponsorships.Add(new Sponsorship(
                reader.GetString(0),
                Enum.Parse<SponsoredKind>(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));

        return sponsorships;
    }

    public async ValueTask<string?> ChargeAsync(SponsoredKind kind, string target, long amount, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        string today = Today;

        await using SqliteConnection  connection  = database.Open();
        await using SqliteTransaction transaction = connection.BeginTransaction();

        string? account;

        await using (SqliteCommand next = Command(connection, transaction,
            """
            SELECT s.account FROM sponsorships s JOIN credit_accounts c ON c.account = s.account
            WHERE s.kind = $k AND s.target = $t AND c.balance >= $n
              AND (s.daily_limit IS NULL OR CASE WHEN s.spent_day = $d THEN s.spent_on_day ELSE 0 END + $n <= s.daily_limit)
            ORDER BY s.charge_order, s.since
            LIMIT 1
            """))
        {
            next.Parameters.AddWithValue("$k", kind.ToString());
            next.Parameters.AddWithValue("$t", target);
            next.Parameters.AddWithValue("$n", amount);
            next.Parameters.AddWithValue("$d", today);

            account = await next.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (account is null) return null;

        await SqliteCreditLedger.WithdrawAsync(connection, transaction, account, amount, ct).ConfigureAwait(false);
        await SqliteCreditLedger.AppendAsync(connection, transaction, account, -amount, CreditEntryKind.Spent, null,
            kind == SponsoredKind.DiscordChannel ? $"Discord channel {target}" : $"API client {target}", ct).ConfigureAwait(false);

        await using (SqliteCommand record = Command(connection, transaction,
            """
            UPDATE sponsorships SET
                spent_on_day = CASE WHEN spent_day = $d THEN spent_on_day + $n ELSE $n END,
                spent_day    = $d,
                spent_total  = spent_total + $n,
                charge_order = (SELECT coalesce(max(charge_order), 0) + 1 FROM sponsorships)
            WHERE kind = $k AND target = $t AND account = $a
            """))
        {
            AddTarget(record, account, kind, target);
            record.Parameters.AddWithValue("$n", amount);
            record.Parameters.AddWithValue("$d", today);

            await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return account;
    }

    public async ValueTask<bool> CreateApiClientAsync(string id, string name, string keyHash, string createdBy, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = Command(connection, null,
            "INSERT INTO sponsored_api_clients (id, name, key_hash, created_by, created_at) VALUES ($i, $n, $h, $c, $t)");

        command.Parameters.AddWithValue("$i", id);
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$h", keyHash);
        command.Parameters.AddWithValue("$c", createdBy);
        command.Parameters.AddWithValue("$t", _time.GetUtcNow().ToUnixTimeMilliseconds());

        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
        {
            return false;
        }
    }

    public async ValueTask<SponsoredApiClient?> FindApiClientAsync(string id, CancellationToken ct) =>
        (await QueryClientsAsync("WHERE id = $p", id, ct).ConfigureAwait(false)).FirstOrDefault();

    public async ValueTask<SponsoredApiClient?> FindApiClientByKeyAsync(string key, CancellationToken ct) =>
        (await QueryClientsAsync("WHERE key_hash = $p", SponsoredApiClient.HashKey(key), ct).ConfigureAwait(false)).FirstOrDefault();

    public ValueTask<IReadOnlyList<SponsoredApiClient>> ListApiClientsAsync(CancellationToken ct) =>
        QueryClientsAsync("ORDER BY id", null, ct);

    private async ValueTask<IReadOnlyList<SponsoredApiClient>> QueryClientsAsync(string clause, string? parameter, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = Command(connection, null,
            $"SELECT id, name, created_by, created_at FROM sponsored_api_clients {clause}");

        if (parameter is not null) command.Parameters.AddWithValue("$p", parameter);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        List<SponsoredApiClient> clients = [];

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            clients.Add(new SponsoredApiClient(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));

        return clients;
    }

    private static void AddTarget(SqliteCommand command, string account, SponsoredKind kind, string target)
    {
        command.Parameters.AddWithValue("$k", kind.ToString());
        command.Parameters.AddWithValue("$t", target);
        command.Parameters.AddWithValue("$a", account);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        return command;
    }
}
