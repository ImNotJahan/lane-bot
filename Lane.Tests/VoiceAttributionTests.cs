using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Lane.Audio;
using Lane.Audio.Recognition;
using Lane.Audio.Voiceprints;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Keeping the last few seconds of what was sent to a recogniser, so an utterance's audio
/// can be taken back out once the recogniser says where it was.
/// </summary>
public sealed class UtteranceBufferTests
{
    private static readonly AudioFormat Format = AudioFormat.Pcm16kMono;   // 32000 bytes a second

    /// <summary>A recognisable pattern, so a slice can be checked against where it came from.</summary>
    private static byte[] Pattern(int from, int length)
    {
        byte[] bytes = new byte[length];

        for (int i = 0; i < length; i++) bytes[i] = (byte)((from + i) % 251);

        return bytes;
    }

    [Fact]
    public void An_utterance_comes_back_exactly_as_it_went_in()
    {
        UtteranceBuffer buffer = new(Format, TimeSpan.FromSeconds(2));

        buffer.Write(Pattern(0, 32000));

        // The second half second of what was written.
        ReadOnlyMemory<byte> slice = buffer.Slice(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.5));

        Assert.Equal(16000, slice.Length);
        Assert.Equal(Pattern(16000, 16000), slice.ToArray());
    }

    [Fact]
    public void A_slice_that_spans_the_wrap_is_still_contiguous()
    {
        // The case a ring buffer exists to get wrong: the window has turned over, and the
        // wanted audio starts near the end of the array and finishes at the beginning.
        UtteranceBuffer buffer = new(Format, TimeSpan.FromSeconds(1));

        buffer.Write(Pattern(0, 24000));
        buffer.Write(Pattern(24000, 16000));         // total 40000, past the 32000 capacity

        ReadOnlyMemory<byte> slice = buffer.Slice(TimeSpan.FromSeconds(0.75), TimeSpan.FromSeconds(0.25));

        Assert.Equal(Pattern(24000, 8000), slice.ToArray());
    }

    [Fact]
    public void Audio_that_has_rolled_past_comes_back_empty_rather_than_wrong()
    {
        // The whole reason the return is allowed to be empty. Handing back whatever now
        // occupies those bytes would be a voiceprint taken from the wrong person's words.
        UtteranceBuffer buffer = new(Format, TimeSpan.FromSeconds(1));

        buffer.Write(Pattern(0, 32000));
        buffer.Write(Pattern(32000, 32000));

        Assert.True(buffer.Slice(TimeSpan.Zero, TimeSpan.FromSeconds(0.5)).IsEmpty);
    }

    [Fact]
    public void A_write_larger_than_the_window_keeps_its_tail()
    {
        UtteranceBuffer buffer = new(Format, TimeSpan.FromSeconds(1));

        buffer.Write(Pattern(0, 96000));             // three times the capacity in one go

        ReadOnlyMemory<byte> slice = buffer.Slice(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(0.5));

        Assert.Equal(Pattern(80000, 16000), slice.ToArray());
    }

    [Fact]
    public void An_utterance_the_buffer_has_not_been_handed_yet_is_clipped_not_invented()
    {
        UtteranceBuffer buffer = new(Format, TimeSpan.FromSeconds(2));

        buffer.Write(Pattern(0, 16000));

        // Asking for a second when only half of one has arrived.
        ReadOnlyMemory<byte> slice = buffer.Slice(TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Assert.Equal(Pattern(0, 16000), slice.ToArray());
    }
}

