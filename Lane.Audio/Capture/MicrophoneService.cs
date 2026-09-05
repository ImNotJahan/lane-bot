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
///
/// Attaching both is also what makes the room a feedback loop, so the ear goes on behind an
/// <see cref="EchoGate"/>: without it her own answer comes back through the microphone,
/// takes the floor off her mid-sentence, and is written down as something the room said.
/// </summary>
public sealed class MicrophoneService(
    MicrophoneOptions options,
    AudioRouter router,
    ISessionRegistry sessions,
    IVoiceFloor floor,
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
            router.RegisterShared(Listen(source, id), id,
                new Participant(new ParticipantId(Surface, "room"), "the room"));

            _source = source;

            _log.LogInformation(
                "The room is listening on {Session}{Threshold}, answering out loud{Echo}{Deaf}",
                id,
                options.NoiseGate.Enabled ? $" to anything above {options.NoiseGate.MinimumLevel:0.0} dBFS" : "",
                options.ShowInTui && echo is not null ? " and on screen" : "",
                options.SuppressEcho ? " and ignoring itself while it does" : "");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The local microphone could not be opened; Lane is running without it");

            _attachment?.Dispose();
            _attachment = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// What recognition actually gets to hear, which is not simply the microphone.
    ///
    /// Both gates hand on silence rather than withholding frames, so they compose: audio
    /// goes through as itself only where the room was loud enough to be talking to her and
    /// Lane was not talking over it.
    ///
    /// Innermost first, and the order is the reasoning. The noise gate goes closest to the
    /// microphone because its question is about the room — how loud is it — and it wants the
    /// room as it actually sounded. The echo gate goes outside it, because what it silences
    /// is not the room at all: attaching the mouth above is what puts Lane's own voice into
    /// this microphone in the first place, and without it she interrupts herself on her own
    /// first clause.
    /// </summary>
    internal IAudioSource Listen(IAudioSource source, SessionId id)
    {
        if (options.NoiseGate.Enabled) source = new NoiseGate(source, options.NoiseGate, _log);

        if (options.SuppressEcho) source = new EchoGate(source, floor, id, options.EchoTail, _log);

        return source;
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
