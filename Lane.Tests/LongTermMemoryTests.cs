using System.Text.Json;
using System.Text.Json.Nodes;
using Lane.Core.Context;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Prompts;
using Lane.Memory.Handlers;
using Lane.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The memory that outlives the recent window: a rolling summary of a conversation, and a
/// short list of durable facts about a person.
///
/// The unit is the point. v2's long-term memory embedded individual chat messages, which
/// carry so little topical content that similarity search mostly recovered *who was
/// talking* rather than what about. A fact stands on its own; "yeah lol" does not.
/// </summary>
public sealed class LongTermMemoryTests
{
    private static readonly SurfaceId Surface = new("terminal");
    private static readonly SessionId Session = new(Surface, SessionKind.Text, "local");

    private static LaneMessage Said(string speaker, string text) =>
        LaneMessage.User(Session, new Participant(new ParticipantId(Surface, speaker), speaker),
            text, DateTimeOffset.UtcNow);

    private static MemoryWrite Write(LaneMessage message) =>
        new(message, new MemoryContext(), new ScopeKey("session:test"));

    private static MemoryQuery Query() =>
        new() { Context = new MemoryContext(), Key = new ScopeKey("session:test") };

    private static ILanguageModelRegistry Registry(ScriptedLanguageModel model) =>
        new LanguageModelRegistry([model], new Dictionary<string, string> { ["summarize"] = "scripted" });

    private static SummaryHandler Summary(ScriptedLanguageModel model, int threshold = 2) =>
        new(new MemoryHandlerOptions
            {
                Id = "summary", Type = "Summary", Scope = MemoryScope.Session,
                Slot = MemorySlot.Stable, MaxMessages = threshold
            },
            Registry(model), new EmptyPromptLibrary(), new TranscriptFormatter(),
            NullLogger.Instance);

    private static ProfileHandler Profile(ScriptedLanguageModel model, int threshold = 2, int limit = 5) =>
        new(new MemoryHandlerOptions
            {
                Id = "profile", Type = "Profile", Scope = MemoryScope.User,
                Slot = MemorySlot.Stable, MaxMessages = threshold, MaxEntries = limit
            },
            Registry(model), new EmptyPromptLibrary(), new TranscriptFormatter(),
            NullLogger.Instance);

    // ---- summary -----------------------------------------------------------

    [Fact]
    public async Task Summarising_waits_until_there_is_enough_to_be_worth_a_model_call()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("They talked about tide pools.");

        SummaryHandler handler = Summary(model, threshold: 3);

        await handler.RememberAsync(Write(Said("jahan", "one")), default);
        Assert.False(handler.NeedsMaintenance);

        await handler.RememberAsync(Write(Said("jahan", "two")), default);
        await handler.RememberAsync(Write(Said("jahan", "three")), default);

