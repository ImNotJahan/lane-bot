using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Lane.Memory.Sqlite;

public sealed class SqliteOptions
{
    /// <summary>Database path. Relative paths resolve against the assembly directory.</summary>
    public string Path { get; set; } = "lane.db";

    /// <summary>Use an in-memory database. Tests only — nothing survives the process.</summary>
    public bool InMemory { get; set; }
}

/// <summary>
/// Owns the connection string and the schema.
///
/// SQLite replaces v2's single <c>data.json</c>, which was rewritten whole by
/// <c>File.WriteAllText</c> from two threads with no lock. Beyond not corrupting itself,
/// this buys atomic multi-key writes, a queryable transcript for the API's history
/// endpoint, and cheap ordered reads for the recent window.
/// </summary>
public sealed class LaneDatabase
{
    private readonly SqliteConnection? _keepAlive;

    public LaneDatabase(SqliteOptions options, ILogger<LaneDatabase> log)
    {
        if (options.InMemory)
        {
            // A shared-cache in-memory database lives only as long as one connection is
            // open, so the database holds one for its lifetime.
            ConnectionString = $"Data Source=lane-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";

            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();
        }
        else
        {
            string path = System.IO.Path.IsPathRooted(options.Path)
                ? options.Path
                : System.IO.Path.Combine(AppContext.BaseDirectory, options.Path);

            string? directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            ConnectionString = $"Data Source={path}";

            log.LogInformation("Memory database at {Path}", path);
        }

        Initialise();
    }

    public string ConnectionString { get; }

    public SqliteConnection Open()
    {
        SqliteConnection connection = new(ConnectionString);
        connection.Open();

        return connection;
    }

    private void Initialise()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();

        // WAL keeps a reader from blocking the writer, which matters once several session
        // pumps are committing turns at the same time.
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS handler_state (
                handler_id     TEXT    NOT NULL,
                scope_key      TEXT    NOT NULL,
                schema_version INTEGER NOT NULL,
                state_json     TEXT    NOT NULL,
                updated_at     INTEGER NOT NULL,
                PRIMARY KEY (handler_id, scope_key)
            );

            CREATE TABLE IF NOT EXISTS messages (
                sequence        INTEGER PRIMARY KEY AUTOINCREMENT,
                id              TEXT    NOT NULL UNIQUE,
                session_id      TEXT,
                memory_group    TEXT,
                surface_id      TEXT,
                author_local_id TEXT,
                author_name     TEXT,
                global_user_id  TEXT,
                role            INTEGER NOT NULL,
                kind            INTEGER NOT NULL,
                ts              INTEGER NOT NULL,
                external_id     TEXT,
                content_json    TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_messages_group_seq ON messages(memory_group, sequence);
            CREATE INDEX IF NOT EXISTS ix_messages_user_seq  ON messages(global_user_id, sequence);
            CREATE INDEX IF NOT EXISTS ix_messages_session    ON messages(session_id, sequence);

            CREATE TABLE IF NOT EXISTS kv (
                scope_key  TEXT NOT NULL,
                key        TEXT NOT NULL,
                value_json TEXT NOT NULL,
                PRIMARY KEY (scope_key, key)
            );
            """;

        command.ExecuteNonQuery();
    }
}
