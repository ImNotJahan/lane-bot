using System.Text;
using System.Threading.Channels;
using Lane.Audio;
using Lane.Audio.Capture;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// A microphone and a speaker in one room hear each other.
///
/// Left alone, that loop is not subtle: Lane answers the room, the microphone picks her up,
/// the first interim result of her own sentence takes the floor away from her, and she stops
/// mid-word — then whatever she managed to say arrives back as something the room said to
/// her, which she answers, into the same speakers.
/// </summary>
public sealed class EchoGateTests
{
    private static readonly SessionId Room = new(new SurfaceId("microphone"), SessionKind.Voice, "room");

    private static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(500);

    /// <summary>A floor a test holds and releases by hand.</summary>
    private sealed class HeldFloor : IVoiceFloor
    {
        public bool Speaking { get; set; }

        public IDisposable BeginSpeaking(SessionId session, CancellationTokenSource speech) =>
            throw new NotSupportedException();

        public void NoticeSpeech(SessionId session, Transcript transcript) { }

        public void NoticeFinal(SessionId session) { }

        public bool IsSpeaking(SessionId session) => Speaking;
    }

    /// <summary>A source that yields frames stamped with whatever time the test says.</summary>
    private sealed class Room_ : IAudioSource
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>();

        public AudioSourceId Id => new("microphone/room");
        public AudioFormat Format => AudioFormat.Pcm16kMono;
        public string? SpeakerHint => null;

        public void Hear(string words, DateTimeOffset at) =>
            _frames.Writer.TryWrite(new AudioFrame(Encoding.UTF8.GetBytes(words), Format, at));

        public void Close() => _frames.Writer.TryComplete();

        public IAsyncEnumerable<AudioFrame> ReadAsync(CancellationToken ct) => _frames.Reader.ReadAllAsync(ct);

        public ValueTask DisposeAsync()
        {
            Close();
            return ValueTask.CompletedTask;
        }
    }

    private static (Room_ Microphone, HeldFloor Floor, EchoGate Gate) Gate()
    {
        Room_ microphone = new();
        HeldFloor floor  = new();

        return (microphone, floor, new EchoGate(microphone, floor, Room, Tail, NullLogger.Instance));
    }

    private static async Task<List<AudioFrame>> DrainAsync(EchoGate gate)
    {
        List<AudioFrame> heard = [];

        await foreach (AudioFrame frame in gate.ReadAsync(TestContext.Current.CancellationToken))
            heard.Add(frame);

        return heard;
    }

    [Fact]
    public async Task What_the_room_says_passes_through_untouched()
    {
        (Room_ microphone, _, EchoGate gate) = Gate();

        microphone.Hear("it is raining", DateTimeOffset.UtcNow);
        microphone.Close();

        AudioFrame heard = Assert.Single(await DrainAsync(gate));

        Assert.Equal("it is raining", Encoding.UTF8.GetString(heard.Pcm.Span));
    }

    [Fact]
    public async Task Her_own_voice_reaches_recognition_as_silence()
    {
        // Silence rather than nothing: dropping the frames shortens the stream, and both the
        // recogniser's own timing and the offsets an utterance's audio is sliced back out by
        // are measured from the start of it.
        (Room_ microphone, HeldFloor floor, EchoGate gate) = Gate();

        floor.Speaking = true;

        microphone.Hear("I heard it is raining", DateTimeOffset.UtcNow);
        microphone.Close();

        AudioFrame heard = Assert.Single(await DrainAsync(gate));

        Assert.Equal("I heard it is raining".Length, heard.Pcm.Length);
        Assert.True(heard.Pcm.Span.IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public async Task The_last_of_her_sentence_does_not_get_through_as_she_stops()
    {
        // The tail is the whole difficulty. Playback ending is not the sound ending — there
        // is audio still leaving the speakers, and audio already captured but not yet read,
        // and both of them are her.
        (Room_ microphone, HeldFloor floor, EchoGate gate) = Gate();

        DateTimeOffset spoke = DateTimeOffset.UtcNow;

        // One frame at a time, releasing the floor between them, because that is the shape of
        // the thing: frames are read as they are captured, and the moment that matters is the
        // one where she has just finished and the room has not.
        await using IAsyncEnumerator<AudioFrame> ear =
            gate.ReadAsync(TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        floor.Speaking = true;
        microphone.Hear("I heard it", spoke);

        Assert.True(await ear.MoveNextAsync());
        Assert.True(ear.Current.Pcm.Span.IndexOfAnyExcept((byte)0) < 0);

        floor.Speaking = false;

        microphone.Hear("is raining", spoke + TimeSpan.FromMilliseconds(200));

        Assert.True(await ear.MoveNextAsync());
        Assert.True(ear.Current.Pcm.Span.IndexOfAnyExcept((byte)0) < 0);

        microphone.Hear("and cold", spoke + Tail + TimeSpan.FromMilliseconds(1));

        Assert.True(await ear.MoveNextAsync());
        Assert.Equal("and cold", Encoding.UTF8.GetString(ear.Current.Pcm.Span));
    }

    [Fact]
    public async Task A_long_answer_is_ignored_for_all_of_itself_not_just_its_first_moment()
    {
        // The tail runs from the last of her voice, not the first: measured from the start,
        // everything she said after the first half second would come straight back.
        (Room_ microphone, HeldFloor floor, EchoGate gate) = Gate();

        DateTimeOffset spoke = DateTimeOffset.UtcNow;

        floor.Speaking = true;

        for (int second = 0; second < 5; second++)
            microphone.Hear($"clause {second}", spoke + TimeSpan.FromSeconds(second));

        microphone.Close();

        Assert.All(await DrainAsync(gate), frame => Assert.True(frame.Pcm.Span.IndexOfAnyExcept((byte)0) < 0));
    }

    [Fact]
    public async Task Nothing_she_says_is_written_down_as_something_the_room_said()
    {
        // End to end through the router, because the symptom people see is not a silenced
        // frame — it is her own reply coming back as a message, in a loop.
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Transforming(text => $"heard[{text}]"));

        RecordingChannel channel = harness.OpenSession("microphone", "room", SessionKind.Voice);

        (Room_ microphone, HeldFloor floor, EchoGate gate) = Gate();

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new TranscribingBytesRecognizer(),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance);

        Participant room = new(new ParticipantId(new SurfaceId("microphone"), "room"), "the room");

        router.Register(gate, channel.Id, room);

        DateTimeOffset spoke = DateTimeOffset.UtcNow;

        floor.Speaking = true;
        microphone.Hear("heard[it is raining]", spoke);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Her own answer produced no words at all, so there was nothing to reply to.
        Assert.Empty(channel.Texts);

        floor.Speaking = false;
        microphone.Hear("so it is", spoke + Tail + TimeSpan.FromMilliseconds(1));

        await channel.WaitForAsync(1, TimeSpan.FromSeconds(5));

        Assert.Contains("heard[so it is]", channel.Texts);
    }
}
