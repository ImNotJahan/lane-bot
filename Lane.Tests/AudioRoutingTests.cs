using System.Threading.Channels;
using Lane.Audio;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class SentenceChunkerTests
{
    [Fact]
    public void A_clause_is_released_as_soon_as_it_ends()
    {
        // The point of the whole exercise: speech starts on the first sentence rather than
        // after the last one.
        SentenceChunker chunker = new();

        Assert.Empty(chunker.Add("Tide pools are"));

        Assert.Equal(["Tide pools are rocky shore habitats."], chunker.Add(" rocky shore habitats."));
    }

    [Fact]
    public void Several_sentences_in_one_fragment_all_come_out()
    {
        SentenceChunker chunker = new();

        IReadOnlyList<string> ready = chunker.Add("First one here. Second one here! Third one here?");

        Assert.Equal(3, ready.Count);
        Assert.Equal("First one here.", ready[0]);
        Assert.Equal("Third one here?", ready[2]);
    }

    [Fact]
    public void A_decimal_point_does_not_end_a_sentence()
    {
        // "It cost 3." followed by "5 pounds." would be two absurd utterances.
        SentenceChunker chunker = new();

        Assert.Empty(chunker.Add("The reading was 3.5 degrees"));
    }

    [Fact]
    public void A_very_short_fragment_waits_for_more()
    {
        // Speaking "Ah." on its own is worse than waiting a moment.
        SentenceChunker chunker = new();

        Assert.Empty(chunker.Add("Ah."));
    }

    [Fact]
    public void A_sentence_that_never_ends_is_broken_at_a_word_boundary()
    {
        SentenceChunker chunker = new(maxLength: 40, firstChunkMaxLength: 40);

        IReadOnlyList<string> ready = chunker.Add(string.Join(" ", Enumerable.Repeat("word", 30)));

        Assert.NotEmpty(ready);
        Assert.All(ready, s => Assert.DoesNotContain("wor ", s));      // never mid-word
        Assert.All(ready, s => Assert.True(s.Length <= 45, $"chunk was {s.Length} chars"));
    }

    [Fact]
    public void The_first_clause_is_cut_shorter_than_the_rest()
    {
        // Everything before she starts talking is dead air, so the opening clause is
        // deliberately released sooner than later ones.
        SentenceChunker chunker = new(maxLength: 200, firstChunkMaxLength: 60);

        string text = string.Join(" ", Enumerable.Repeat("word", 60));

        IReadOnlyList<string> ready = chunker.Add(text);

        Assert.True(ready[0].Length <= 65, $"first clause was {ready[0].Length} chars");
        Assert.True(ready.Skip(1).Any(s => s.Length > 65), "later clauses should be allowed to run longer");
    }

    [Fact]
    public void A_forced_break_prefers_a_comma_over_an_arbitrary_space()
    {
        // "creating a temporary ecosystem" / "teeming with life" sounds like a fault;
        // a clause break just sounds like a pause.
        SentenceChunker chunker = new(maxLength: 60, firstChunkMaxLength: 60);

        IReadOnlyList<string> ready = chunker.Add(
            "the tide had gone right out that morning, leaving the rocks bare and glistening in the sun");

        Assert.EndsWith(",", ready[0]);
    }

    [Fact]
    public void Whatever_is_left_comes_out_on_flush()
    {
        SentenceChunker chunker = new();

        chunker.Add("An unfinished thought");

        Assert.Equal("An unfinished thought", chunker.Flush());
        Assert.Null(chunker.Flush());
    }

    [Fact]
    public void Newlines_end_a_clause()
    {
        SentenceChunker chunker = new();

        Assert.Equal(["Here is a line of text"], chunker.Add("Here is a line of text\n"));
    }
}

