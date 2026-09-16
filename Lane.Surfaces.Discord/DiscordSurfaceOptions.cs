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

    /// <summary>Ignore messages from people until they run /opt-in. Bots are exempt.</summary>
    public bool RequireOptIn { get; set; } = true;

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

    public StatusOptions Status { get; set; } = new();
}

public sealed class StatusOptions
{
    /// <summary>
    /// Show Lane's status on this bot — her face for now, and whatever else is given a slot
    /// on the line later. Per instance, like everything else here: two bots can look
    /// different, or one of them can say nothing about itself at all.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

public sealed class VoiceOptions
{
    /// <summary>
    /// Let her join voice channels when she is asked to. Which one and when is hers to
    /// decide through <c>join_voice_channel</c>; this only says whether the ability exists,
    /// and it does nothing unless audio is configured too.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Leave a voice channel after this long with nothing heard in it and nothing said.
    /// Sitting silently in an empty channel is a live socket and an open ear nobody asked
    /// for.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
