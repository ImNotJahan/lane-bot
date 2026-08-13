using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Memory;
using Lane.Memory.Sqlite;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The requirement the whole rewrite exists for: several surfaces, and several instances of
/// one surface, all live at once, each conversation staying its own.
///
/// v2 chose one body from an enum at startup. Everything below was unreachable.
/// </summary>
public sealed class MultiSurfaceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static MemoryHandlerOptions Recent() => new()
    {
        Id = "recent", Type = "SlidingWindow", Scope = MemoryScope.Session,
        Slot = MemorySlot.Inline, MaxMessages = 20, Kinds = [MessageKind.Utterance]
    };

    private static MemoryHandlerOptions Everywhere() => new()
    {
        Id = "everywhere", Type = "SlidingWindow", Scope = MemoryScope.Global,
        Slot = MemorySlot.Volatile, Order = 10, MaxMessages = 50, Pinned = true,
        SectionTitle = "Elsewhere", Kinds = [MessageKind.Utterance]
    };

    private static LaneHarness Harness(ScriptedLanguageModel model, params MemoryHandlerOptions[] handlers) =>
        LaneHarness.Create(model, services => services.AddLaneMemory(
            new SqliteOptions { InMemory = true }, o => o.Handlers = [.. handlers]));

    [Fact]
    public async Task Two_discord_instances_and_a_terminal_all_run_at_once_without_crosstalk()
    {
        await using LaneHarness harness = Harness(
            ScriptedLanguageModel.Transforming(text => $"heard:{text}"), Recent());

        // Note discord.main and discord.alt both use channel "general": the same channel id
        // on two different bot instances must still be two separate conversations.
        RecordingChannel mainGeneral  = harness.OpenSession("discord.main", "general",
            memoryGroup: "discord.main/guild1/general");
        RecordingChannel mainOfftopic = harness.OpenSession("discord.main", "offtopic",
            memoryGroup: "discord.main/guild1/offtopic");
        RecordingChannel altGeneral   = harness.OpenSession("discord.alt", "general",
            memoryGroup: "discord.alt/guild1/general");
        RecordingChannel terminal     = harness.OpenSession("terminal", "local",
            memoryGroup: "terminal/local");

        await Task.WhenAll(
            harness.SendAsync(mainGeneral,  "alice", "one").AsTask(),
            harness.SendAsync(mainOfftopic, "bob",   "two").AsTask(),
            harness.SendAsync(altGeneral,   "carol", "three").AsTask(),
            harness.SendAsync(terminal,     "jahan", "four").AsTask());

        Assert.Equal(["heard:one"],   await mainGeneral.WaitForAsync(1, Timeout));
        Assert.Equal(["heard:two"],   await mainOfftopic.WaitForAsync(1, Timeout));
        Assert.Equal(["heard:three"], await altGeneral.WaitForAsync(1, Timeout));
        Assert.Equal(["heard:four"],  await terminal.WaitForAsync(1, Timeout));
    }

    [Fact]
    public async Task The_same_channel_on_two_instances_keeps_two_separate_memories()
    {
        await using LaneHarness harness = Harness(ScriptedLanguageModel.Echoing("ok"), Recent());

        RecordingChannel main = harness.OpenSession("discord.main", "general",
            memoryGroup: "discord.main/guild1/general");
        RecordingChannel alt = harness.OpenSession("discord.alt", "general",
            memoryGroup: "discord.alt/guild1/general");

        await harness.SendAsync(main, "alice", "something only main heard");
        await main.WaitForAsync(1, Timeout);

        await harness.SendAsync(alt, "alice", "and now alt");
        await alt.WaitForAsync(1, Timeout);

        string inlineForAlt = string.Join("\n", harness.Model.Requests[^1].Messages.Select(m => m.TextContent));

        Assert.Contains("and now alt", inlineForAlt);
        Assert.DoesNotContain("something only main heard", inlineForAlt);
    }

    [Fact]
    public async Task Global_memory_still_spans_every_surface()
    {
        // Separate conversations, one Lane: what she was told on Discord is available in
        // the terminal, labelled so the mix stays legible.
        await using LaneHarness harness = Harness(
            ScriptedLanguageModel.Echoing("ok"), Recent(), Everywhere());

        RecordingChannel discord  = harness.OpenSession("discord.main", "general",
            memoryGroup: "discord.main/guild1/general");
        RecordingChannel terminal = harness.OpenSession("terminal", "local", memoryGroup: "terminal/local");

        await harness.SendAsync(discord, "alice", "the parcel arrived");
        await discord.WaitForAsync(1, Timeout);

        await harness.SendAsync(terminal, "jahan", "anything happen?");
        await terminal.WaitForAsync(1, Timeout);

        string system = string.Join("\n", harness.Model.Requests[^1].System.Select(s => s.Text));

        Assert.Contains("the parcel arrived", system);
        Assert.Contains("discord.main/Text/general", system);
    }

    [Fact]
    public async Task Messages_arriving_together_across_surfaces_are_each_answered_once()
    {
        // Four conversations, four replies, none duplicated and none lost.
        await using LaneHarness harness = Harness(
            ScriptedLanguageModel.Transforming(text => $"re:{text}"), Recent());

        List<RecordingChannel> channels =
        [
            harness.OpenSession("discord.main", "a", memoryGroup: "m/a"),
            harness.OpenSession("discord.main", "b", memoryGroup: "m/b"),
            harness.OpenSession("discord.alt",  "a", memoryGroup: "alt/a"),
            harness.OpenSession("terminal",     "local", memoryGroup: "t/local")
        ];

        await Task.WhenAll(channels.Select((c, i) => harness.SendAsync(c, $"user{i}", $"msg{i}").AsTask()));

        for (int i = 0; i < channels.Count; i++)
        {
            IReadOnlyList<string> sent = await channels[i].WaitForAsync(1, Timeout);

            Assert.Single(sent);
            Assert.Equal($"re:msg{i}", sent[0]);
        }
    }

    [Fact]
    public async Task Back_to_back_messages_in_one_channel_lose_nothing_while_other_surfaces_run()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Transforming(text => $"re:{text}");
        model.Delay = TimeSpan.FromMilliseconds(150);

        await using LaneHarness harness = Harness(model, Recent());

        RecordingChannel busy  = harness.OpenSession("discord.main", "general", memoryGroup: "m/general");
        RecordingChannel quiet = harness.OpenSession("terminal", "local", memoryGroup: "t/local");

        await harness.SendAsync(busy, "alice", "first");
        await Task.Delay(40);                               // land the second mid-turn
        await harness.SendAsync(busy, "alice", "second");
        await harness.SendAsync(quiet, "jahan", "meanwhile");

        IReadOnlyList<string> busyReplies = await busy.WaitForAsync(2, Timeout);

        Assert.Contains(busyReplies, r => r.Contains("first"));
        Assert.Contains(busyReplies, r => r.Contains("second"));

        Assert.Equal(["re:meanwhile"], await quiet.WaitForAsync(1, Timeout));
    }

    [Fact]
    public async Task A_surface_going_quiet_does_not_affect_the_others()
    {
        // One transport failing must not take the rest of Lane down with it.
        ScriptedLanguageModel model = new((request, _) =>
            request.Messages.Any(m => m.TextContent.Contains("boom"))
                ? throw new InvalidOperationException("that channel is broken")
                : ScriptedLanguageModel.Text("fine"));

        await using LaneHarness harness = Harness(model, Recent());

        RecordingChannel broken = harness.OpenSession("discord.main", "general", memoryGroup: "m/general");
        RecordingChannel other  = harness.OpenSession("terminal", "local", memoryGroup: "t/local");

        await harness.SendAsync(broken, "alice", "boom");
        await Task.Delay(200);

        await harness.SendAsync(other, "jahan", "still there?");

        Assert.Equal(["fine"], await other.WaitForAsync(1, Timeout));
        Assert.Empty(broken.Texts);
    }
}
