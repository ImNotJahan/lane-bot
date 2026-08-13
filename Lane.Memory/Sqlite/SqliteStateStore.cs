using System.Text.Json.Nodes;
using Lane.Core.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Lane.Memory.Sqlite;

public sealed class SqliteStateStore(LaneDatabase database, ILogger<SqliteStateStore> log) : IStateStore
{
    public async ValueTask<JsonNode?> LoadAsync(string handlerId, ScopeKey key, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT state_json FROM handler_state WHERE handler_id = $h AND scope_key = $k";
        command.Parameters.AddWithValue("$h", handlerId);
        command.Parameters.AddWithValue("$k", key.Value);

        object? result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (result is not string json) return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            // Corrupt state is treated as absent. Refusing to start over a bad row would
            // make one damaged handler take down the whole process.
            log.LogWarning(ex, "Unparseable state for {Handler} at {Key}; treating as empty", handlerId, key);
            return null;
        }
    }

    public async ValueTask SaveAsync(
        string handlerId, ScopeKey key, JsonNode state, int schemaVersion, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO handler_state (handler_id, scope_key, schema_version, state_json, updated_at)
            VALUES ($h, $k, $v, $s, $t)
            ON CONFLICT (handler_id, scope_key)
            DO UPDATE SET schema_version = $v, state_json = $s, updated_at = $t
            """;

        command.Parameters.AddWithValue("$h", handlerId);
        command.Parameters.AddWithValue("$k", key.Value);
        command.Parameters.AddWithValue("$v", schemaVersion);
        command.Parameters.AddWithValue("$s", state.ToJsonString());
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(string handlerId, ScopeKey key, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "DELETE FROM handler_state WHERE handler_id = $h AND scope_key = $k";
        command.Parameters.AddWithValue("$h", handlerId);
        command.Parameters.AddWithValue("$k", key.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
