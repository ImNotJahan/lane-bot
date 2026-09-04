using System.Collections.Concurrent;
using Lane.Audio;
using Lane.Core.Energy;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Messages;
using Lane.Core.Presence;
using Lane.Core.Sessions;
using Lane.Core.Surfaces;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using Lane.Surfaces.Discord.Presence;
using Lane.Surfaces.Discord.Voice;

namespace Lane.Surfaces.Discord;

/// <summary>
/// One Discord bot.
///
/// Everything that made v2's Discord body the centre of the harness is gone: it owns no
/// model, no memory, no ear and no mouth, and it decides nothing about whether or how Lane
/// replies. It turns gateway events into <c>InboundEvent</c>s and attaches a channel the
/// kernel can write back through — the same contract the terminal surface has, which is
/// what lets both run in one process instead of being an either/or at startup.
///
/// Two of these can run side by side. Nothing here is static, and the token, the channel
/// filter and the memory grouping all come from this instance's own options.
/// </summary>
public sealed class DiscordSurface : ISurface
{
    private readonly GatewayClient          _client;
    private readonly DiscordSurfaceOptions  _options;
    private readonly IAgentKernel           _kernel;
    private readonly ISessionRegistry       _sessions;
    private readonly IIdentityResolver      _identity;
    private readonly IEventBus              _bus;
    private readonly AudioRouter?           _router;
    private readonly ILogger<DiscordSurface> _log;

    /// <summary>Channels Lane has already attached to, so a second message does not attach twice.</summary>
    private readonly ConcurrentDictionary<ulong, IDisposable> _attached = new();

    private readonly ConcurrentDictionary<ulong, SessionId> _openSessions = new();

    private readonly ConcurrentDictionary<ulong, DiscordVoiceConnection> _voiceConnections = new();

    /// <summary>What this bot shows about itself, composed from one slot per source.</summary>
    private readonly DiscordStatusPublisher _status;

    private IDisposable? _presence;

    private IDisposable? _energy;

    private CancellationTokenSource? _lifetime;

    public DiscordSurface(
        SurfaceId id,
        string token,
        DiscordSurfaceOptions options,
        IAgentKernel kernel,
        ISessionRegistry sessions,
        IIdentityResolver identity,
        IEventBus bus,
        ILogger<DiscordSurface> log,
        AudioRouter? router = null)
    {
        _router   = router;
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        Id        = id;
        _options  = options;
        _kernel   = kernel;
        _sessions = sessions;
        _identity = identity;
        _bus      = bus;
        _log      = log;

        _client = new GatewayClient(
            new BotToken(token),
            new GatewayClientConfiguration
            {
                Intents = GatewayIntents.AllNonPrivileged | GatewayIntents.MessageContent
            });

        _status = new DiscordStatusPublisher(WriteStatusAsync, log);

        _client.MessageCreate    += OnMessageCreate;
        _client.Ready            += OnReady;
        _client.Disconnect       += OnDisconnect;
        _client.GuildCreate      += OnGuildCreate;
        _client.VoiceStateUpdate += OnVoiceStateUpdate;
    }

    public SurfaceId Id { get; }

    public async Task StartAsync(CancellationToken ct)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        StartStatus(_lifetime.Token);

        await _client.StartAsync(cancellationToken: ct).ConfigureAwait(false);

