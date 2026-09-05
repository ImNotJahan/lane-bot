using Lane.Audio;
using Lane.Audio.Capture;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The room's write-back handle.
///
/// A microphone with no channel attached is a conversation Lane holds entirely to herself:
/// she hears the room, answers it, writes the answer to memory, and <c>DeliveryStage</c>
/// drops it with one warning line. From inside the room that is indistinguishable from her
/// having nothing to say, which is why most of these tests are about the reply having
/// somewhere to go at all.
/// </summary>
public sealed class RoomChannelTests
{
    private static readonly SessionId Room =
        new(new SurfaceId("microphone"), SessionKind.Voice, "room");

    /// <summary>A speaker that records what it was asked to play instead of playing it.</summary>
    private sealed class FakeSpeaker : IVoiceOutput
    {
        public List<byte[]> Played { get; } = [];

        public AudioFormat Format => AudioFormat.Pcm16kMono;

        public async Task PlayAsync(IAsyncEnumerable<AudioFrame> audio, CancellationToken ct)
        {
            await foreach (AudioFrame frame in audio.WithCancellation(ct)) Played.Add(frame.Pcm.ToArray());
        }

        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class FakeEcho : IRoomEcho
    {
        private readonly StringWriter _writer = new();

        public TextWriter Writer => _writer;

        public string Written => _writer.ToString();
    }

    private static RoomChannel Channel(IVoiceOutput speaker, IRoomEcho? echo = null) =>
        new(Room, speaker, echo, "Lane", NullLogger.Instance);

    [Fact]
    public void The_room_offers_both_a_voice_and_somewhere_to_write()
    {
        // Both, on one channel, the way the API's voice socket does it — they are two
        // renderings of one reply rather than two places to send it.
        RoomChannel channel = Channel(new FakeSpeaker());

        Assert.True(channel.TryGetService(out IVoiceOutput? voice));
        Assert.True(channel.TryGetService(out ITextOutput? text));

        Assert.NotNull(voice);
        Assert.NotNull(text);
    }

    [Fact]
    public async Task A_reply_reaches_the_pane_when_one_is_watching()
    {
        FakeEcho echo = new();

        await Channel(new FakeSpeaker(), echo)
            .SendAsync(new OutboundText("it is raining"), TestContext.Current.CancellationToken);

        Assert.Contains("Lane: it is raining", echo.Written);
    }

    [Fact]
    public async Task A_reply_with_nobody_watching_is_not_an_error()
    {
        // She said it out loud; the mirror simply had nowhere to go. Failing the turn here
        // would make a headless room worse than no room.
        await Channel(new FakeSpeaker())
            .SendAsync(new OutboundText("nobody is watching"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_pane_that_has_gone_away_does_not_fail_a_turn_already_spoken()
    {
        await Channel(new FakeSpeaker(), new BrokenEcho())
            .SendAsync(new OutboundText("still spoken"), TestContext.Current.CancellationToken);
    }

    private sealed class BrokenEcho : IRoomEcho
    {
        public TextWriter Writer => throw new ObjectDisposedException("the pane");
    }

    [Fact]
    public async Task Speaking_goes_to_the_speaker()
    {
        FakeSpeaker speaker = new();

        await Channel(speaker).PlayAsync(
            Frames("hello"u8.ToArray()), TestContext.Current.CancellationToken);

        Assert.Equal([.. "hello"u8], Assert.Single(speaker.Played));
    }

    private static async IAsyncEnumerable<AudioFrame> Frames(byte[] pcm)
    {
        await Task.Yield();

        yield return AudioFrame.Of(pcm, AudioFormat.Pcm16kMono);
    }

    [Fact]
    public async Task A_reply_to_the_room_is_delivered_rather_than_dropped()
    {
        // The regression that started this: the microphone session used to be created but
        // never attached to, so every reply it produced went to the warning log instead of
        // to the room. Asserted end to end through the real pipeline.
        FakeEcho echo = new();

        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Transforming(said => $"I heard {said}"));

        harness.Sessions.GetOrCreate(new SessionDescriptor
        {
            Id           = Room,
            DisplayName  = "the room",
            MemoryGroup  = "microphone/room",
            Capabilities = ChannelCapabilities.Voice | ChannelCapabilities.Interrupt,
            IsDirect     = false
        });

        using IDisposable attachment = harness.Sessions.Attach(Channel(new FakeSpeaker(), echo));

        Participant speaker = new(new ParticipantId(Room.Surface, "voice/v-1"), "Voice 1");

        await harness.Kernel.SubmitAsync(new InboundEvent
        {
            Session = Room,
            Author  = speaker,
            Message = LaneMessage.User(Room, speaker, "it is raining", DateTimeOffset.UtcNow)
        }, TestContext.Current.CancellationToken);

        // The reply came back to the room it was said in.
        for (int i = 0; i < 100 && !echo.Written.Contains("I heard"); i++) await Task.Delay(50);

        Assert.Contains("Lane: I heard it is raining", echo.Written);
    }

    [Fact]
    public async Task Opening_the_microphone_also_gives_the_room_a_mouth()
    {
        // The bug this whole file exists for. MicrophoneService used to create the session
        // and register the source without ever attaching a channel, so the ear worked and
        // the mouth did not — and the only symptom was one warning line per turn. Asserted
        // against the service rather than against a channel a test attached itself, because
        // that is where the omission was.
        await using LaneHarness harness = LaneHarness.Create();

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new SilentRecognizer(),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance);

        MicrophoneOptions options = new()
        {
            Enabled   = true,
            Session   = "room",
            Command   = "sh",
            Arguments = ["-c", "sleep 30"],          // opens, stays open, says nothing
            ShowInTui = true
        };

        FakeEcho echo = new();

        MicrophoneService service = new(
            options, router, harness.Sessions, NullLoggerFactory.Instance, echo);

        await service.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            Assert.True(harness.Sessions.TryGet(Room, out Session? session));
            Assert.NotNull(session);

            // Both halves, because either alone is a conversation that only works one way.
            Assert.NotEmpty(session.ResolveOutputs<IVoiceOutput>(DeliveryTarget.Primary));
            Assert.NotEmpty(session.ResolveOutputs<ITextOutput>(DeliveryTarget.Primary));

            Assert.Equal(1, router.ActiveSources);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        // And the channel goes when the microphone does, rather than outliving it.
        Assert.True(harness.Sessions.TryGet(Room, out Session? after));
        Assert.NotNull(after);

        Assert.Empty(after.ResolveOutputs<IVoiceOutput>(DeliveryTarget.Primary));
    }

    /// <summary>Hears nothing, for as long as it is asked to.</summary>
    private sealed class SilentRecognizer : ISpeechRecognizer
    {
        public async IAsyncEnumerable<Transcript> TranscribeAsync(
            IAudioSource source,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);

            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
