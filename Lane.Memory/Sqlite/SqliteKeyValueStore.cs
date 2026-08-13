using System.Text.Json;
using Lane.Core.Memory;
using Lane.Core.Serialization;
using Microsoft.Data.Sqlite;

namespace Lane.Memory.Sqlite;

/// <summary>
/// Small scoped values that do not deserve a handler of their own — book positions,
/// schedules, per-session flags. Replaces the loose keys v2 kept alongside memory in
/// <c>data.json</c>.
/// </summary>
public sealed class SqliteKeyValueStore(LaneDatabase database) : IKeyValueStore
{
    public async ValueTask<T?> GetAsync<T>(ScopeKey scope, string key, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT value_json FROM kv WHERE scope_key = $s AND key = $k";
        command.Parameters.AddWithValue("$s", scope.Value);
        command.Parameters.AddWithValue("$k", key);

        object? result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (result is not string json) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, LaneJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public async ValueTask SetAsync<T>(ScopeKey scope, string key, T value, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO kv (scope_key, key, value_json) VALUES ($s, $k, $v)
            ON CONFLICT (scope_key, key) DO UPDATE SET value_json = $v
            """;

        command.Parameters.AddWithValue("$s", scope.Value);
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", JsonSerializer.Serialize(value, LaneJson.Options));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask RemoveAsync(ScopeKey scope, string key, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "DELETE FROM kv WHERE scope_key = $s AND key = $k";
        command.Parameters.AddWithValue("$s", scope.Value);
        command.Parameters.AddWithValue("$k", key);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
