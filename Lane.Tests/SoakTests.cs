using System.Diagnostics;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Sessions;
using Lane.Memory;
using Lane.Memory.Sqlite;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Running for a long time without leaking.
///
/// The plan called for a 24-hour soak across four surfaces. This is not that, and does not
/// pretend to be: it is the part of a soak that can be made deterministic and run in CI —
/// thousands of turns across many concurrent conversations, with the things that would
/// accumulate over a day checked afterwards. A day of real traffic is still worth doing
/// separately, because what it catches is provider flakiness and clock drift, which nothing
/// here simulates.
/// </summary>
[Trait("Category", "Soak")]
public sealed class SoakTests
{
    [Fact]
    public async Task Thousands_of_turns_across_many_conversations_leave_nothing_behind()
    {
        const int sessions   = 24;
        const int perSession = 60;

        string database = Path.Combine(Path.GetTempPath(), $"lane-soak-{Guid.NewGuid():n}.db");

        List<string> failures = [];

        try
        {
            await using LaneHarness harness = LaneHarness.Create(
                ScriptedLanguageModel.Transforming(said => $"re: {said}"),
                configure: services =>
                {
                    services.AddLaneMemory(new SqliteOptions { Path = database }, o =>
                    {
                        o.Handlers =
                        [
                            new MemoryHandlerOptions
                            {
                                Id = "recent", Type = "SlidingWindow", Scope = MemoryScope.Session,
                                Slot = MemorySlot.Inline, MaxMessages = 8
                            },
                            new MemoryHandlerOptions
                            {
                                Id = "global", Type = "SlidingWindow", Scope = MemoryScope.Global,
                                Slot = MemorySlot.Volatile, MaxMessages = 5, Pinned = true
                            }
                        ];

                        // Aggressive on purpose: a day's idle eviction has to happen inside a
                        // test that lasts seconds, or the thing being checked never runs.
                        o.IdleEviction  = TimeSpan.FromMilliseconds(200);
                        o.FlushInterval = TimeSpan.FromMilliseconds(100);
                        o.MaxInstances  = 64;
                    });
                },
                sessionOptions: o => o.MaxConcurrentTurns = 4);

            // A turn that fails is caught by the pump and logged rather than thrown, so the
            // only way to notice one in a soak is to watch the bus.
            using IDisposable watching = harness.Services
                .GetRequiredService<IEventBus>()
                .Subscribe<TurnFailed>(failed =>
                {
                    lock (failures) failures.Add($"{failed.Session}: {failed.Error}");
                });

            List<RecordingChannel> channels =
                [.. Enumerable.Range(0, sessions).Select(i => harness.OpenSession("soak", $"room-{i}"))];

            Stopwatch clock = Stopwatch.StartNew();

            await Task.WhenAll(channels.Select(async channel =>
            {
                for (int i = 0; i < perSession; i++)
                    await harness.SendAsync(channel, $"speaker-{i % 3}", $"message {i} in {channel.Id.LocalKey}");
            }));

            foreach (RecordingChannel channel in channels)
                await channel.WaitForAsync(1, TimeSpan.FromSeconds(60));

            await WaitUntilIdleAsync(harness, TimeSpan.FromSeconds(60));

            clock.Stop();

            // Nothing broke.
            Assert.Empty(failures);

            // Every conversation got answers, and only its own.
            foreach (RecordingChannel channel in channels)
            {
                Assert.NotEmpty(channel.Sent);

                foreach (string text in channel.Texts)
                    Assert.Contains(channel.Id.LocalKey, text);
            }

            // Memory instances were evicted rather than accumulating one per conversation
            // for the life of the process.
            MemoryHandlerPool pool = harness.Services.GetRequiredService<MemoryHandlerPool>();

            await Task.Delay(600);
            await pool.EvictIdleAsync(CancellationToken.None);

            Assert.True(pool.LiveInstances <= 16,
                $"{pool.LiveInstances} memory handler instances survived eviction.");

            // And the sessions themselves are still exactly the ones that were opened: no
            // conversation invented itself along the way.
            Assert.Equal(sessions, harness.Sessions.Active.Count);
        }
        finally
        {
            foreach (string extra in Directory.GetFiles(
                Path.GetDirectoryName(database)!, Path.GetFileName(database) + "*"))
            {
                try { File.Delete(extra); } catch (IOException) { /* still held; it is a temp file */ }
            }
        }
    }

    [Fact]
    [Trait("Category", "Soak")]
    public async Task A_conversation_hammered_from_several_directions_stays_in_order()
    {
        // The cohesion guarantee under load rather than in the quiet case: one conversation,
        // many concurrent writers, and nothing lost or reordered.
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Transforming(said => said),
            sessionOptions: o =>
            {
                o.BatchWindow = TimeSpan.Zero;
                o.MaxConcurrentTurns = 8;
            });

        RecordingChannel channel = harness.OpenSession("soak", "busy");

        const int writers = 8;
        const int each    = 40;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(async writer =>
        {
            for (int i = 0; i < each; i++)
                await harness.SendAsync(channel, $"writer-{writer}", $"{writer}:{i}");
        }));

        await WaitUntilIdleAsync(harness, TimeSpan.FromSeconds(60));

        // Coalescing means far fewer replies than messages, which is the point of it — but
        // every message must appear in what the model was shown, none dropped.
        string everything = string.Join("\n", harness.Model.Requests.SelectMany(r => r.Messages)
                                                     .Select(m => m.TextContent));

        for (int writer = 0; writer < writers; writer++)
            for (int i = 0; i < each; i++)
                Assert.Contains($"{writer}:{i}", everything);

        // Per writer, in the order that writer sent them.
        for (int writer = 0; writer < writers; writer++)
        {
            List<int> seen = [.. Enumerable.Range(0, each)
                .Select(i => everything.IndexOf($"{writer}:{i}", StringComparison.Ordinal))];

            Assert.Equal(seen.Order().ToList(), seen);
        }
    }

    private static async Task WaitUntilIdleAsync(LaneHarness harness, TimeSpan timeout)
    {
        using CancellationTokenSource deadline = new(timeout);

        while (!deadline.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None);

            if (harness.Sessions.Active.All(s => s.State == SessionState.Idle)) return;
        }

        throw new TimeoutException("Sessions never settled.");
    }
}
