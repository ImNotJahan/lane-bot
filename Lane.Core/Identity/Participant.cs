namespace Lane.Core.Identity;

/// <summary>A speaker as one surface knows them. Scoped to the surface, because a Discord
/// snowflake and an API client id are only unique within their own surface.</summary>
public readonly record struct ParticipantId(SurfaceId Surface, string LocalId)
{
    public override string ToString() => $"{Surface.Value}:{LocalId}";
}

/// <summary>
/// Someone taking part in a conversation, including Lane herself.
/// </summary>
/// <param name="GlobalUserId">
/// Links the same human across surfaces — the Discord account and the API client that are
/// both Jahan. Supplied by <see cref="IIdentityResolver"/> from the configured identity map;
/// null means unlinked. This is the only thing that makes <c>MemoryScope.User</c> meaningful.
/// </param>
public sealed record Participant(
    ParticipantId Id,
    string        DisplayName,
    string?       GlobalUserId = null,
    bool          IsLane       = false)
{
    /// <summary>The author of Lane's own messages on a given surface.</summary>
    public static Participant Lane(SurfaceId surface) =>
        new(new ParticipantId(surface, "lane"), "Lane", GlobalUserId: "lane", IsLane: true);

    /// <summary>The author of thoughts and other messages that belong to no surface.</summary>
    public static Participant LaneInternal { get; } = Lane(new SurfaceId("internal"));

    /// <summary>Best available stable key for this participant — the global link if there is one.</summary>
    public string StableKey => GlobalUserId ?? Id.ToString();

    public override string ToString() => $"{DisplayName} ({Id})";
}

/// <summary>
/// Resolves surface-local identities to a cross-surface <c>GlobalUserId</c>.
/// </summary>
public interface IIdentityResolver
{
    /// <summary>Returns the participant with <c>GlobalUserId</c> populated where a mapping exists.</summary>
    Participant Resolve(ParticipantId id, string displayName);

    /// <summary>
    /// Whether a global id already belongs to somebody, configured or linked at runtime.
    /// Asked before minting a new one: a mint that collided with a configured person would
    /// silently pour a stranger's memory into theirs.
    /// </summary>
    bool IsPerson(string globalUserId);
}
