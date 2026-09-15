using Lane.Core.Models;
using Lane.Core.Nodes;
using Microsoft.Data.Sqlite;

namespace Lane.Memory.Sqlite;

public sealed class SqliteNodeDirectory(LaneDatabase database) : INodeDirectory
{
    private const string Columns =
        "i.key_id, i.algorithm, i.public_key, i.nickname, i.node_name, i.responses, i.first_seen, i.last_seen";

    private const int SqliteConstraint = 19;

    public async ValueTask SeenAsync(NodeIdentity identity, string? nodeName, string? credentialId, CancellationToken ct)
    {
        await using SqliteConnection  connection  = database.Open();
        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using (SqliteCommand upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO node_identities (key_id, algorithm, public_key, node_name, first_seen, last_seen)
                VALUES ($k, $a, $p, $n, $t, $t)
                ON CONFLICT (key_id) DO UPDATE SET node_name = coalesce($n, node_name), last_seen = $t
                """;

            AddIdentity(upsert, identity);
            upsert.Parameters.AddWithValue("$n", (object?)nodeName ?? DBNull.Value);

            await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (credentialId is not null)
        {
            await using SqliteCommand link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT OR IGNORE INTO node_credentials (credential_id, key_id) VALUES ($c, $k)";
            link.Parameters.AddWithValue("$c", credentialId);
            link.Parameters.AddWithValue("$k", identity.KeyId);

            await link.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask RecordResponseAsync(NodeIdentity identity, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO node_identities (key_id, algorithm, public_key, responses, first_seen, last_seen)
            VALUES ($k, $a, $p, 1, $t, $t)
            ON CONFLICT (key_id) DO UPDATE SET responses = responses + 1, last_seen = $t
            """;

        AddIdentity(command, identity);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<NodeIdentityRecord?> FindAsync(string keyId, CancellationToken ct)
    {
        IReadOnlyList<NodeIdentityRecord> found = await QueryAsync(
            $"SELECT {Columns} FROM node_identities i WHERE i.key_id = $k", ("$k", keyId), ct).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    public ValueTask<IReadOnlyList<NodeIdentityRecord>> ListAsync(CancellationToken ct) =>
        QueryAsync($"SELECT {Columns} FROM node_identities i ORDER BY i.responses DESC, i.first_seen ASC", null, ct);

    public ValueTask<IReadOnlyList<NodeIdentityRecord>> FindByCredentialAsync(string credentialId, CancellationToken ct) =>
        QueryAsync(
            $"SELECT {Columns} FROM node_identities i JOIN node_credentials c ON c.key_id = i.key_id WHERE c.credential_id = $c",
            ("$c", credentialId), ct);

    public async ValueTask LinkCredentialAsync(string keyId, string credentialId, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = connection.CreateCommand();

        command.CommandText =
            """
            INSERT OR IGNORE INTO node_credentials (credential_id, key_id)
            SELECT $c, key_id FROM node_identities WHERE key_id = $k
            """;
        command.Parameters.AddWithValue("$c", credentialId);
        command.Parameters.AddWithValue("$k", keyId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<string>> CredentialIdsAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = connection.CreateCommand();

        command.CommandText = "SELECT DISTINCT credential_id FROM node_credentials";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        List<string> ids = [];

        while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(reader.GetString(0));

        return ids;
    }

    public async ValueTask<bool> SetNicknameAsync(string keyId, string? nickname, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = connection.CreateCommand();

        command.CommandText = "UPDATE node_identities SET nickname = $n WHERE key_id = $k";
        command.Parameters.AddWithValue("$n", (object?)nickname ?? DBNull.Value);
        command.Parameters.AddWithValue("$k", keyId);

        try
        {
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint && nickname is not null)
        {
            throw new NicknameTakenException(nickname);
        }
    }

    private async ValueTask<IReadOnlyList<NodeIdentityRecord>> QueryAsync(
        string sql, (string Name, string Value)? parameter, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = connection.CreateCommand();

        command.CommandText = sql;

        if (parameter is { } p) command.Parameters.AddWithValue(p.Name, p.Value);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        List<NodeIdentityRecord> records = [];

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            records.Add(new NodeIdentityRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7))));

        return records;
    }

    private static void AddIdentity(SqliteCommand command, NodeIdentity identity)
    {
        command.Parameters.AddWithValue("$k", identity.KeyId);
        command.Parameters.AddWithValue("$a", identity.Algorithm);
        command.Parameters.AddWithValue("$p", identity.PublicKey);
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
