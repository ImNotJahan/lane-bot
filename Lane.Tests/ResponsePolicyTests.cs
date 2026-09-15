using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Pipeline.Stages;
using Lane.Core.Presence;
using Lane.Core.Sessions;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Deciding whether to answer at all.
///
/// This is the difference between an agent that sits in a busy channel and one that replies
/// to every message in it. v2 had it; the rewrite went eight milestones without it, and
/// nothing failed — Lane simply answered everything, which is the kind of regression only a
/// parity pass catches.
/// </summary>
public sealed class ResponsePolicyTests
{
    /// <summary>A model that scores the gate, then answers normally on the turn itself.</summary>
    private static ScriptedLanguageModel Scoring(float enthusiasm, string emoticon = ":3", string reply = "sure")
    {
        return new ScriptedLanguageModel((request, _) =>
            request.ToolChoice.Mode == ToolChoiceMode.Specific
                ? ScriptedLanguageModel.ToolCall("assess", new { enthusiasm, emoticon })
                : ScriptedLanguageModel.Text(reply));
    }

    private static LaneHarness Harness(ScriptedLanguageModel model, Action<ResponsePolicyOptions>? configure = null) =>
        LaneHarness.Create(model, configure: services =>
            services.Configure<ResponsePolicyOptions>(o =>
            {
                o.Enabled              = true;
                o.SkipInDirectSessions = false;
                configure?.Invoke(o);
            }));

    [Fact]
    public async Task A_message_she_has_no_interest_in_gets_no_reply()
    {
        await using LaneHarness harness = Harness(Scoring(0.0f));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "chatter aimed at somebody else");

        await channel.QuietAsync();

        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task A_message_she_cares_about_gets_one()
    {
        await using LaneHarness harness = Harness(Scoring(0.9f, reply: "go on"));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "jahan", "Lane, what do you make of this?");

        await channel.WaitForAsync(1);

