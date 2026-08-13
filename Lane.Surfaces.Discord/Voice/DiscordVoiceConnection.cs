using System.Collections.Concurrent;
using Lane.Audio;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;

namespace Lane.Surfaces.Discord.Voice;

/// <summary>
/// Lane sitting in one voice channel.
///
/// The voice channel is its own session — it has different capabilities from a text one and
/// its own turn queue — but shares a memory group with the text channel beside it, so what
/// is said aloud and what is typed are one conversation as far as she is concerned.
///
/// Every speaker becomes their own <see cref="IAudioSource"/>, registered with the router.
/// Two people talking are two sources bound to one session, which is what makes them arrive
/// as one ordered conversation rather than two interleaved ones.
/// </summary>
internal sealed class DiscordVoiceConnection(
    SurfaceId surface,
    GatewayClient gateway,
    ISessionRegistry sessions,
    AudioRouter router,
    IIdentityResolver identity,
    DiscordSurfaceOptions options,
    ILogger log) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<uint, DiscordVoiceSource> _sources = new();
    private readonly ConcurrentDictionary<uint, ulong> _ssrcToUser = new();
    private readonly ConcurrentDictionary<ulong, uint> _userToSsrc = new();

    private VoiceClient?  _voice;
    private SessionId?    _session;
    private IDisposable?  _attachment;

    public async Task JoinAsync(ulong guildId, ulong channelId, string channelName, CancellationToken ct)
    {
        SessionId id = new(surface, SessionKind.Voice, channelId.ToString());

        _session = id;

        sessions.GetOrCreate(new SessionDescriptor
        {
            Id           = id,
            DisplayName  = $"VC: {channelName}",

            // The same group the text channel uses, so one conversation spans both.
            MemoryGroup  = DiscordMapper.BuildMemoryGroup(
                options.MemoryGroupTemplate, surface.Value, guildId, channelId),

            Capabilities = ChannelCapabilities.Voice | ChannelCapabilities.Interrupt
        });

        _voice = await gateway.JoinVoiceChannelAsync(guildId, channelId, new VoiceClientConfiguration
        {
            ReceiveHandler = new VoiceReceiveHandler()
        }, cancellationToken: ct).ConfigureAwait(false);

        _voice.VoiceReceive += OnVoiceReceive;
        _voice.Speaking     += OnSpeaking;

        await _voice.StartAsync(cancellationToken: ct).ConfigureAwait(false);

        _attachment = sessions.Attach(new DiscordVoiceOutput(id, _voice, log));

        log.LogInformation("Joined voice channel {Channel} as {Session}", channelName, id);
    }

    private ValueTask OnVoiceReceive(VoiceReceiveEventArgs args)
    {
        // A null timestamp means the packet was lost; skipping mirrors the gap.
        if (args.Timestamp is null) return default;

        if (_sources.TryGetValue(args.Ssrc, out DiscordVoiceSource? source)) source.Write(args.Frame);

        return default;
    }

    private ValueTask OnSpeaking(SpeakingEventArgs args)
    {
        if (args.UserId == gateway.Id) return default;   // her own voice
        if (_session is null) return default;

        // Already listening to this speaker.
        if (_sources.ContainsKey(args.Ssrc)) return default;

        _ = Task.Run(() => StartListeningAsync(args.Ssrc, args.UserId));

        return default;
    }

    private async Task StartListeningAsync(uint ssrc, ulong userId)
    {
        try
        {
            if (_session is not { } session) return;

            string name = await ResolveNameAsync(userId).ConfigureAwait(false);

            AudioSourceId id = new($"{surface.Value}/ssrc/{ssrc}");

            DiscordVoiceSource source = new(id, name, log);

            if (!_sources.TryAdd(ssrc, source))
            {
                await source.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _ssrcToUser[ssrc]   = userId;
            _userToSsrc[userId] = ssrc;

            Participant speaker = identity.Resolve(new ParticipantId(surface, userId.ToString()), name);

            router.Register(source, session, speaker);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not start listening to SSRC {Ssrc}", ssrc);
        }
    }

    private async Task<string> ResolveNameAsync(ulong userId)
    {
        try
        {
            User user = await gateway.Rest.GetUserAsync(userId).ConfigureAwait(false);

            return DiscordMapper.DisplayNameOf(user.GlobalName, user.Username);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not resolve the name of user {User}", userId);

            return "someone";
        }
    }

    /// <summary>Stops listening to someone who left the channel.</summary>
    public async Task ForgetAsync(ulong userId)
    {
        if (!_userToSsrc.TryRemove(userId, out uint ssrc)) return;

        _ssrcToUser.TryRemove(ssrc, out _);

        if (_sources.TryRemove(ssrc, out DiscordVoiceSource? source))
        {
            await router.UnregisterAsync(source.Id).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (DiscordVoiceSource source in _sources.Values)
            await router.UnregisterAsync(source.Id).ConfigureAwait(false);

        _sources.Clear();

        if (_session is not null) await sessions.CloseAsync(_session, "left voice").ConfigureAwait(false);

        _attachment?.Dispose();

        if (_voice is not null)
        {
            try { await _voice.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { log.LogDebug(ex, "Voice client did not close cleanly"); }

            _voice.Dispose();
        }
    }
}
