using Lane.Core.Forum;
using Microsoft.Data.Sqlite;

namespace Lane.Memory.Sqlite;

public sealed class SqliteForum(LaneDatabase database, TimeProvider? time = null) : IForum
{
    private const string PostColumns =
        """
        p.id, p.author, p.title, p.description, p.created_at, p.bumped_at,
        (SELECT count(*) FROM forum_comments c WHERE c.post_id = p.id)
        """;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async ValueTask<ForumPost> CreatePostAsync(string author, string title, string description, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();

        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO forum_posts (author, title, description, created_at, bumped_at, bump_order)
            VALUES ($a, $t, $d, $n, $n, (SELECT coalesce(max(bump_order), 0) + 1 FROM forum_posts))
            RETURNING id
            """;
        command.Parameters.AddWithValue("$a", author);
        command.Parameters.AddWithValue("$t", title);
        command.Parameters.AddWithValue("$d", description);
        command.Parameters.AddWithValue("$n", now);

        long id = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(now);

        return new ForumPost(id, author, title, description, at, at, 0);
    }

    public async ValueTask<ForumComment?> CommentAsync(long postId, string author, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();

        await using SqliteConnection  connection  = database.Open();
        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using (SqliteCommand bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText =
                """
                UPDATE forum_posts SET bumped_at = $n, bump_order = (SELECT coalesce(max(bump_order), 0) + 1 FROM forum_posts)
                WHERE id = $p
                """;
            bump.Parameters.AddWithValue("$n", now);
            bump.Parameters.AddWithValue("$p", postId);

            if (await bump.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0) return null;
        }

        long id;

        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO forum_comments (post_id, author, body, created_at) VALUES ($p, $a, $b, $n) RETURNING id";
            insert.Parameters.AddWithValue("$p", postId);
            insert.Parameters.AddWithValue("$a", author);
            insert.Parameters.AddWithValue("$b", body);
            insert.Parameters.AddWithValue("$n", now);

            id = (long)(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new ForumComment(id, postId, author, body, DateTimeOffset.FromUnixTimeMilliseconds(now));
    }

    public async ValueTask<IReadOnlyList<ForumPost>> ListPostsAsync(ForumSort sort, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        string order = sort == ForumSort.Newest ? "p.id DESC" : "p.bump_order DESC";

        return await QueryPostsAsync($"ORDER BY {order} LIMIT $l", command => command.Parameters.AddWithValue("$l", limit), ct)
            .ConfigureAwait(false);
    }

    public async ValueTask<ForumPost?> FindPostAsync(long id, CancellationToken ct) =>
        (await QueryPostsAsync("WHERE p.id = $i", command => command.Parameters.AddWithValue("$i", id), ct).ConfigureAwait(false))
        .FirstOrDefault();

    public async ValueTask<IReadOnlyList<ForumComment>> CommentsAsync(long postId, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = connection.CreateCommand();

        command.CommandText = "SELECT id, post_id, author, body, created_at FROM forum_comments WHERE post_id = $p ORDER BY id";
        command.Parameters.AddWithValue("$p", postId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        List<ForumComment> comments = [];

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            comments.Add(new ForumComment(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));

        return comments;
    }

    private async ValueTask<IReadOnlyList<ForumPost>> QueryPostsAsync(string clause, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using SqliteConnection connection = database.Open();
        await using SqliteCommand    command    = connection.CreateCommand();

        command.CommandText = $"SELECT {PostColumns} FROM forum_posts p {clause}";
        bind(command);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        List<ForumPost> posts = [];

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            posts.Add(new ForumPost(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                reader.GetInt32(6)));

        return posts;
    }
}
