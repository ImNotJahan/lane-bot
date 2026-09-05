using Lane.Audio;
using Lane.Audio.Capture;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// A microphone left open in a house hears the whole house.
///
/// A conversation through a wall reaches it, comes back as words, and she answers a room that
/// was not talking to her. Loudness is the one thing that separates the two, because distance
/// is what made the far one faint.
/// </summary>
public sealed class NoiseGateTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(800);

    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly SessionId Room = new(new SurfaceId("microphone"), SessionKind.Voice, "room");

    private static NoiseGateOptions Options(double minimum = -40) =>
        new() { Enabled = true, MinimumLevel = minimum, Hold = Hold };

    /// <summary>A tenth of a second of tone at a given amplitude, as the microphone would hand it over.</summary>
    private static AudioFrame Sound(double amplitude, TimeSpan at)
    {
        const int samples = 1600;

        byte[] pcm = new byte[samples * 2];

        for (int i = 0; i < samples; i++)
        {
            short value = (short)(Math.Sin(i * 0.3) * amplitude * short.MaxValue);

            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), value);
        }

        return new AudioFrame(pcm, AudioFormat.Pcm16kMono, Start + at);
    }

    private static AudioFrame Talking(TimeSpan at)  => Sound(0.25, at);   // about -15 dBFS
    private static AudioFrame NextDoor(TimeSpan at) => Sound(0.004, at);  // about -51 dBFS

    private static bool IsSilent(AudioFrame frame) => frame.Pcm.Span.IndexOfAnyExcept((byte)0) < 0;

    private static async Task<List<AudioFrame>> HeardAsync(params AudioFrame[] frames)
    {
        NoiseGate gate = new(new Recording(frames), Options(), NullLogger.Instance);

        List<AudioFrame> heard = [];

        await foreach (AudioFrame frame in gate.ReadAsync(TestContext.Current.CancellationToken))
            heard.Add(frame);

        return heard;
    }

    private sealed class Recording(AudioFrame[] frames) : IAudioSource
    {
        public AudioSourceId Id => new("microphone/room");
        public AudioFormat Format => AudioFormat.Pcm16kMono;
        public string? SpeakerHint => null;

        public async IAsyncEnumerable<AudioFrame> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (AudioFrame frame in frames)
            {
                await Task.Yield();
                yield return frame;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TimeSpan At(int tenths) => TimeSpan.FromMilliseconds(tenths * 100);

    [Fact]
    public void Level_is_measured_against_what_the_format_can_hold()
    {
        // dBFS, so the numbers in configuration mean something absolute rather than something
        // relative to whatever this particular microphone happened to be doing.
        Assert.Equal(-9.0, NoiseGate.Level(Sound(0.5, At(0))), 0.5);
        Assert.Equal(-15.0, NoiseGate.Level(Talking(At(0))), 1.0);
        Assert.Equal(-51.0, NoiseGate.Level(NextDoor(At(0))), 1.0);

        // Digital silence is not quiet, it is nothing, and the logarithm of it is not a number
        // a comparison can do anything sensible with.
        Assert.Equal(
            double.NegativeInfinity,
            NoiseGate.Level(new AudioFrame(new byte[64], AudioFormat.Pcm16kMono, Start)));
    }

    [Fact]
    public async Task The_room_next_door_reaches_recognition_as_silence()
    {
        List<AudioFrame> heard = await HeardAsync(
            NextDoor(At(0)), NextDoor(At(1)), NextDoor(At(2)));

        Assert.Equal(3, heard.Count);
        Assert.All(heard, frame => Assert.True(IsSilent(frame)));
    }

    [Fact]
    public async Task Somebody_in_the_room_is_heard()
    {
        List<AudioFrame> heard = await HeardAsync(
            Talking(At(0)), Talking(At(1)), Talking(At(2)));

        Assert.Equal(3, heard.Count);
        Assert.All(heard, frame => Assert.False(IsSilent(frame)));
    }

    [Fact]
    public async Task The_start_of_the_first_word_is_not_clipped_off()
    {
        // Why the gate runs a frame behind. A word does not begin at full volume, so the
        // frame that crosses the threshold is usually its second — and the first one is where
        // her name would be.
        List<AudioFrame> heard = await HeardAsync(
            NextDoor(At(0)),                     // the room, quiet
            NextDoor(At(1)),                     // still quiet — but the word starts in here
            Talking(At(2)),                      // and crosses the threshold here
            Talking(At(3)));

        Assert.True(IsSilent(heard[0]));
        Assert.False(IsSilent(heard[1]));        // kept, because of what came next
        Assert.False(IsSilent(heard[2]));
        Assert.False(IsSilent(heard[3]));
    }

    [Fact]
    public async Task A_pause_in_the_middle_of_a_sentence_does_not_shut_the_gate()
    {
        // Speech is not continuously loud. A gate that shuts on the first quiet moment cuts
        // sentences into pieces, and recognition is left to guess at the halves.
        List<AudioFrame> heard = await HeardAsync(
            Talking(At(0)),
            NextDoor(At(1)),                     // a gap between words
            NextDoor(At(2)),
            Talking(At(3)));

        Assert.All(heard, frame => Assert.False(IsSilent(frame)));
    }

    [Fact]
    public async Task The_room_goes_quiet_again_once_the_hold_has_run_out()
    {
        List<AudioFrame> heard = await HeardAsync(
            Talking(At(0)),
            NextDoor(Hold + TimeSpan.FromMilliseconds(100)),
            NextDoor(Hold + TimeSpan.FromMilliseconds(200)),
            NextDoor(Hold + TimeSpan.FromMilliseconds(300)));

        Assert.False(IsSilent(heard[0]));
        Assert.True(IsSilent(heard[^1]));
    }

    [Fact]
    public async Task Nothing_captured_is_dropped_or_reordered()
    {
        // The gate substitutes silence rather than withholding frames, and hands over the
        // last one it was holding when the microphone stops. Anything else shortens the
        // stream, and both the recogniser's timing and the offsets an utterance is sliced
        // back out by are measured from the start of it.
        AudioFrame[] room = [Talking(At(0)), NextDoor(At(1)), Talking(At(2)), NextDoor(At(3))];

        List<AudioFrame> heard = await HeardAsync(room);

        Assert.Equal(room.Length, heard.Count);
        Assert.Equal([.. room.Select(f => f.Captured)], [.. heard.Select(f => f.Captured)]);
        Assert.Equal([.. room.Select(f => f.Pcm.Length)], [.. heard.Select(f => f.Pcm.Length)]);
    }

    // ---- what the microphone service actually assembles ---------------------

    private static MicrophoneService Service(MicrophoneOptions options, IVoiceFloor floor) =>
        new(options, null!, null!, floor, NullLoggerFactory.Instance);

    private static async Task<List<AudioFrame>> ThroughAsync(
        MicrophoneOptions options, IVoiceFloor floor, params AudioFrame[] frames)
    {
        IAudioSource ear = Service(options, floor).Listen(new Recording(frames), Room);

        List<AudioFrame> heard = [];

        await foreach (AudioFrame frame in ear.ReadAsync(TestContext.Current.CancellationToken))
            heard.Add(frame);

        return heard;
    }

    [Fact]
    public async Task Both_gates_are_hung_on_the_microphone_when_both_are_asked_for()
    {
        // The wiring, rather than either gate — they are separately correct above, and what
        // is left to get wrong is hanging one of them and not the other.
        MicrophoneOptions options = new()
        {
            SuppressEcho = true,
            NoiseGate    = { Enabled = true, MinimumLevel = -40, Hold = Hold }
        };

        // Quiet: the next room, which the noise gate answers for.
        Assert.All(
            await ThroughAsync(options, new NoVoiceFloor(), NextDoor(At(0)), NextDoor(At(1))),
            frame => Assert.True(IsSilent(frame)));

        // Loud, but it is Lane herself out of the speakers, which the echo gate answers for.
        Assert.All(
            await ThroughAsync(options, new AlwaysSpeaking(), Talking(At(0)), Talking(At(1))),
            frame => Assert.True(IsSilent(frame)));

        // Loud, and hers to hear.
        Assert.Contains(
            await ThroughAsync(options, new NoVoiceFloor(), Talking(At(0)), Talking(At(1))),
            frame => !IsSilent(frame));
    }

    [Fact]
    public async Task An_ungated_microphone_is_handed_over_exactly_as_it_arrived()
    {
        // Both are off by default, and off has to mean untouched — including no frame of
        // latency from a gate that is not there.
        AudioFrame quiet = NextDoor(At(0));

        List<AudioFrame> heard = await ThroughAsync(new MicrophoneOptions
        {
            SuppressEcho = false
        }, new NoVoiceFloor(), quiet);

        Assert.Same(quiet, Assert.Single(heard));
    }

    private sealed class AlwaysSpeaking : IVoiceFloor
    {
        public IDisposable BeginSpeaking(SessionId session, CancellationTokenSource speech) =>
            throw new NotSupportedException();

        public void NoticeSpeech(SessionId session, Transcript transcript) { }
        public void NoticeFinal(SessionId session) { }
        public bool IsSpeaking(SessionId session) => true;
    }
}
