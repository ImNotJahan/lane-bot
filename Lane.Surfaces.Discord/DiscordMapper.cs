using Lane.Core.Identity;
using Lane.Core.Sessions;

namespace Lane.Surfaces.Discord;

/// <summary>
/// The parts of the Discord surface that are pure text and identity work.
///
/// Kept apart from the gateway glue so they can be tested without a live connection —
/// mention resolution and message splitting are exactly the things that break quietly and
/// only in production otherwise.
/// </summary>
internal static class DiscordMapper
{
    /// <summary>Discord rejects anything longer than this.</summary>
    public const int MessageLimit = 2000;

    public static string BuildMemoryGroup(string template, string surface, ulong? guildId, ulong channelId) =>
        template
            .Replace("{surface}", surface, StringComparison.Ordinal)
            .Replace("{guild}", guildId?.ToString() ?? "dm", StringComparison.Ordinal)
            .Replace("{channel}", channelId.ToString(), StringComparison.Ordinal);

    public static SessionDescriptor Describe(
        SurfaceId surface,
        ulong channelId,
        ulong? guildId,
        string channelName,
        string memoryGroupTemplate,
        IReadOnlyList<Participant> participants)
    {
        bool isDirect = guildId is null;

        return new SessionDescriptor
        {
            Id          = new SessionId(surface, SessionKind.Text, channelId.ToString()),
            DisplayName = isDirect ? $"DM: {channelName}" : $"#{channelName}",
            MemoryGroup = BuildMemoryGroup(memoryGroupTemplate, surface.Value, guildId, channelId),

            // Images are accepted; voice arrives with the audio work.
            Capabilities = ChannelCapabilities.Text | ChannelCapabilities.Images | ChannelCapabilities.Typing,

            IsDirect          = isDirect,
            KnownParticipants = participants
        };
    }

    /// <summary>
    /// Turns raw mention markup into readable names.
    ///
    /// Without this the model sees <c>&lt;@2313…&gt;</c> and has no idea it was addressed,
    /// or by whom.
    /// </summary>
    public static string ResolveMentions(
        string content,
        IReadOnlyDictionary<ulong, string> users,
        IReadOnlyDictionary<ulong, string> roles)
    {
        if (string.IsNullOrEmpty(content)) return content;

        foreach ((ulong id, string name) in users)
        {
            content = content.Replace($"<@{id}>", $"@{name}", StringComparison.Ordinal)
                             .Replace($"<@!{id}>", $"@{name}", StringComparison.Ordinal);
        }

        foreach ((ulong id, string name) in roles)
            content = content.Replace($"<@&{id}>", $"@{name}", StringComparison.Ordinal);

        return content;
    }

    /// <summary>Adds the quoted message a reply was aimed at, so the reference is not lost.</summary>
    public static string AppendReplyContext(string content, string? replyAuthor, string? replyContent)
    {
        if (string.IsNullOrWhiteSpace(replyAuthor) || string.IsNullOrWhiteSpace(replyContent)) return content;

        string quoted = replyContent.Length > 300 ? replyContent[..300] + "…" : replyContent;

        return $"{content}\n\n(replying to {replyAuthor}: \"{quoted}\")";
    }

    /// <summary>
    /// Breaks a reply into pieces Discord will accept, preferring paragraph then line then
    /// word boundaries. A reply over the limit is otherwise rejected outright, which loses
    /// the whole turn rather than trimming it.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int limit = MessageLimit)
    {
        if (string.IsNullOrEmpty(text)) return [];
        if (text.Length <= limit) return [text];

        List<string> parts = [];
        ReadOnlySpan<char> remaining = text.AsSpan();

        while (remaining.Length > limit)
        {
            int cut = FindBreak(remaining, limit);

            parts.Add(remaining[..cut].ToString().TrimEnd());

            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0) parts.Add(remaining.ToString());

        return parts;
    }

    private static int FindBreak(ReadOnlySpan<char> text, int limit)
    {
        ReadOnlySpan<char> window = text[..limit];

        int paragraph = window.LastIndexOf("\n\n");
        if (paragraph > limit / 4) return paragraph;

        int line = window.LastIndexOf('\n');
        if (line > limit / 4) return line;

        int space = window.LastIndexOf(' ');
        if (space > limit / 4) return space;

        // A single unbroken run longer than the limit — a URL or a wall of code.
        return limit;
    }

    /// <summary>
    /// The display name a person should be known by: their chosen global name where they
    /// have one, otherwise their handle.
    /// </summary>
    public static string DisplayNameOf(string? globalName, string username) =>
        string.IsNullOrWhiteSpace(globalName) ? username : globalName;
}
