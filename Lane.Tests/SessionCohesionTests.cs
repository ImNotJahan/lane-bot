using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Testing;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The requirement that motivated the rewrite: several conversations live at once, each
/// staying its own conversation.
/// </summary>
public sealed class SessionCohesionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Replies_go_only_to_the_session_that_produced_them()
    {
        // The model echoes back whatever it was told, so a leak between sessions is visible
        // in the text rather than having to be inferred.
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Transforming(text => $"heard:{text}"));

        RecordingChannel general = harness.OpenSession("discord.main", "general");
        RecordingChannel offtopic = harness.OpenSession("discord.main", "offtopic");
        RecordingChannel terminal = harness.OpenSession("terminal", "local");

        await harness.SendAsync(general,  "alice", "one");
        await harness.SendAsync(offtopic, "bob",   "two");
        await harness.SendAsync(terminal, "jahan", "three");

        Assert.Equal(["heard:one"],   await general.WaitForAsync(1, Timeout));
        Assert.Equal(["heard:two"],   await offtopic.WaitForAsync(1, Timeout));
        Assert.Equal(["heard:three"], await terminal.WaitForAsync(1, Timeout));
    }

    [Fact]
    public async Task Two_messages_arriving_together_are_coalesced_into_one_turn()
    {
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Echoing("ack"),
            sessionOptions: o => o.BatchWindow = TimeSpan.FromMilliseconds(120));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "alice", "first");
        await harness.SendAsync(channel, "alice", "second");

        IReadOnlyList<string> sent = await channel.WaitForAsync(1, Timeout);

        // Give a second reply a chance to appear before asserting there wasn't one.
        await Task.Delay(300);

        Assert.Single(sent);
        Assert.Equal(1, harness.Model.CallCount);

        // Both messages still reached the model — coalescing must not mean discarding.
        string[] texts = [.. harness.Model.Requests[0].Messages.Select(m => m.TextContent)];
        Assert.Contains("first", texts);
        Assert.Contains("second", texts);
    }

    [Fact]
    public async Task A_message_arriving_mid_turn_is_answered_rather_than_dropped()
    {
        // v2's isResponding flag silently discarded this message. It must now queue.
        ScriptedLanguageModel model = ScriptedLanguageModel.Transforming(text => $"re:{text}");
        model.Delay = TimeSpan.FromMilliseconds(250);

        await using LaneHarness harness = LaneHarness.Create(model);

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "alice", "first");

        await Task.Delay(60);                       // land the second message mid-turn

        await harness.SendAsync(channel, "alice", "second");

        IReadOnlyList<string> sent = await channel.WaitForAsync(2, Timeout);

        Assert.Equal(2, sent.Count);
        Assert.Contains(sent, s => s.Contains("first"));
        Assert.Contains(sent, s => s.Contains("second"));
    }

    [Fact]
    public async Task One_session_never_runs_two_turns_at_once()
    {
        int concurrent = 0, peak = 0;

        ScriptedLanguageModel model = new((_, _) =>
        {
            int now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);

            Thread.Sleep(40);

            Interlocked.Decrement(ref concurrent);
            return ScriptedLanguageModel.Text("ok");
        });

        await using LaneHarness harness = LaneHarness.Create(model);

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        for (int i = 0; i < 8; i++) await harness.SendAsync(channel, "alice", $"msg{i}");

        await channel.WaitForAsync(1, Timeout);

        // Everything queued behind the first turn, so the peak must never exceed one.
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task Different_sessions_run_concurrently()
    {
        using SemaphoreSlim gate = new(0);
        int entered = 0;

        ScriptedLanguageModel model = new((_, _) =>
        {
            // Each turn blocks until another turn has also entered. If sessions were
            // serialised against one another this would deadlock and time out.
            if (Interlocked.Increment(ref entered) == 2) gate.Release(2);
            else gate.Wait(TimeSpan.FromSeconds(4));

            return ScriptedLanguageModel.Text("ok");
        });

        await using LaneHarness harness = LaneHarness.Create(model);

        RecordingChannel a = harness.OpenSession("discord.main", "a");
        RecordingChannel b = harness.OpenSession("discord.main", "b");

        await harness.SendAsync(a, "alice", "hello");
        await harness.SendAsync(b, "bob",   "hello");

        await a.WaitForAsync(1, Timeout);
        await b.WaitForAsync(1, Timeout);

        Assert.Equal(2, entered);
    }

    [Fact]
    public async Task Concurrent_turns_are_capped_by_the_shared_budget()
    {
        int concurrent = 0, peak = 0;

        ScriptedLanguageModel model = new((_, _) =>
        {
            int now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);

            Thread.Sleep(60);

            Interlocked.Decrement(ref concurrent);
            return ScriptedLanguageModel.Text("ok");
        });

        await using LaneHarness harness = LaneHarness.Create(model, sessionOptions: o => o.MaxConcurrentTurns = 2);

        List<RecordingChannel> channels = [];

        for (int i = 0; i < 6; i++) channels.Add(harness.OpenSession("discord.main", $"c{i}"));

        foreach (RecordingChannel channel in channels) await harness.SendAsync(channel, "alice", "hello");

        foreach (RecordingChannel channel in channels) await channel.WaitForAsync(1, Timeout);

        Assert.InRange(peak, 1, 2);
    }

    [Fact]
    public async Task A_failing_turn_does_not_stop_the_session_from_answering_the_next_one()
    {
        ScriptedLanguageModel model = new((request, call) =>
            call == 0
                ? throw new InvalidOperationException("provider exploded")
                : ScriptedLanguageModel.Text("recovered"));

        await using LaneHarness harness = LaneHarness.Create(model);

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "alice", "first");
        await Task.Delay(150);
        await harness.SendAsync(channel, "alice", "second");

        Assert.Equal(["recovered"], await channel.WaitForAsync(1, Timeout));
    }

    [Fact]
    public async Task A_volunteered_utterance_is_delivered_through_the_named_session()
    {
        // This is the shape the monologue's speak_to_session tool uses: post work onto the
        // session queue, never write to a channel directly.
        await using LaneHarness harness = LaneHarness.Create();

        RecordingChannel general  = harness.OpenSession("discord.main", "general");
        RecordingChannel offtopic = harness.OpenSession("discord.main", "offtopic");

        await harness.Kernel.PostAsync(
            general.Id,
            new SessionWorkItem.Speak("I was just thinking about tide pools.", DeliveryTarget.Primary, "monologue"));

        Assert.Equal(["I was just thinking about tide pools."], await general.WaitForAsync(1, Timeout));

        await Task.Delay(150);
        Assert.Empty(offtopic.Texts);

        // A directive is Lane's own words — it must not cost a model call.
        Assert.Equal(0, harness.Model.CallCount);
    }

    [Fact]
    public void Session_ids_round_trip_through_their_string_form()
    {
        SessionId id = new(new SurfaceId("discord.main"), SessionKind.Voice, "guild/123/channel/456");

        Assert.True(SessionId.TryParse(id.Value, out SessionId? parsed));
        Assert.Equal(id, parsed);

        Assert.False(SessionId.TryParse("nonsense", out _));
        Assert.False(SessionId.TryParse("discord.main/NotAKind/x", out _));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);

        while (value > seen)
        {
            int actual = Interlocked.CompareExchange(ref target, value, seen);
            if (actual == seen) return;
            seen = actual;
        }
    }
}