/// <summary>
/// Several microphones at once, each attributed to a person in a conversation.
/// </summary>
public sealed class AudioRoutingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>A source that yields whatever the test writes to it.</summary>
    private sealed class FakeSource(string id, string? speaker = null) : IAudioSource
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>();

        public AudioSourceId Id { get; } = new(id);
        public AudioFormat Format => AudioFormat.Pcm16kMono;
        public string? SpeakerHint { get; } = speaker;

        public IAsyncEnumerable<AudioFrame> ReadAsync(CancellationToken ct) => _frames.Reader.ReadAllAsync(ct);

        public ValueTask DisposeAsync()
        {
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A recogniser that returns a fixed script rather than listening.</summary>
    private sealed class ScriptedRecognizer(params Transcript[] script) : ISpeechRecognizer
    {
        public async IAsyncEnumerable<Transcript> TranscribeAsync(
            IAudioSource source,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (Transcript transcript in script)
            {
                await Task.Delay(10, ct);

                // Prefixed so a test can tell which microphone a line came from.
                yield return transcript with { Text = $"{source.SpeakerHint}: {transcript.Text}" };
            }

            await Task.Delay(Timeout, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Two_people_in_one_channel_arrive_as_one_conversation_with_distinct_speakers()
    {
        // The M7 requirement. v2 could hear several speakers, but only Discord, and only
        // through a stream type its ear was built around.
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Transforming(text => $"heard[{text}]"));

        RecordingChannel channel = harness.OpenSession("discord.main", "vc", SessionKind.Voice);

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new ScriptedRecognizer(new Transcript("hello", IsFinal: true, TimeSpan.Zero)),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance);

        Participant alice = new(new ParticipantId(new SurfaceId("discord.main"), "1"), "alice");
        Participant bob   = new(new ParticipantId(new SurfaceId("discord.main"), "2"), "bob");

        router.Register(new FakeSource("discord.main/ssrc/1", "alice"), channel.Id, alice);
        router.Register(new FakeSource("discord.main/ssrc/2", "bob"), channel.Id, bob);

        Assert.Equal(2, router.ActiveSources);

        await channel.WaitForAsync(1, Timeout);
        await Task.Delay(300);

        // Both were heard and both were attributed, into the one session. Whether that
        // becomes one reply or two is the session pump's business — two people talking in
        // one room is one conversation, and coalescing them is the correct behaviour.
        string heard = string.Join("\n",
            harness.Model.Requests.SelectMany(r => r.Messages).Select(m => m.TextContent));

        Assert.Contains("alice: hello", heard);
        Assert.Contains("bob: hello", heard);
    }

    [Fact]
    public async Task Microphones_bound_to_different_conversations_stay_apart()
    {
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Transforming(text => $"re[{text}]"));

        RecordingChannel here      = harness.OpenSession("discord.main", "vc-a", SessionKind.Voice);
        RecordingChannel elsewhere = harness.OpenSession("discord.main", "vc-b", SessionKind.Voice);

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new ScriptedRecognizer(new Transcript("something", IsFinal: true, TimeSpan.Zero)),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance);

        Participant alice = new(new ParticipantId(new SurfaceId("discord.main"), "1"), "alice");

        router.Register(new FakeSource("mic-a", "alice"), here.Id, alice);

        await here.WaitForAsync(1, Timeout);
        await Task.Delay(200);

        Assert.Empty(elsewhere.Texts);
    }

    [Fact]
    public async Task Interim_results_are_not_submitted_as_messages()
    {
        // Only completed utterances become messages; interim text exists to drive barge-in.
        await using LaneHarness harness = LaneHarness.Create(ScriptedLanguageModel.Echoing("ok"));

        RecordingChannel channel = harness.OpenSession("discord.main", "vc", SessionKind.Voice);

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new ScriptedRecognizer(
                new Transcript("partial", IsFinal: false, TimeSpan.Zero),
                new Transcript("partial words", IsFinal: false, TimeSpan.Zero)),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance);

        router.Register(new FakeSource("mic", "alice"), channel.Id,
            new Participant(new ParticipantId(new SurfaceId("discord.main"), "1"), "alice"));

        await Task.Delay(400);

        Assert.Empty(channel.Texts);
        Assert.Equal(0, harness.Model.CallCount);
    }

    [Fact]
    public async Task Unregistering_stops_listening()
    {
        await using LaneHarness harness = LaneHarness.Create();

        RecordingChannel channel = harness.OpenSession("discord.main", "vc", SessionKind.Voice);

        await using AudioRouter router = new(
            harness.Kernel,
            _ => new ScriptedRecognizer(),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance);

        FakeSource source = new("mic", "alice");

        router.Register(source, channel.Id,
            new Participant(new ParticipantId(new SurfaceId("discord.main"), "1"), "alice"));

        Assert.Equal(1, router.ActiveSources);

        await router.UnregisterAsync(source.Id);

        Assert.Equal(0, router.ActiveSources);
    }

    [Fact]
    public async Task One_failing_microphone_does_not_silence_the_others()
    {
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Transforming(text => $"re[{text}]"));

        RecordingChannel channel = harness.OpenSession("discord.main", "vc", SessionKind.Voice);

        // Keyed on the source rather than call order: the two listeners start concurrently,
        // so "the first one" is a race.
        await using AudioRouter router = new(
            harness.Kernel,
            _ => new BreaksForOneSpeaker("alice",
                new Transcript("still here", IsFinal: true, TimeSpan.Zero)),
            new NoVoiceFloor(),
            NullLogger<AudioRouter>.Instance);

        Participant alice = new(new ParticipantId(new SurfaceId("discord.main"), "1"), "alice");
        Participant bob   = new(new ParticipantId(new SurfaceId("discord.main"), "2"), "bob");

        router.Register(new FakeSource("broken", "alice"), channel.Id, alice);
        router.Register(new FakeSource("working", "bob"), channel.Id, bob);

        IReadOnlyList<string> replies = await channel.WaitForAsync(1, Timeout);

        Assert.Contains("bob: still here", string.Join("\n", replies));
    }

    /// <summary>Fails for one named speaker and works for everyone else.</summary>
    private sealed class BreaksForOneSpeaker(string broken, params Transcript[] script) : ISpeechRecognizer
    {
        public async IAsyncEnumerable<Transcript> TranscribeAsync(
            IAudioSource source,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (source.SpeakerHint == broken) throw new InvalidOperationException("the microphone fell over");

            foreach (Transcript transcript in script)
            {
                await Task.Delay(10, ct);

                yield return transcript with { Text = $"{source.SpeakerHint}: {transcript.Text}" };
            }

            await Task.Delay(Timeout, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