        _log.LogInformation("Discord surface {Surface} connecting", Id);
    }

    /// <summary>
    /// Wires whatever Lane shows about herself to the one line Discord gives a bot.
    ///
    /// Her face is the first thing on it, read off the event bus like every other observer —
    /// the surface owns none of this and is told nothing directly. A mood she reached in
    /// another conversation, on another surface or in her own monologue is still her mood,
    /// so this deliberately does not filter by session: one person, one face.
    /// </summary>
    private void StartStatus(CancellationToken ct)
    {
        if (!_options.Status.Enabled) return;

        _status.Start(ct);

        _presence = _bus.Subscribe<PresenceChanged>(
            change => _status.Set(DiscordStatusSlot.Face, change.Emoticon));

        // Null clears the slot, and the line reports whether it actually changed — so a
        // heartbeat every ten minutes that finds her still rested costs no gateway write.
        _energy = _bus.Subscribe<EnergyChanged>(change => _status.Set(DiscordStatusSlot.Sleep,
            change.State.Asleep                      ? "zzz"
          : change.State.Tier == EnergyTier.Weary    ? "worn out"
          : change.State.Tier == EnergyTier.Tired    ? "tired"
          :                                            null));
    }

    /// <summary>Sends the composed line to the gateway. An empty line clears the status.</summary>
    private async Task WriteStatusAsync(string line, CancellationToken ct)
    {
        PresenceProperties presence = new(UserStatusType.Online);

        if (line.Length > 0)
            presence = presence.WithActivities(
                [new UserActivityProperties("Custom Status", UserActivityType.Custom).WithState(line)]);

        await _client.UpdatePresenceAsync(presence, cancellationToken: ct).ConfigureAwait(false);
    }

    private ValueTask OnReady(ReadyEventArgs args)
    {
        _log.LogInformation("Discord surface {Surface} ready as {User}", Id, args.User.Username);

        _bus.Publish(new SurfaceStateChanged(Id, Connected: true, args.User.Username));

        // Presence belongs to the gateway session, so a reconnect starts her blank-faced
        // unless the line is sent again.
        _status.Refresh();

        return default;
    }

    private ValueTask OnDisconnect(DisconnectEventArgs args)
    {
        _log.LogWarning("Discord surface {Surface} disconnected", Id);

        _bus.Publish(new SurfaceStateChanged(Id, Connected: false));

        return default;
    }

    /// <summary>
    /// Joins a voice channel once the guild is known. Only when audio is configured — the
    /// surface is text-only otherwise, and says so by simply not connecting.
    /// </summary>
    private ValueTask OnGuildCreate(GuildCreateEventArgs args)
    {
        if (_router is null || !_options.Voice.AutoJoin) return default;
        if (args.Guild is not { } guild) return default;

        _ = Task.Run(async () =>
        {
            try
            {
                VoiceGuildChannel? channel = _options.Voice.ChannelId is { } wanted
                    ? guild.Channels.Values.OfType<VoiceGuildChannel>().FirstOrDefault(c => c.Id == wanted)
                    : guild.Channels.Values.OfType<VoiceGuildChannel>().FirstOrDefault();

                if (channel is null)
                {
                    _log.LogWarning("No voice channel to join in {Guild}", guild.Name);
                    return;
                }

                DiscordVoiceConnection connection = new(
                    Id, _client, _sessions, _router, _identity, _options, _log);

                if (!_voiceConnections.TryAdd(guild.Id, connection))
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                await connection.JoinAsync(guild.Id, channel.Id, channel.Name,
                    _lifetime?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Voice failing must not cost the text channels.
                _log.LogError(ex, "Could not join voice in {Guild}", guild.Name);
            }
        });

        return default;
    }

    /// <summary>Stops listening to someone once they leave the channel.</summary>
    private ValueTask OnVoiceStateUpdate(VoiceState state)
    {
        if (state.UserId == _client.Id) return default;
        if (state.ChannelId is not null) return default;      // moved, not left

        foreach (DiscordVoiceConnection connection in _voiceConnections.Values)
            _ = connection.ForgetAsync(state.UserId);

        return default;
    }

    private ValueTask OnMessageCreate(Message message)
    {
        // The gateway dispatch must return immediately; anything slower stalls every other
        // event on the socket, including the ones for other channels.
        _ = Task.Run(() => IngestAsync(message));

        return default;
    }

    private async Task IngestAsync(Message message)
    {
        try
        {
            if (message.Author.Id == _client.Id) return;                       // her own words
            if (message.Author.IsBot && !_options.RespondToBots) return;

            bool isDirect = message.GuildId is null;

            if (isDirect && !_options.AllowDirectMessages) return;
            if (!isDirect && !_options.Channels.Allows(message.ChannelId)) return;

            Participant author = ToParticipant(message.Author);

            SessionDescriptor descriptor = DiscordMapper.Describe(
                Id,
                message.ChannelId,
                message.GuildId,
                ChannelNameOf(message),
                _options.MemoryGroupTemplate,
                [author]);

            _sessions.GetOrCreate(descriptor);
            _openSessions[message.ChannelId] = descriptor.Id;

            Attach(descriptor.Id, message.ChannelId);

            LaneMessage inbound = LaneMessage.User(
                descriptor.Id,
                author,
                BuildContent(message, descriptor.Id, author),
                message.CreatedAt,
                message.Id.ToString());

            await _kernel.SubmitAsync(new InboundEvent
            {
                Session    = descriptor.Id,
                Author     = author,
                Message    = inbound,
                ExternalId = message.Id.ToString(),

                // Other bots are remembered but never answered, so two of them cannot get
                // stuck talking to each other.
                RequiresResponse = !message.Author.IsBot
            }, _lifetime?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Failed to ingest a Discord message in {Channel}", message.ChannelId);
        }
    }

    private IReadOnlyList<ContentPart> BuildContent(Message message, SessionId session, Participant author)
    {
        Dictionary<ulong, string> users = [];

        foreach (User mentioned in message.MentionedUsers)
            users[mentioned.Id] = DiscordMapper.DisplayNameOf(mentioned.GlobalName, mentioned.Username);

        // Lane's own id resolves to her name rather than a number, so being addressed reads
        // as being addressed.
        users.TryAdd(_client.Id, "Lane");

        Dictionary<ulong, string> roles = [];

        foreach (ulong roleId in message.MentionedRoleIds)
            roles[roleId] = RoleNameOf(message, roleId);

        string text = DiscordMapper.ResolveMentions(message.Content, users, roles);

        IReadOnlyList<EmbedView> embeds = ToViews(message.Embeds);

        // An embed is often the whole message — a posted link, a bot's answer — so the card
        // is folded into the text rather than left as structure nobody reads.
        if (DiscordMapper.RenderEmbeds(embeds) is { Length: > 0 } cards)
            text = string.IsNullOrWhiteSpace(text) ? cards : $"{text}\n\n{cards}";

        text = DiscordMapper.AppendReplyContext(
            text,
            message.ReferencedMessage is { } replied
                ? DiscordMapper.DisplayNameOf(replied.Author.GlobalName, replied.Author.Username)
                : null,
            message.ReferencedMessage is { } quoted ? WithEmbeds(quoted.Content, quoted.Embeds) : null);

        List<ContentPart> parts = [];

        if (!string.IsNullOrWhiteSpace(text)) parts.Add(new TextPart(text));

        foreach (Attachment attachment in message.Attachments)
        {
            if (attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) continue;

            parts.Add(new ImagePart(new Uri(attachment.Url), null, attachment.ContentType));
        }

        foreach (EmbedView embed in embeds.Take(DiscordMapper.MaxEmbeds))
        {
            if (DiscordMapper.EmbedImage(embed) is not { } image) continue;

            parts.Add(new ImagePart(image.Url, null, image.MediaType));
        }

        // An image with no caption is still something worth reacting to.
        if (parts.Count == 0) parts.Add(new TextPart("(no text)"));

        return parts;
    }

    /// <summary>Drops the gateway's layout detail, keeping what a reader would take from the card.</summary>
    private static IReadOnlyList<EmbedView> ToViews(IReadOnlyList<Embed>? embeds)
    {
        if (embeds is not { Count: > 0 }) return [];

        return [.. embeds.Select(embed => new EmbedView
        {
            Title        = embed.Title,
            Url          = embed.Url,
            Description  = embed.Description,
            AuthorName   = embed.Author?.Name,
            AuthorUrl    = embed.Author?.Url,
            Provider     = embed.Provider?.Name,
            Footer       = embed.Footer?.Text,
            ImageUrl     = embed.Image?.Url,
            ThumbnailUrl = embed.Thumbnail?.Url,
            VideoUrl     = embed.Video?.Url,

            Fields = embed.Fields is { } fields
                ? [.. fields.Select(field => new EmbedFieldView(field.Name, field.Value))]
                : []
        })];
    }

    /// <summary>A message as one string, so a quoted card is not quoted as nothing.</summary>
    private static string WithEmbeds(string? content, IReadOnlyList<Embed>? embeds)
    {
        string cards = DiscordMapper.RenderEmbeds(ToViews(embeds));

        if (cards.Length == 0) return content ?? "";

        return string.IsNullOrWhiteSpace(content) ? cards : $"{content}\n\n{cards}";
    }

    private void Attach(SessionId session, ulong channelId)
    {
        _attached.GetOrAdd(channelId, _ =>
            _sessions.Attach(new DiscordTextChannel(session, _client.Rest, channelId, _options, _log)));
    }

    private Participant ToParticipant(User user) => _identity.Resolve(
        new ParticipantId(Id, user.Id.ToString()),
        DiscordMapper.DisplayNameOf(user.GlobalName, user.Username));

    private static string ChannelNameOf(Message message)
    {
        if (message.Channel is TextGuildChannel guild && !string.IsNullOrWhiteSpace(guild.Name))
            return guild.Name;

        return message.GuildId is null
            ? DiscordMapper.DisplayNameOf(message.Author.GlobalName, message.Author.Username)
            : message.ChannelId.ToString();
    }

    private static string RoleNameOf(Message message, ulong roleId)
    {
        if (message.Guild is { } guild && guild.Roles.TryGetValue(roleId, out Role? role)) return role.Name;

        return "role";
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_lifetime is not null) await _lifetime.CancelAsync().ConfigureAwait(false);

        _presence?.Dispose();
        _presence = null;

        _energy?.Dispose();
        _energy = null;

        await _status.DisposeAsync().ConfigureAwait(false);

        // Sessions are closed before their channels detach: closing drains whatever is
        // still queued, and a reply produced during that drain still needs somewhere to go.
        foreach (SessionId session in _openSessions.Values)
            await _sessions.CloseAsync(session, "surface stopped").ConfigureAwait(false);

        foreach (DiscordVoiceConnection connection in _voiceConnections.Values)
            await connection.DisposeAsync().ConfigureAwait(false);

        _voiceConnections.Clear();

        foreach (IDisposable attachment in _attached.Values) attachment.Dispose();

        _attached.Clear();
        _openSessions.Clear();

        try { await _client.CloseAsync(cancellationToken: ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "Discord surface {Surface} did not close cleanly", Id); }

        _bus.Publish(new SurfaceStateChanged(Id, Connected: false, "stopped"));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        _lifetime?.Dispose();
        _client.Dispose();
    }
}
