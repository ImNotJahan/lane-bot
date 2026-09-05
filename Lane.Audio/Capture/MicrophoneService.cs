using Lane.Audio.Playback;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Capture;

/// <summary>
/// Opens the machine's microphone at startup, points it at a conversation, and gives that
/// conversation somewhere to answer.
///
/// Registered as a <em>shared</em> source, which is the whole reason it exists: a microphone
/// worn by one person is already covered by the API socket, and what this adds is a
/// microphone sitting in a room with several people around it.
///
/// The ear and the mouth are attached together on purpose. A session with a source and no
/// channel is not half-working, it is a conversation Lane holds entirely to herself — she
/// hears the room, answers it, writes the answer to memory, and drops it. Anything that
/// stops one here should stop the other.
/// </summary>
public sealed class MicrophoneService(
    MicrophoneOptions options,
    AudioRouter router,
    ISessionRegistry sessions,
    ILoggerFactory loggers,
    IRoomEcho? echo = null) : IHostedService
{
    private static readonly SurfaceId Surface = new("microphone");

    private readonly ILogger _log = loggers.CreateLogger<MicrophoneService>();

    private LocalMicrophoneSource? _source;
    private IDisposable? _attachment;

    public Task StartAsync(CancellationToken ct)
    {
        if (!options.Enabled) return Task.CompletedTask;

        SessionId id = new(Surface, SessionKind.Voice, options.Session);

        // The kernel drops events for sessions it has never been told about, so the
        // conversation has to exist before the first word arrives.
        sessions.GetOrCreate(new SessionDescriptor
        {
            Id           = id,
            DisplayName  = "the room",
            MemoryGroup  = $"microphone/{options.Session}",
            Capabilities = ChannelCapabilities.Voice | ChannelCapabilities.Interrupt,

            // Not direct: there is more than one person here, which is the point, and
            // User-scoped memory must not be surfaced to a room.
            IsDirect     = false
        });

        try
        {
            LocalMicrophoneSource source = new(
                new AudioSourceId($"{Surface.Value}/{options.Session}"), options, _log);

            source.Start();

            // Attached before the source is registered: audio can arrive the instant the
            // capture program starts, and a reply to the first thing said should not be the
            // one reply that has nowhere to go.
            _attachment = sessions.Attach(new RoomChannel(
                id,
                new SystemSpeaker(
                    new AudioFormat(options.OutputSampleRate, options.OutputChannels, 16),
                    loggers.CreateLogger("Lane.Audio.Playback"),
                    string.IsNullOrWhiteSpace(options.Player) ? null : options.Player),
                options.ShowInTui ? echo : null,
                "Lane",
                _log));

            // Whoever cannot be placed falls back to this rather than being dropped. It is
            // deliberately not a person: an unplaceable line belongs to the room.
            router.RegisterShared(source, id,
                new Participant(new ParticipantId(Surface, "room"), "the room"));

            _source = source;

            _log.LogInformation("The room is listening on {Session}, answering out loud{Echo}",
                id, options.ShowInTui && echo is not null ? " and on screen" : "");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The local microphone could not be opened; Lane is running without it");

            _attachment?.Dispose();
            _attachment = null;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_source is not null)
        {
            await router.UnregisterAsync(_source.Id).ConfigureAwait(false);

            _source = null;
        }

        // After the source, not before: unregistering drains what the recogniser was still
        // holding, and a reply produced during that drain still needs somewhere to go.
        _attachment?.Dispose();
        _attachment = null;
    }
}