/// <summary>
/// One microphone, several people: telling them apart, and never quietly guessing.
/// </summary>
public sealed class VoiceAttributionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly AudioFormat Format = AudioFormat.Pcm16kMono;

    private static LabelRosterAttributor Attributor() =>
        new(IdentityResolver.Empty, NullLogger<LabelRosterAttributor>.Instance);

    /// <summary>A source that reads nothing; the script does the talking.</summary>
    private sealed class SilentSource(string id) : IAudioSource
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>();

        public AudioSourceId Id { get; } = new(id);
        public AudioFormat Format => AudioFormat.Pcm16kMono;
        public string? SpeakerHint => null;

        public IAsyncEnumerable<AudioFrame> ReadAsync(CancellationToken ct) => _frames.Reader.ReadAllAsync(ct);

        public ValueTask DisposeAsync()
        {
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Speaks a fixed script of labelled utterances, the way a diarizing service would.</summary>
    private sealed class ScriptedDiarizingRecognizer(params (string Label, string Text, double Seconds)[] script)
        : ISpeechRecognizer
    {
        public async IAsyncEnumerable<Transcript> TranscribeAsync(
            IAudioSource source, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach ((string label, string text, double seconds) in script)
            {
                await Task.Delay(10, ct);

                yield return new Transcript(text, IsFinal: true, TimeSpan.Zero,
                    Voice: new VoiceSample(
                        label,
                        new byte[Format.BytesFor(TimeSpan.FromSeconds(seconds))],
                        Format));
            }

            await Task.Delay(Timeout, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static LaneHarness HarnessWith(RecordingTranscript transcript) =>
        LaneHarness.Create(
            ScriptedLanguageModel.Transforming(text => $"heard[{text}]"),
            configure: s => s.AddSingleton<ITranscriptStore>(transcript));

    [Fact]
    public async Task Two_people_on_one_microphone_arrive_as_two_people()
    {
        // The point of the whole exercise. Before this, one source was one person, so a
        // microphone pointed at a room made three people into one contradicting themselves.
        RecordingTranscript transcript = new();

        await using LaneHarness harness = HarnessWith(transcript);

        RecordingChannel channel = harness.OpenSession("api.local", "room", SessionKind.Voice);

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new ScriptedDiarizingRecognizer(
                ("Guest-1", "it is raining", 2.0),
                ("Guest-2", "so it is", 2.0)),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance,
            Attributor());

        router.RegisterShared(new SilentSource("api.local/ws/room"), channel.Id,
            new Participant(new ParticipantId(new SurfaceId("api.local"), "client"), "the room"));

        await channel.WaitForAsync(1, Timeout);
        await Task.Delay(300);

        Assert.Equal(["it is raining"], transcript.Said["Voice 1"]);
        Assert.Equal(["so it is"], transcript.Said["Voice 2"]);

        // And specifically not both under the name the source was registered with.
        Assert.Empty(transcript.Said["the room"]);

        // Two people, so two private memory scopes — one room's worth of talk accumulating
        // under a single key is the fault this replaces.
        Assert.Equal(2, transcript.Speakers.Select(p => p.StableKey).Distinct().Count());
    }

    [Fact]
    public async Task One_person_speaking_twice_stays_one_person()
    {
        RecordingTranscript transcript = new();

        await using LaneHarness harness = HarnessWith(transcript);

        RecordingChannel channel = harness.OpenSession("api.local", "room", SessionKind.Voice);

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new ScriptedDiarizingRecognizer(
                ("Guest-1", "first thing", 2.0),
                ("Guest-2", "an interruption", 2.0),
                ("Guest-1", "second thing", 2.0)),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance,
            Attributor());

        router.RegisterShared(new SilentSource("api.local/ws/room"), channel.Id,
            new Participant(new ParticipantId(new SurfaceId("api.local"), "client"), "the room"));

        await channel.WaitForAsync(1, Timeout);
        await Task.Delay(300);

        // The roster is what stops attribution flapping: the same label is the same person
        // for as long as the microphone stays open.
        Assert.Equal(["first thing", "second thing"], transcript.Said["Voice 1"]);
        Assert.Equal(["an interruption"], transcript.Said["Voice 2"]);
    }

    [Fact]
    public async Task A_source_with_one_person_on_it_is_left_alone()
    {
        // Discord gives one source per speaker and knows exactly who each one is. Running
        // that through attribution could only ever make it worse — so a diarized transcript
        // arriving on a source registered as one person is ignored, not trusted.
        RecordingTranscript transcript = new();

        await using LaneHarness harness = HarnessWith(transcript);

        RecordingChannel channel = harness.OpenSession("discord.main", "vc", SessionKind.Voice);

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new ScriptedDiarizingRecognizer(("Guest-1", "hello", 2.0)),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance,
            Attributor());

        router.Register(new SilentSource("discord.main/ssrc/1"), channel.Id,
            new Participant(new ParticipantId(new SurfaceId("discord.main"), "1"), "alice"));

        await channel.WaitForAsync(1, Timeout);
        await Task.Delay(300);

        Assert.Equal(["hello"], transcript.Said["alice"]);
        Assert.Empty(transcript.Said["Voice 1"]);
    }

    [Fact]
    public async Task A_label_the_service_has_not_decided_on_is_still_heard()
    {
        // "Unknown" arrives while the service is still making its mind up. Dropping those
        // words would lose the opening of every conversation.
        RecordingTranscript transcript = new();

        await using LaneHarness harness = HarnessWith(transcript);

        RecordingChannel channel = harness.OpenSession("api.local", "room", SessionKind.Voice);

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new ScriptedDiarizingRecognizer(("Unknown", "who said that", 2.0)),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance,
            Attributor());

        router.RegisterShared(new SilentSource("api.local/ws/room"), channel.Id,
            new Participant(new ParticipantId(new SurfaceId("api.local"), "client"), "the room"));

        await channel.WaitForAsync(1, Timeout);
        await Task.Delay(300);

        Assert.Equal(["who said that"], transcript.Said["Voice 1"]);
    }

    [Fact]
    public async Task An_attributor_that_fails_still_lets_the_words_through()
    {
        // Something was said. Hearing it as the fallback beats not hearing it at all.
        RecordingTranscript transcript = new();

        await using LaneHarness harness = HarnessWith(transcript);

        RecordingChannel channel = harness.OpenSession("api.local", "room", SessionKind.Voice);

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new ScriptedDiarizingRecognizer(("Guest-1", "still here", 2.0)),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance,
            new ThrowingAttributor());

        router.RegisterShared(new SilentSource("api.local/ws/room"), channel.Id,
            new Participant(new ParticipantId(new SurfaceId("api.local"), "client"), "the room"));

        await channel.WaitForAsync(1, Timeout);
        await Task.Delay(300);

        Assert.Equal(["still here"], transcript.Said["the room"]);
    }

    private sealed class ThrowingAttributor : ISpeakerAttributor
    {
        public ValueTask<Participant> AttributeAsync(
            AudioBinding binding, VoiceSample voice, CancellationToken ct) =>
            throw new InvalidOperationException("the model file is a picture of a duck");

        public void Release(AudioSourceId source) { }
    }

    [Fact]
    public async Task Labels_are_forgotten_when_the_microphone_goes()
    {
        // They are about to be handed to whoever talks next, so anything remembered against
        // them would give one person's private memory to the next stranger to say hello.
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Transforming(text => $"heard[{text}]"));

        RecordingChannel channel = harness.OpenSession("api.local", "room", SessionKind.Voice);

        LabelRosterAttributor attributor = Attributor();

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new ScriptedDiarizingRecognizer(("Guest-9", "hello", 2.0)),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance,
            attributor);

        SilentSource source = new("api.local/ws/room");

        router.RegisterShared(source, channel.Id,
            new Participant(new ParticipantId(new SurfaceId("api.local"), "client"), "the room"));

        await channel.WaitForAsync(1, Timeout);

        AudioBinding binding = new(source.Id, channel.Id,
            new Participant(new ParticipantId(new SurfaceId("api.local"), "client"), "the room"),
            SpeakerAttribution.Diarized);

        // Guest-9 was the first voice heard, so it is Voice 1 until the source goes away.
        Participant before = await attributor.AttributeAsync(
            binding, new VoiceSample("Guest-9", new byte[64000], Format), TestContext.Current.CancellationToken);

        await router.UnregisterAsync(source.Id);

        Participant after = await attributor.AttributeAsync(
            binding, new VoiceSample("Guest-4", new byte[64000], Format), TestContext.Current.CancellationToken);

        Assert.Equal("Voice 1", before.DisplayName);
        Assert.Equal("Voice 1", after.DisplayName);
        Assert.NotEqual(before.Id, after.Id);
    }
}
