using System.Text;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Serialization;
using Microsoft.Data.Sqlite;

namespace Lane.Memory.Sqlite;

/// <summary>
/// The durable log of everything said, in one table, ordered by an autoincrement sequence.
///
/// Kept apart from the memory handlers on purpose: handlers hold whatever shape the prompt
/// needs and are free to forget, while this stays the complete record — what the API's
/// history endpoint reads and what any future re-indexing would replay.
/// </summary>
public sealed class SqliteTranscriptStore(LaneDatabase database) : ITranscriptStore
{
    public async ValueTask<long> AppendAsync(LaneMessage message, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO messages
                (id, session_id, memory_group, surface_id, author_local_id, author_name,
                 global_user_id, role, kind, ts, external_id, content_json)
            VALUES ($id, $session, $group, $surface, $authorId, $authorName,
                    $globalUser, $role, $kind, $ts, $external, $content)
            RETURNING sequence
            """;

        command.Parameters.AddWithValue("$id",         message.Id.Value.ToString("n"));
        command.Parameters.AddWithValue("$session",    (object?)message.Session?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$group",      (object?)MemoryGroupOf(message) ?? DBNull.Value);
        command.Parameters.AddWithValue("$surface",    message.Author.Id.Surface.Value);
        command.Parameters.AddWithValue("$authorId",   message.Author.Id.LocalId);
        command.Parameters.AddWithValue("$authorName", message.Author.DisplayName);
        command.Parameters.AddWithValue("$globalUser", (object?)message.Author.GlobalUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$role",       (int)message.Role);
        command.Parameters.AddWithValue("$kind",       (int)message.Kind);
        command.Parameters.AddWithValue("$ts",         message.Timestamp.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$external",   (object?)message.ExternalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$content",    LaneJson.SerializeContent(message.Content));

        object? sequence = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return sequence is long value ? value : 0;
    }

    public async ValueTask<IReadOnlyList<LaneMessage>> ReadAsync(TranscriptQuery query, CancellationToken ct)
    {
        StringBuilder sql = new(
            """
            SELECT id, session_id, surface_id, author_local_id, author_name, global_user_id,
                   role, kind, ts, external_id, content_json, sequence
            FROM messages
            WHERE sequence > $after
            """);

        await using SqliteConnection connection = database.Open();
        await using SqliteCommand command = connection.CreateCommand();

        command.Parameters.AddWithValue("$after", query.AfterSequence);

        if (query.Session is not null)
        {
            sql.Append(" AND session_id = $session");
            command.Parameters.AddWithValue("$session", query.Session.Value);
        }

        if (query.MemoryGroup is not null)
        {
            sql.Append(" AND memory_group = $group");
            command.Parameters.AddWithValue("$group", query.MemoryGroup);
        }

        if (query.GlobalUserId is not null)
        {
            sql.Append(" AND global_user_id = $user");
            command.Parameters.AddWithValue("$user", query.GlobalUserId);
        }

        sql.Append(query.Descending ? " ORDER BY sequence DESC" : " ORDER BY sequence ASC");
        sql.Append(" LIMIT $limit");
        command.Parameters.AddWithValue("$limit", query.Limit);

        command.CommandText = sql.ToString();

        List<LaneMessage> messages = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false)) messages.Add(Read(reader));

        // Callers want oldest-first regardless of which end the query took them from.
        if (query.Descending) messages.Reverse();

        return messages;
    }

    private static LaneMessage Read(SqliteDataReader reader)
    {
        SessionId? session = null;
        if (!reader.IsDBNull(1)) SessionId.TryParse(reader.GetString(1), out session);

        Participant author = new(
            new ParticipantId(new SurfaceId(reader.GetString(2)), reader.GetString(3)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));

        return new LaneMessage
        {
            Id         = Guid.TryParseExact(reader.GetString(0), "n", out Guid id) ? new MessageId(id) : MessageId.New(),
            Session    = session,
            Author     = author with { IsLane = author.Id.LocalId == "lane" },
            Role       = (LaneRole)reader.GetInt32(6),
            Kind       = (MessageKind)reader.GetInt32(7),
            Timestamp  = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            ExternalId = reader.IsDBNull(9) ? null : reader.GetString(9),
            Content    = LaneJson.DeserializeContent(reader.GetString(10)),
            Sequence   = reader.GetInt64(11)
        };
    }

    /// <summary>
    /// The memory group is a property of the session, which a message does not carry.
    /// Storing the session's local key is close enough to make group queries useful while
    /// keeping the message model free of session configuration.
    /// </summary>
    private static string? MemoryGroupOf(LaneMessage message) =>
        message.Session is null ? null : $"{message.Session.Surface.Value}/{message.Session.LocalKey}";
}