        Assert.True(handler.NeedsMaintenance);
        Assert.Equal(0, model.CallCount);          // nothing spent while the turn was running
    }

    [Fact]
    public async Task The_summary_is_produced_during_maintenance_not_while_remembering()
    {
        // Messages are committed before they are delivered, so a model call inside remember
        // would sit between Lane deciding what to say and saying it.
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("They discussed anemones at length.");

        SummaryHandler handler = Summary(model);

        await handler.RememberAsync(Write(Said("jahan", "anemones are underrated")), default);
        await handler.RememberAsync(Write(Said("jahan", "especially the green ones")), default);

        Assert.Equal(MemoryRecall.Empty, await handler.RecallAsync(Query(), default));

        await handler.MaintainAsync(default);

        MemoryRecall recall = await handler.RecallAsync(Query(), default);

        Assert.Equal("They discussed anemones at length.", recall.RenderedText);
        Assert.Empty(recall.Messages);             // prose about the past, not turns to replay
    }

    [Fact]
    public async Task Each_pass_builds_on_the_last_summary()
    {
        ScriptedLanguageModel model = new((request, call) =>
            ScriptedLanguageModel.Text($"summary#{call}"));

        SummaryHandler handler = Summary(model);

        await handler.RememberAsync(Write(Said("jahan", "one")), default);
        await handler.RememberAsync(Write(Said("jahan", "two")), default);
        await handler.MaintainAsync(default);

        await handler.RememberAsync(Write(Said("jahan", "three")), default);
        await handler.RememberAsync(Write(Said("jahan", "four")), default);
        await handler.MaintainAsync(default);

        // The previous summary is handed back in so nothing is lost between passes.
        string secondPrompt = model.Requests[1].System[0].Text;

        Assert.Contains("summary#0", secondPrompt);
        Assert.Contains("three", secondPrompt);
    }

    [Fact]
    public async Task A_failed_summarisation_keeps_the_messages_for_the_next_attempt()
    {
        // Clearing the buffer on failure would lose the conversation it was meant to record.
        ScriptedLanguageModel model = new((_, call) =>
            call == 0 ? throw new InvalidOperationException("provider fell over")
                      : ScriptedLanguageModel.Text("caught up"));

        SummaryHandler handler = Summary(model);

        await handler.RememberAsync(Write(Said("jahan", "something worth keeping")), default);
        await handler.RememberAsync(Write(Said("jahan", "and more")), default);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler.MaintainAsync(default));

        Assert.True(handler.NeedsMaintenance);

        await handler.MaintainAsync(default);

        Assert.Contains("something worth keeping", model.Requests[1].System[0].Text);
        Assert.Equal("caught up", (await handler.RecallAsync(Query(), default)).RenderedText);
    }

    [Fact]
    public async Task An_empty_answer_leaves_the_previous_summary_standing()
    {
        ScriptedLanguageModel model = new((_, call) =>
            call == 0 ? ScriptedLanguageModel.Text("the real summary") : ScriptedLanguageModel.Text("   "));

        SummaryHandler handler = Summary(model);

        await handler.RememberAsync(Write(Said("jahan", "a")), default);
        await handler.RememberAsync(Write(Said("jahan", "b")), default);
        await handler.MaintainAsync(default);

        await handler.RememberAsync(Write(Said("jahan", "c")), default);
        await handler.RememberAsync(Write(Said("jahan", "d")), default);
        await handler.MaintainAsync(default);

        Assert.Equal("the real summary", (await handler.RecallAsync(Query(), default)).RenderedText);
    }

    [Fact]
    public async Task A_summary_survives_a_restart()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("what they were doing");

        SummaryHandler original = Summary(model);

        await original.RememberAsync(Write(Said("jahan", "one")), default);
        await original.RememberAsync(Write(Said("jahan", "two")), default);
        await original.MaintainAsync(default);

        JsonNode state = (await original.SaveStateAsync(default))!;

        SummaryHandler restored = Summary(model);
        await restored.LoadStateAsync(state, default);

        Assert.Equal("what they were doing", (await restored.RecallAsync(Query(), default)).RenderedText);
    }

    [Fact]
    public async Task Tool_traffic_never_reaches_the_summariser()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("summary");

        SummaryHandler handler = Summary(model, threshold: 1);

        LaneMessage toolCall = LaneMessage.Assistant(Session, Participant.Lane(Surface),
            [new ToolUsePart("c1", "web_search", JsonSerializer.SerializeToElement(new { }))],
            DateTimeOffset.UtcNow);

        await handler.RememberAsync(Write(toolCall), default);

        Assert.False(handler.NeedsMaintenance);
    }

    // ---- profile -----------------------------------------------------------

    [Fact]
    public async Task Facts_are_kept_as_standalone_statements()
    {
        // The unit that makes long-term memory work: each line means something without the
        // conversation it came from.
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing(
            "- Jahan keeps a pet cuttlefish called Marlow\n- Jahan is rewriting Lane's harness");

        ProfileHandler handler = Profile(model);

        await handler.RememberAsync(Write(Said("jahan", "my cuttlefish is called Marlow")), default);
        await handler.RememberAsync(Write(Said("jahan", "and I'm rewriting your harness")), default);
        await handler.MaintainAsync(default);

        string rendered = (await handler.RecallAsync(Query(), default)).RenderedText!;

        Assert.Contains("- Jahan keeps a pet cuttlefish called Marlow", rendered);
        Assert.Contains("- Jahan is rewriting Lane's harness", rendered);
    }

    [Fact]
    public async Task The_previous_facts_are_offered_back_so_they_can_be_corrected()
    {
        ScriptedLanguageModel model = new((_, call) => call == 0
            ? ScriptedLanguageModel.Text("- Jahan has one cuttlefish")
            : ScriptedLanguageModel.Text("- Jahan has two cuttlefish"));

        ProfileHandler handler = Profile(model);

        await handler.RememberAsync(Write(Said("jahan", "got a cuttlefish")), default);
        await handler.RememberAsync(Write(Said("jahan", "it's called Marlow")), default);
        await handler.MaintainAsync(default);

        await handler.RememberAsync(Write(Said("jahan", "got another one actually")), default);
        await handler.RememberAsync(Write(Said("jahan", "so two now")), default);
        await handler.MaintainAsync(default);

        Assert.Contains("Jahan has one cuttlefish", model.Requests[1].System[0].Text);

        // Replaced, not accumulated — otherwise the profile fills with contradictions.
        string rendered = (await handler.RecallAsync(Query(), default)).RenderedText!;

        Assert.Contains("two cuttlefish", rendered);
        Assert.DoesNotContain("one cuttlefish", rendered);
    }

    [Theory]
    [InlineData("- one\n- two", 2)]
    [InlineData("* one\n* two", 2)]
    [InlineData("1. one\n2. two", 2)]
    [InlineData("Here is the list:\n- one\n- two\nHope that helps.", 2)]
    [InlineData("- one\n- ONE\n- two", 2)]
    [InlineData("no list at all", 0)]
    public void A_models_answer_is_read_back_tolerantly(string text, int expected) =>
        Assert.Equal(expected, ProfileHandler.ParseFacts(text, 10).Count);

    [Fact]
    public void The_fact_list_is_capped()
    {
        string many = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"- fact {i}"));

        Assert.Equal(5, ProfileHandler.ParseFacts(many, 5).Count);
    }

    [Fact]
    public async Task An_empty_answer_leaves_the_known_facts_alone()
    {
        // Far likelier to be a bad call than a person about whom nothing is true.
        ScriptedLanguageModel model = new((_, call) => call == 0
            ? ScriptedLanguageModel.Text("- Jahan likes cuttlefish")
            : ScriptedLanguageModel.Text("I don't have enough to go on."));

        ProfileHandler handler = Profile(model);

        await handler.RememberAsync(Write(Said("jahan", "a")), default);
        await handler.RememberAsync(Write(Said("jahan", "b")), default);
        await handler.MaintainAsync(default);

        await handler.RememberAsync(Write(Said("jahan", "c")), default);
        await handler.RememberAsync(Write(Said("jahan", "d")), default);
        await handler.MaintainAsync(default);

        Assert.Contains("cuttlefish", (await handler.RecallAsync(Query(), default)).RenderedText!);
    }

    [Fact]
    public async Task A_profile_survives_a_restart()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("- Jahan keeps a cuttlefish called Marlow");

        ProfileHandler original = Profile(model);

        await original.RememberAsync(Write(Said("jahan", "a")), default);
        await original.RememberAsync(Write(Said("jahan", "b")), default);
        await original.MaintainAsync(default);

        ProfileHandler restored = Profile(model);
        await restored.LoadStateAsync((await original.SaveStateAsync(default))!, default);

        Assert.Contains("Marlow", (await restored.RecallAsync(Query(), default)).RenderedText!);
    }

    [Fact]
    public async Task Nothing_is_recalled_before_anything_is_known()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("- something");

        Assert.Equal(MemoryRecall.Empty, await Profile(model).RecallAsync(Query(), default));
        Assert.Equal(MemoryRecall.Empty, await Summary(model).RecallAsync(Query(), default));
    }
}
