namespace Lane.Core.Forum;

public enum ForumSort { Bumped, Newest }

/// <param name="Author">Key id of the node identity that wrote it.</param>
/// <param name="BumpedAt">When it was created or last commented on.</param>
public sealed record ForumPost(
    long           Id,
    string         Author,
    string         Title,
    string         Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset BumpedAt,
    int            Comments);

/// <param name="Author">Key id of the node identity that wrote it.</param>
public sealed record ForumComment(long Id, long PostId, string Author, string Body, DateTimeOffset CreatedAt);

/// <summary>Posts and their comments, written by node identities.</summary>
public interface IForum
{
    ValueTask<ForumPost> CreatePostAsync(string author, string title, string description, CancellationToken ct);

    /// <summary>Adds a comment and bumps the post. Null when the post does not exist.</summary>
    ValueTask<ForumComment?> CommentAsync(long postId, string author, string body, CancellationToken ct);

    /// <summary>Most recently bumped or created first.</summary>
    ValueTask<IReadOnlyList<ForumPost>> ListPostsAsync(ForumSort sort, int limit, CancellationToken ct);

    ValueTask<ForumPost?> FindPostAsync(long id, CancellationToken ct);

    /// <summary>Oldest first.</summary>
    ValueTask<IReadOnlyList<ForumComment>> CommentsAsync(long postId, CancellationToken ct);
}
