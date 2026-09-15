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

            CREATE TABLE IF NOT EXISTS node_identities (
                key_id     TEXT    PRIMARY KEY,
                algorithm  TEXT    NOT NULL,
                public_key TEXT    NOT NULL,
                nickname   TEXT    COLLATE NOCASE UNIQUE,
                node_name  TEXT,
                responses  INTEGER NOT NULL DEFAULT 0,
                first_seen INTEGER NOT NULL,
                last_seen  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS node_credentials (
                credential_id TEXT NOT NULL,
                key_id        TEXT NOT NULL REFERENCES node_identities(key_id),
                PRIMARY KEY (credential_id, key_id)
            );

            CREATE TABLE IF NOT EXISTS credit_accounts (
                account TEXT    PRIMARY KEY,
                balance INTEGER NOT NULL CHECK (balance >= 0)
            );

            CREATE TABLE IF NOT EXISTS credit_entries (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                account      TEXT    NOT NULL,
                amount       INTEGER NOT NULL,
                kind         TEXT    NOT NULL,
                counterparty TEXT,
                memo         TEXT,
                at           INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_credit_entries_account ON credit_entries(account, id);

            CREATE TABLE IF NOT EXISTS sponsorships (
                kind         TEXT    NOT NULL,
                target       TEXT    NOT NULL,
                account      TEXT    NOT NULL,
                daily_limit  INTEGER,
                spent_day    TEXT,
                spent_on_day INTEGER NOT NULL DEFAULT 0,
                spent_total  INTEGER NOT NULL DEFAULT 0,
                charge_order INTEGER NOT NULL DEFAULT 0,
                since        INTEGER NOT NULL,
                PRIMARY KEY (kind, target, account)
            );

            CREATE TABLE IF NOT EXISTS sponsored_api_clients (
                id         TEXT    PRIMARY KEY COLLATE NOCASE,
                name       TEXT    NOT NULL,
                key_hash   TEXT    NOT NULL UNIQUE,
                created_by TEXT    NOT NULL,
                created_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS forum_posts (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                author      TEXT    NOT NULL,
                title       TEXT    NOT NULL,
                description TEXT    NOT NULL,
                created_at  INTEGER NOT NULL,
                bumped_at   INTEGER NOT NULL,
                bump_order  INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_forum_posts_bump ON forum_posts(bump_order);

            CREATE TABLE IF NOT EXISTS forum_comments (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                post_id    INTEGER NOT NULL REFERENCES forum_posts(id),
                author     TEXT    NOT NULL,
                body       TEXT    NOT NULL,
                created_at INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_forum_comments_post ON forum_comments(post_id, id);
            """;

        command.ExecuteNonQuery();
    }
}
