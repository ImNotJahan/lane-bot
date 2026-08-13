using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Host.Migration;
using Lane.Memory.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Bringing what v2 remembered across.
///
/// Only the transcript moves. v2's handler state is a snapshot of windows whose shapes have
/// all changed, so replaying it into the new handlers would be rebuilding a cache; the
/// durable thing is what was actually said, and the new handlers rebuild from that.
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lane-v2-{Guid.NewGuid():n}.json");

    private static readonly SessionId Target =
        new(new SurfaceId("terminal"), SessionKind.Text, "local");

    /// <summary>The shape v2 actually wrote: handler ids holding MessageContainer objects.</summary>
    private const string V2Data =
        """
        {
          "MessageWindow": [
            { "content": "jahan says: my cuttlefish is called Marlow", "author": 0, "type": 0,
              "time": "2026-08-01T10:00:00Z" },
            { "content": "Noted. Marlow it is.", "author": 1, "type": 0,
              "time": "2026-08-01T10:00:05Z" }
          ],
          "ThoughtWindow": [
            { "content": "Nobody has said anything for a while.", "author": 1, "type": 2,
              "time": "2026-08-01T10:05:00Z" }
          ],
          "RAG": [
            { "content": "jahan says: my cuttlefish is called Marlow", "author": 0, "type": 0,
              "time": "2026-08-01T10:00:00Z" }
          ],
          "BookPositions": { "myth_of_sisyphus": 42, "the_pupa_woman": 7 }
        }
        """;

    private (V2Migrator Migrator, SqliteTranscriptStore Transcript, SqliteKeyValueStore Keys) Build()
    {
        LaneDatabase database = new(new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance);

        SqliteTranscriptStore transcript = new(database);
        SqliteKeyValueStore   keys       = new(database);

        return (new V2Migrator(transcript, keys, NullLogger<V2Migrator>.Instance), transcript, keys);
    }

    [Fact]
    public async Task Messages_thoughts_and_book_positions_all_come_across()
    {
        File.WriteAllText(_path, V2Data);

        (V2Migrator migrator, SqliteTranscriptStore transcript, SqliteKeyValueStore keys) = Build();

        MigrationReport report = await migrator.MigrateAsync(_path, Target, dryRun: false, default);

        Assert.Equal(2, report.Messages);
        Assert.Equal(1, report.Thoughts);
        Assert.Equal(2, report.BookPositions);

        IReadOnlyList<LaneMessage> logged = await transcript.ReadAsync(new TranscriptQuery { Limit = 50 }, default);

        Assert.Equal(3, logged.Count);
        Assert.Equal(42, await keys.GetAsync<int>(new ScopeKey("global"), "book:myth_of_sisyphus", default));
    }

    [Fact]
    public async Task The_same_message_held_by_several_handlers_arrives_once()
    {
        // v2 kept a message in a sliding window and the RAG write buffer at the same time.
        File.WriteAllText(_path, V2Data);

        (V2Migrator migrator, SqliteTranscriptStore transcript, _) = Build();

        await migrator.MigrateAsync(_path, Target, dryRun: false, default);

        IReadOnlyList<LaneMessage> logged = await transcript.ReadAsync(new TranscriptQuery { Limit = 50 }, default);

        Assert.Single(logged, m => m.TextContent.Contains("Marlow it is") is false &&
                                    m.TextContent.Contains("cuttlefish is called Marlow"));
    }

    [Fact]
    public async Task Who_said_what_is_preserved()
    {
        File.WriteAllText(_path, V2Data);

        (V2Migrator migrator, SqliteTranscriptStore transcript, _) = Build();

        await migrator.MigrateAsync(_path, Target, dryRun: false, default);

        IReadOnlyList<LaneMessage> logged = await transcript.ReadAsync(new TranscriptQuery { Limit = 50 }, default);

        LaneMessage fromPerson = Assert.Single(logged, m => m.TextContent.Contains("cuttlefish is called"));
        LaneMessage fromLane   = Assert.Single(logged, m => m.TextContent.Contains("Marlow it is"));

        Assert.Equal(LaneRole.User, fromPerson.Role);
        Assert.Equal(LaneRole.Assistant, fromLane.Role);
        Assert.True(fromLane.Author.IsLane);
    }

    [Fact]
    public async Task Thoughts_keep_belonging_to_no_conversation()
    {
        File.WriteAllText(_path, V2Data);

        (V2Migrator migrator, SqliteTranscriptStore transcript, _) = Build();

        await migrator.MigrateAsync(_path, Target, dryRun: false, default);

        IReadOnlyList<LaneMessage> logged = await transcript.ReadAsync(new TranscriptQuery { Limit = 50 }, default);

        LaneMessage thought = Assert.Single(logged, m => m.Kind == MessageKind.Thought);

        Assert.Null(thought.Session);

        // Everything else lands in the session the migration was pointed at.
        Assert.All(logged.Where(m => m.Kind != MessageKind.Thought), m => Assert.Equal(Target, m.Session));
    }

    [Fact]
    public async Task Messages_arrive_in_the_order_they_were_said()
    {
        File.WriteAllText(_path, V2Data);

        (V2Migrator migrator, SqliteTranscriptStore transcript, _) = Build();

        await migrator.MigrateAsync(_path, Target, dryRun: false, default);

        IReadOnlyList<LaneMessage> logged = await transcript.ReadAsync(new TranscriptQuery { Limit = 50 }, default);

        Assert.Equal(logged.OrderBy(m => m.Timestamp).Select(m => m.TextContent), logged.Select(m => m.TextContent));
    }

    [Fact]
    public async Task A_dry_run_reports_without_writing_anything()
    {
        File.WriteAllText(_path, V2Data);

        (V2Migrator migrator, SqliteTranscriptStore transcript, SqliteKeyValueStore keys) = Build();

        MigrationReport report = await migrator.MigrateAsync(_path, Target, dryRun: true, default);

        Assert.Equal(2, report.Messages);

        Assert.Empty(await transcript.ReadAsync(new TranscriptQuery { Limit = 50 }, default));
        Assert.Equal(0, await keys.GetAsync<int>(new ScopeKey("global"), "book:myth_of_sisyphus", default));
    }

    [Fact]
    public async Task Malformed_entries_are_counted_and_skipped_rather_than_failing_the_run()
    {
        File.WriteAllText(_path,
            """
            {
              "MessageWindow": [
                { "content": "a real one", "author": 0, "type": 0, "time": "2026-08-01T10:00:00Z" },
                { "author": 0, "type": 0 },
                { "content": "   ", "author": 0, "type": 0 },
                "not even an object"
              ]
            }
            """);

        (V2Migrator migrator, SqliteTranscriptStore transcript, _) = Build();

        MigrationReport report = await migrator.MigrateAsync(_path, Target, dryRun: false, default);

        Assert.Equal(1, report.Messages);
        Assert.Equal(3, report.Skipped);

        Assert.Single(await transcript.ReadAsync(new TranscriptQuery { Limit = 50 }, default));
    }

    [Fact]
    public async Task A_missing_file_says_so_plainly()
    {
        (V2Migrator migrator, _, _) = Build();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => migrator.MigrateAsync("/no/such/data.json", Target, dryRun: true, default));
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
