using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Capture;

/// <summary>
/// Opens the machine's microphone at startup and points it at a conversation.
///
/// Registered as a <em>shared</em> source, which is the whole reason it exists: a microphone
/// worn by one person is already covered by the API socket, and what this adds is a
/// microphone sitting in a room with several people around it.
///
/// A microphone that will not open takes the microphone down and nothing else. Lane keeps
/// every surface she had, and the failure is logged loudly rather than leaving a room
/// wondering why she never answers — which is what silence looks like from the inside.
/// </summary>
public sealed class MicrophoneService(
    MicrophoneOptions options,
    AudioRouter router,
    ISessionRegistry sessions,
    ILogger<MicrophoneService> log) : IHostedService
{
    private static readonly SurfaceId Surface = new("microphone");

    private LocalMicrophoneSource? _source;

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
                new AudioSourceId($"{Surface.Value}/{options.Session}"), options, log);

            source.Start();

            // Whoever cannot be placed falls back to this rather than being dropped. It is
            // deliberately not a person: an unplaceable line belongs to the room.
            router.RegisterShared(source, id,
                new Participant(new ParticipantId(Surface, "room"), "the room"));

            _source = source;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The local microphone could not be opened; Lane is running without it");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_source is null) return;

        await router.UnregisterAsync(_source.Id).ConfigureAwait(false);

        _source = null;
    }
}
