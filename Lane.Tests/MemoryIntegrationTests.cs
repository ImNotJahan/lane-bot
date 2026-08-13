using Lane.Core.Memory;
using Lane.Core.Models;
using Lane.Memory;
using Lane.Memory.Sqlite;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Memory driven through the whole kernel, which is the only place the scope decisions
/// actually show up: what ends up in a request, and what survives a restart.
/// </summary>
public sealed class MemoryIntegrationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"lane-test-{Guid.NewGuid():n}.db");

    private static MemoryHandlerOptions Recent() => new()
    {
        Id = "recent", Type = "SlidingWindow", Scope = MemoryScope.Session,
        Slot = MemorySlot.Inline, Order = 0, MaxMessages = 20,
        Kinds = [Core.Messages.MessageKind.Utterance]
    };

    private static MemoryHandlerOptions Everything() => new()
    {
        Id = "everything", Type = "SlidingWindow", Scope = MemoryScope.Global,
        Slot = MemorySlot.Volatile, Order = 10, MaxMessages = 50, Pinned = true,
        SectionTitle = "Elsewhere", Kinds = [Core.Messages.MessageKind.Utterance]
    };

    private LaneHarness CreateHarness(
        ScriptedLanguageModel model,
        bool onDisk,
        params MemoryHandlerOptions[] handlers) =>
        LaneHarness.Create(model, services => services.AddLaneMemory(
            onDisk ? new SqliteOptions { Path = _databasePath } : new SqliteOptions { InMemory = true },
            o => o.Handlers = [.. handlers]));

    [Fact]
    public async Task Session_windows_stay_separate_while_a_global_handler_sees_everything()
    {
        await using LaneHarness harness = CreateHarness(
            ScriptedLanguageModel.Transforming(text => $"ack:{text}"),
            onDisk: false, Recent(), Everything());

        RecordingChannel a = harness.OpenSession("discord.main", "alpha", memoryGroup: "g/alpha");
        RecordingChannel b = harness.OpenSession("discord.main", "beta",  memoryGroup: "g/beta");

        // Sequenced: delivery happens after persistence, so awaiting each reply means the
        // next turn genuinely sees the previous one's memory.
        await harness.SendAsync(a, "alice", "alpha one");
        await a.WaitForAsync(1, Timeout);

        await harness.SendAsync(b, "bob", "beta one");
        await b.WaitForAsync(1, Timeout);

        await harness.SendAsync(a, "alice", "alpha two");
        await a.WaitForAsync(2, Timeout);

        ModelRequest lastTurnInA = harness.Model.Requests[^1];

        string inline = string.Join("\n", lastTurnInA.Messages.Select(m => m.TextContent));

        // The session window carries alpha's history and nothing of beta's.
        Assert.Contains("alpha one", inline);
        Assert.Contains("alpha two", inline);
        Assert.DoesNotContain("beta one", inline);

        // The global handler saw both, and lands in a system block rather than inline.
        string system = string.Join("\n", lastTurnInA.System.Select(s => s.Text));

        Assert.Contains("alpha one", system);
        Assert.Contains("beta one", system);
    }

    [Fact]
    public async Task Global_memory_is_labelled_by_session_so_mixed_conversations_stay_legible()
    {
        await using LaneHarness harness = CreateHarness(
            ScriptedLanguageModel.Echoing("ok"), onDisk: false, Recent(), Everything());

        RecordingChannel a = harness.OpenSession("discord.main", "alpha", memoryGroup: "g/alpha");
        RecordingChannel b = harness.OpenSession("discord.main", "beta",  memoryGroup: "g/beta");

        await harness.SendAsync(a, "alice", "from alpha");
        await a.WaitForAsync(1, Timeout);

        await harness.SendAsync(b, "bob", "from beta");
        await b.WaitForAsync(1, Timeout);

        string system = string.Join("\n", harness.Model.Requests[^1].System.Select(s => s.Text));

        // Without the label, a global block reads as one conversation that never happened.
        Assert.Contains("discord.main/Text/alpha", system);
        Assert.Contains("Elsewhere", system);
    }

    [Fact]
    public async Task A_conversation_continues_after_a_restart()
    {
        // The M1 requirement: stop the process, start it again, and Lane still knows what
        // was said. Two harnesses over one database file stand in for two runs.
        await using (LaneHarness first = CreateHarness(
            ScriptedLanguageModel.Echoing("noted"), onDisk: true, Recent()))
        {
            RecordingChannel channel = first.OpenSession("terminal", "local", memoryGroup: "terminal/local");

            await first.SendAsync(channel, "jahan", "my favourite animal is the cuttlefish");
            await channel.WaitForAsync(1, Timeout);

            await first.Services.GetRequiredService<IMemoryService>().FlushAsync(default);
        }

        await using LaneHarness second = CreateHarness(
            ScriptedLanguageModel.Echoing("still here"), onDisk: true, Recent());

        RecordingChannel restored = second.OpenSession("terminal", "local", memoryGroup: "terminal/local");

        await second.SendAsync(restored, "jahan", "what did I say?");
        await restored.WaitForAsync(1, Timeout);

        string inline = string.Join("\n", second.Model.Requests[^1].Messages.Select(m => m.TextContent));

        Assert.Contains("cuttlefish", inline);
        Assert.Contains("noted", inline);          // Lane's own reply came back too
    }

    [Fact]
    public async Task A_different_conversation_does_not_inherit_the_restored_one()
    {
        await using (LaneHarness first = CreateHarness(
            ScriptedLanguageModel.Echoing("noted"), onDisk: true, Recent()))
        {
            RecordingChannel channel = first.OpenSession("terminal", "local", memoryGroup: "terminal/local");

            await first.SendAsync(channel, "jahan", "a secret");
            await channel.WaitForAsync(1, Timeout);

            await first.Services.GetRequiredService<IMemoryService>().FlushAsync(default);
        }

        await using LaneHarness second = CreateHarness(
            ScriptedLanguageModel.Echoing("hello"), onDisk: true, Recent());

        RecordingChannel elsewhere = second.OpenSession("discord.main", "general", memoryGroup: "g/general");

        await second.SendAsync(elsewhere, "alice", "hi");
        await elsewhere.WaitForAsync(1, Timeout);

        string inline = string.Join("\n", second.Model.Requests[^1].Messages.Select(m => m.TextContent));

        Assert.DoesNotContain("a secret", inline);
    }

    [Fact]
    public async Task User_scoped_memory_marked_direct_only_never_reaches_a_group_channel()
    {
        // Recalling what someone said in a DM into a busy channel is a disclosure, so the
        // guard has to hold on the write side too — the group message must not even enter it.
        MemoryHandlerOptions aboutYou = new()
        {
            Id = "aboutyou", Type = "SlidingWindow", Scope = MemoryScope.User,
            Slot = MemorySlot.Stable, Order = 20, MaxMessages = 20,
            SectionTitle = "About them", DirectSessionsOnly = true,
            Kinds = [Core.Messages.MessageKind.Utterance]
        };

        await using LaneHarness harness = CreateHarness(
            ScriptedLanguageModel.Echoing("ok"), onDisk: false, Recent(), aboutYou);

        RecordingChannel dm = harness.OpenSession("discord.main", "dm-alice", memoryGroup: "g/dm-alice");
        RecordingChannel group = harness.OpenSession("discord.main", "general", memoryGroup: "g/general");

        // Mark the DM as a 1:1 conversation.
        harness.Sessions.GetOrCreate(harness.Sessions.TryGet(dm.Id, out Core.Sessions.Session? session)
            ? session.Descriptor with { IsDirect = true }
            : throw new InvalidOperationException("session missing"));

        await harness.SendAsync(dm, "alice", "I am afraid of moths");
        await dm.WaitForAsync(1, Timeout);

        await harness.SendAsync(group, "alice", "hey everyone");
        await group.WaitForAsync(1, Timeout);

        string system = string.Join("\n", harness.Model.Requests[^1].System.Select(s => s.Text));

        Assert.DoesNotContain("moths", system);
        Assert.DoesNotContain("About them", system);
    }

    [Fact]
    public async Task Handler_instances_are_created_per_scope_key()
    {
        await using LaneHarness harness = CreateHarness(
            ScriptedLanguageModel.Echoing("ok"), onDisk: false, Recent(), Everything());

        RecordingChannel a = harness.OpenSession("discord.main", "alpha", memoryGroup: "g/alpha");
        RecordingChannel b = harness.OpenSession("discord.main", "beta",  memoryGroup: "g/beta");

        await harness.SendAsync(a, "alice", "one");
        await a.WaitForAsync(1, Timeout);

        await harness.SendAsync(b, "bob", "two");
        await b.WaitForAsync(1, Timeout);

        // Two session-scoped instances plus one shared global instance.
        Assert.Equal(3, harness.Services.GetRequiredService<MemoryHandlerPool>().LiveInstances);
    }

    public void Dispose()
    {
        foreach (string file in (string[])[_databasePath, _databasePath + "-wal", _databasePath + "-shm"])
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