        Assert.Equal("go on", Assert.Single(channel.Sent).Text);
    }

    [Fact]
    public async Task Sitting_one_out_still_means_hearing_it()
    {
        // The point of suppressing rather than dropping: a conversation Lane stayed out of is
        // one she can still refer back to.
        RecordingTranscript transcript = new();

        await using LaneHarness harness = LaneHarness.Create(Scoring(0.0f), configure: services =>
        {
            services.Configure<ResponsePolicyOptions>(o =>
            {
                o.Enabled              = true;
                o.SkipInDirectSessions = false;
            });

            services.AddSingleton<Lane.Core.Memory.ITranscriptStore>(transcript);
        });

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "a thing worth remembering");

        await channel.QuietAsync();

        Assert.Empty(channel.Sent);

        // Heard and written down, even though she said nothing back.
        Assert.Contains(transcript.Written, m => m.TextContent.Contains("a thing worth remembering"));
    }

    private sealed class RecordingTranscript : Lane.Core.Memory.ITranscriptStore
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<LaneMessage> _written = new();

        public IReadOnlyList<LaneMessage> Written => [.. _written];

        public ValueTask<long> AppendAsync(LaneMessage message, CancellationToken ct)
        {
            _written.Enqueue(message);
            return ValueTask.FromResult((long)_written.Count);
        }

        public ValueTask<IReadOnlyList<LaneMessage>> ReadAsync(
            Lane.Core.Memory.TranscriptQuery query, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<LaneMessage>>([]);
    }

    [Fact]
    public async Task The_threshold_is_where_it_is_configured()
    {
        await using LaneHarness harness = Harness(Scoring(0.5f), o => o.DefaultThreshold = 0.7f);

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "middling");

        await channel.QuietAsync();

        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task A_conversation_threshold_overrides_the_default()
    {
        FixedThresholds thresholds = new(0.1f);

        await using LaneHarness harness = LaneHarness.Create(Scoring(0.5f, reply: "fine"), configure: services =>
        {
            services.Configure<ResponsePolicyOptions>(o =>
            {
                o.Enabled              = true;
                o.SkipInDirectSessions = false;
                o.DefaultThreshold     = 0.7f;
            });

            services.AddSingleton<ISessionThresholds>(thresholds);
        });

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "middling");

        await channel.WaitForAsync(1);

        Assert.Equal("fine", Assert.Single(channel.Sent).Text);
    }

    private sealed class FixedThresholds(float threshold) : ISessionThresholds
    {
        public float? For(SessionDescriptor? session) => threshold;

        public bool Durable => true;

        public ValueTask SetAsync(string memoryGroup, float? value, CancellationToken ct) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_one_to_one_conversation_is_not_gated_by_default()
    {
        // Being ignored by something you are talking to directly reads as broken rather than
        // as tactful. v2 gated everywhere; this is a deliberate difference.
        await using LaneHarness harness = LaneHarness.Create(Scoring(0.0f), configure: services =>
            services.Configure<ResponsePolicyOptions>(o => o.Enabled = true));

        RecordingChannel channel = harness.OpenSession("terminal", "local");

        harness.Sessions.GetOrCreate(new SessionDescriptor
        {
            Id          = channel.Id,
            DisplayName = "terminal",
            MemoryGroup = "terminal/local",
            IsDirect    = true
        });

        await harness.SendAsync(channel, "jahan", "hello");

        await channel.WaitForAsync(1);

        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task A_broken_classifier_makes_her_talkative_not_silent()
    {
        // The important failure direction. Silence caused by a broken gate is
        // indistinguishable, from the outside, from Lane ignoring you.
        ScriptedLanguageModel model = new((request, _) =>
            request.ToolChoice.Mode == ToolChoiceMode.Specific
                ? throw new InvalidOperationException("the classifier is down")
                : ScriptedLanguageModel.Text("still here"));

        await using LaneHarness harness = Harness(model);

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "anything");

        await channel.WaitForAsync(1);

        Assert.Equal("still here", Assert.Single(channel.Sent).Text);
    }

    [Fact]
    public async Task A_classifier_that_answers_in_prose_is_not_trusted_into_silence()
    {
        // A model ignoring tool_choice and replying with text is a real thing on OpenRouter.
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("probably 0.0, I would ignore it");

        await using LaneHarness harness = Harness(model);

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "anything");

        await channel.WaitForAsync(1);

        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task Her_face_follows_the_conversation_again()
    {
        // In v2 the router picked an emoticon on every message. Between M5 and M9 the only
        // thing that could change her expression was her own set_emoticon tool.
        await using LaneHarness harness = Harness(Scoring(0.9f, emoticon: "o_o"));

        List<PresenceChanged> seen = [];

        using IDisposable subscription = harness.Services
            .GetRequiredService<IEventBus>()
            .Subscribe<PresenceChanged>(seen.Add);

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "jahan", "look at this");

        await channel.WaitForAsync(1);

        Assert.Equal("o_o", Assert.Single(seen).Emoticon);
    }

    [Fact]
    public async Task How_much_she_cares_reaches_the_reply()
    {
        // Not merely permitted: pitched. A message that scraped past the threshold should get
        // an acknowledgement rather than an essay.
        await using LaneHarness harness = Harness(Scoring(0.3f));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "mm");

        await channel.WaitForAsync(1);

        ModelRequest reply = harness.Model.Requests[^1];

        Assert.Contains(reply.System, block => block.Text.Contains("barely warrants"));
    }

    [Fact]
    public async Task Something_she_volunteers_is_never_second_guessed()
    {
        // A directive is text Lane already decided to say — asking whether to say it would
        // spend a model call to reconsider a decision that has already been made.
        await using LaneHarness harness = Harness(Scoring(0.0f));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.Kernel.PostAsync(
            channel.Id, new SessionWorkItem.Speak("I was thinking about you", DeliveryTarget.Primary, "monologue"));

        await channel.WaitForAsync(1);

        Assert.Equal("I was thinking about you", Assert.Single(channel.Sent).Text);
    }

    [Fact]
    public async Task Turning_the_gate_off_costs_nothing_per_turn()
    {
        await using LaneHarness harness = LaneHarness.Create(Scoring(0.0f), configure: services =>
            services.Configure<ResponsePolicyOptions>(o => o.Enabled = false));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "anything");

        await channel.WaitForAsync(1);

        // One call, not two: the classifier was never consulted.
        Assert.Equal(1, harness.Model.CallCount);
    }
}
