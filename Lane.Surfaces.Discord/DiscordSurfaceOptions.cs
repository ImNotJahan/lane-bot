namespace Lane.Surfaces.Discord;

public sealed class ChannelFilterOptions
{
    /// <summary>Channel ids Lane listens to. Empty means every channel she can see.</summary>
    public List<ulong> Include { get; set; } = [];

    /// <summary>Channel ids to ignore even when <see cref="Include"/> is empty.</summary>
    public List<ulong> Exclude { get; set; } = [];

    public bool Allows(ulong channelId)
    {
        if (Exclude.Contains(channelId)) return false;

        return Include.Count == 0 || Include.Contains(channelId);
    }
}

public sealed class DiscordSurfaceOptions
{
    /// <summary>
    /// Secret reference for this instance's bot token. Per instance, which is the whole
    /// point: two Discord bots are two tokens, and neither reads a global.
    /// </summary>
    public string TokenRef { get; set; } = "env:DISCORD_API_KEY";

    public ChannelFilterOptions Channels { get; set; } = new();

    /// <summary>Reply to direct messages as well as guild channels.</summary>
    public bool AllowDirectMessages { get; set; } = true;

    /// <summary>
    /// Other bots are remembered but not replied to. Two bots left talking to each other
    /// is an unbounded spend, and a loop nobody is reading.
    /// </summary>
    public bool RespondToBots { get; set; }

    /// <summary>
    /// Which conversations share memory. <c>{surface}</c>, <c>{guild}</c> and
    /// <c>{channel}</c> are substituted. Point two channels at one group and Lane carries
    /// what was said between them.
    /// </summary>
    public string MemoryGroupTemplate { get; set; } = "{surface}/{guild}/{channel}";

    /// <summary>Show the typing indicator while a turn is running.</summary>
    public bool ShowTyping { get; set; } = true;

    /// <summary>Thread replies to the message that prompted them.</summary>
    public bool ReplyInThread { get; set; } = true;

    public VoiceOptions Voice { get; set; } = new();
}

public sealed class VoiceOptions
{
    /// <summary>Sit in a voice channel and listen. Off unless audio is configured too.</summary>
    public bool AutoJoin { get; set; }

    /// <summary>Which channel to join. Unset means the first voice channel in the guild.</summary>
    public ulong? ChannelId { get; set; }
}
