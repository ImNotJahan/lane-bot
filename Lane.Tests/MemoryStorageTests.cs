using System.Text.Json;
using System.Text.Json.Nodes;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Serialization;
using Lane.Memory.Handlers;
using Lane.Memory.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class MessageSerializationTests
{
    private static readonly SessionId Session =
        new(new SurfaceId("discord.main"), SessionKind.Text, "general");

    [Fact]
    public void Every_content_part_survives_a_round_trip()
    {
        // Tool parts especially: losing a call id or a thinking signature makes the next
        // request to the provider invalid, and only at that later point does it show up.
        Participant author = new(new ParticipantId(new SurfaceId("discord.main"), "1"), "alice", "jahan");

        LaneMessage original = new()
        {
            Id        = MessageId.New(),
            Session   = Session,
            Role      = LaneRole.Assistant,
            Kind      = MessageKind.Utterance,
            Author    = author,
            Timestamp = DateTimeOffset.UtcNow,
            Sequence  = 42,
            ExternalId = "discord-999",
            Content =
            [
                new TextPart("hello"),
                new ImagePart(new Uri("https://example.test/a.png"), null, "image/png"),
                new ToolUsePart("call_1", "web_search", JsonSerializer.SerializeToElement(new { q = "tide pools" })),
                new ToolResultPart("call_1", [new TextPart("results")], false),
                new ThinkingPart("hmm", "sig-abc")
            ]
        };

        string json = JsonSerializer.Serialize(LaneJson.ToStored(original), LaneJson.Options);
        LaneMessage restored = LaneJson.FromStored(
            JsonSerializer.Deserialize<StoredMessage>(json, LaneJson.Options)!);

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Session, restored.Session);
        Assert.Equal(original.Role, restored.Role);
        Assert.Equal(original.Sequence, restored.Sequence);
        Assert.Equal(original.ExternalId, restored.ExternalId);
        Assert.Equal("jahan", restored.Author.GlobalUserId);

        Assert.Equal("hello", Assert.IsType<TextPart>(restored.Content[0]).Text);
        Assert.Equal("image/png", Assert.IsType<ImagePart>(restored.Content[1]).MediaType);

        ToolUsePart tool = Assert.IsType<ToolUsePart>(restored.Content[2]);
        Assert.Equal("call_1", tool.ToolCallId);
        Assert.Equal("tide pools", tool.Arguments.GetProperty("q").GetString());

        Assert.Equal("call_1", Assert.IsType<ToolResultPart>(restored.Content[3]).ToolCallId);
        Assert.Equal("sig-abc", Assert.IsType<ThinkingPart>(restored.Content[4]).Signature);
    }

    [Fact]
    public void Unreadable_content_yields_nothing_rather_than_throwing()
    {
        // A part type written by a newer build must not stop an older one from starting.
        Assert.Empty(LaneJson.DeserializeContent("""[{"$kind":"from_the_future","x":1}]"""));
        Assert.Empty(LaneJson.DeserializeContent("not json at all"));
    }
}

public sealed class SlidingWindowTests
{
    private static readonly SessionId Session =
        new(new SurfaceId("terminal"), SessionKind.Text, "local");

    private static MemoryHandlerOptions Options(
        int max = 3, MessageKind[]? kinds = null, MemoryScope scope = MemoryScope.Session) =>
        new() { Id = "recent", Type = "SlidingWindow", Scope = scope, Slot = MemorySlot.Inline,
                MaxMessages = max, Kinds = kinds };

    private static LaneMessage Message(string text, MessageKind kind = MessageKind.Utterance) => new()
    {
        Id        = MessageId.New(),
        Session   = Session,
        Role      = LaneRole.User,
        Kind      = kind,
        Author    = new Participant(new ParticipantId(new SurfaceId("terminal"), "jahan"), "jahan"),
        Content   = [new TextPart(text)],
        Timestamp = DateTimeOffset.UtcNow
    };

    private static MemoryWrite Write(LaneMessage message) =>
        new(message, new MemoryContext(), new ScopeKey("session:test"));

    private static MemoryQuery Query(int limit = 50, MessageKind[]? kinds = null) =>
        new() { Context = new MemoryContext(), Key = new ScopeKey("session:test"), Limit = limit, Kinds = kinds };

    [Fact]
    public async Task The_window_keeps_only_the_most_recent_messages()
    {
        SlidingWindowHandler handler = new(Options(max: 3));

        foreach (string text in (string[])["one", "two", "three", "four"])
            await handler.RememberAsync(Write(Message(text)), default);

        MemoryRecall recall = await handler.RecallAsync(Query(), default);

        Assert.Equal(["two", "three", "four"], recall.Messages.Select(m => m.TextContent));
    }

    [Fact]
    public async Task A_kind_filter_decides_what_the_handler_stores()
    {
        // This is what replaced v2's ForThoughts boolean and its two near-identical configs.
        SlidingWindowHandler thoughts = new(Options(max: 10, kinds: [MessageKind.Thought]));

        await thoughts.RememberAsync(Write(Message("said out loud")), default);
        await thoughts.RememberAsync(Write(Message("thought quietly", MessageKind.Thought)), default);

        MemoryRecall recall = await thoughts.RecallAsync(Query(), default);

        Assert.Equal(["thought quietly"], recall.Messages.Select(m => m.TextContent));
    }

    [Fact]
    public async Task State_round_trips_through_save_and_load()
    {
        SlidingWindowHandler original = new(Options(max: 5));

        await original.RememberAsync(Write(Message("first")), default);
        await original.RememberAsync(Write(Message("second")), default);

        JsonNode state = (await original.SaveStateAsync(default))!;

        SlidingWindowHandler restored = new(Options(max: 5));
        await restored.LoadStateAsync(state, default);

        MemoryRecall recall = await restored.RecallAsync(Query(), default);

        Assert.Equal(["first", "second"], recall.Messages.Select(m => m.TextContent));
    }

    [Fact]
    public async Task Loading_honours_a_configuration_that_has_since_tightened()
    {
        // Saved with everything, reloaded by a build configured for thoughts only. The
        // current configuration wins rather than reviving messages it no longer wants.
        SlidingWindowHandler permissive = new(Options(max: 10));

        await permissive.RememberAsync(Write(Message("chatter")), default);
        await permissive.RememberAsync(Write(Message("a thought", MessageKind.Thought)), default);

        JsonNode state = (await permissive.SaveStateAsync(default))!;

        SlidingWindowHandler strict = new(Options(max: 10, kinds: [MessageKind.Thought]));
        await strict.LoadStateAsync(state, default);

        MemoryRecall recall = await strict.RecallAsync(Query(), default);

        Assert.Equal(["a thought"], recall.Messages.Select(m => m.TextContent));
    }

    [Fact]
    public async Task Loading_a_larger_saved_window_trims_to_the_configured_size()
    {
        SlidingWindowHandler big = new(Options(max: 10));

        foreach (string text in (string[])["a", "b", "c", "d"])
            await big.RememberAsync(Write(Message(text)), default);

        SlidingWindowHandler small = new(Options(max: 2));
        await small.LoadStateAsync((await big.SaveStateAsync(default))!, default);

        MemoryRecall recall = await small.RecallAsync(Query(), default);

        Assert.Equal(["c", "d"], recall.Messages.Select(m => m.TextContent));
    }
}

public sealed class SqliteStoreTests
{
    private static LaneDatabase InMemory() =>
        new(new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance);

    private static readonly SessionId Session =
        new(new SurfaceId("discord.main"), SessionKind.Text, "general");

    private static LaneMessage Message(string text, SessionId? session = null) => new()
    {
        Id        = MessageId.New(),
        Session   = session ?? Session,
        Role      = LaneRole.User,
        Kind      = MessageKind.Utterance,
        Author    = new Participant(new ParticipantId(new SurfaceId("discord.main"), "1"), "alice", "jahan"),
        Content   = [new TextPart(text)],
        Timestamp = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Handler_state_saves_loads_and_overwrites()
    {
        LaneDatabase database = InMemory();
        SqliteStateStore store = new(database, NullLogger<SqliteStateStore>.Instance);

        ScopeKey key = new("session:discord.main/general");

        Assert.Null(await store.LoadAsync("recent", key, default));

        await store.SaveAsync("recent", key, JsonNode.Parse("""{"n":1}""")!, 1, default);
        Assert.Equal(1, (int)(await store.LoadAsync("recent", key, default))!["n"]!);

        await store.SaveAsync("recent", key, JsonNode.Parse("""{"n":2}""")!, 1, default);
        Assert.Equal(2, (int)(await store.LoadAsync("recent", key, default))!["n"]!);

        await store.DeleteAsync("recent", key, default);
        Assert.Null(await store.LoadAsync("recent", key, default));
    }

    [Fact]
    public async Task State_is_keyed_by_handler_and_scope_together()
    {
        LaneDatabase database = InMemory();
        SqliteStateStore store = new(database, NullLogger<SqliteStateStore>.Instance);

        await store.SaveAsync("recent", new ScopeKey("session:a"), JsonNode.Parse("""{"v":"a"}""")!, 1, default);
        await store.SaveAsync("recent", new ScopeKey("session:b"), JsonNode.Parse("""{"v":"b"}""")!, 1, default);
        await store.SaveAsync("thoughts", new ScopeKey("session:a"), JsonNode.Parse("""{"v":"t"}""")!, 1, default);

        Assert.Equal("a", (string?)(await store.LoadAsync("recent", new ScopeKey("session:a"), default))!["v"]);
        Assert.Equal("b", (string?)(await store.LoadAsync("recent", new ScopeKey("session:b"), default))!["v"]);
        Assert.Equal("t", (string?)(await store.LoadAsync("thoughts", new ScopeKey("session:a"), default))!["v"]);
    }

    [Fact]
    public async Task The_transcript_assigns_increasing_sequences_and_reads_back_in_order()
    {
        SqliteTranscriptStore store = new(InMemory());

        long first  = await store.AppendAsync(Message("one"), default);
        long second = await store.AppendAsync(Message("two"), default);

        Assert.True(second > first);

        IReadOnlyList<LaneMessage> read = await store.ReadAsync(
            new TranscriptQuery { Session = Session, Limit = 10 }, default);

        // Oldest first, whichever end the query started from.
        Assert.Equal(["one", "two"], read.Select(m => m.TextContent));
        Assert.Equal(first, read[0].Sequence);
    }

    [Fact]
    public async Task The_transcript_filters_by_session()
    {
        SqliteTranscriptStore store = new(InMemory());

        SessionId other = new(new SurfaceId("discord.main"), SessionKind.Text, "offtopic");

        await store.AppendAsync(Message("in general", Session), default);
        await store.AppendAsync(Message("in offtopic", other), default);

        IReadOnlyList<LaneMessage> read = await store.ReadAsync(
            new TranscriptQuery { Session = Session, Limit = 10 }, default);

        Assert.Equal(["in general"], read.Select(m => m.TextContent));
    }

    [Fact]
    public async Task The_transcript_returns_the_most_recent_when_limited()
    {
        SqliteTranscriptStore store = new(InMemory());

        foreach (string text in (string[])["a", "b", "c", "d"])
            await store.AppendAsync(Message(text), default);

        IReadOnlyList<LaneMessage> read = await store.ReadAsync(
            new TranscriptQuery { Session = Session, Limit = 2 }, default);

        Assert.Equal(["c", "d"], read.Select(m => m.TextContent));
    }

    [Fact]
    public async Task Key_values_are_scoped_and_typed()
    {
        SqliteKeyValueStore store = new(InMemory());

        ScopeKey global = new("global");
        ScopeKey session = new("session:x");

        await store.SetAsync(global, "book:sisyphus", 12, default);
        await store.SetAsync(session, "book:sisyphus", 99, default);

        Assert.Equal(12, await store.GetAsync<int>(global, "book:sisyphus", default));
        Assert.Equal(99, await store.GetAsync<int>(session, "book:sisyphus", default));
        Assert.Equal(0, await store.GetAsync<int>(global, "missing", default));

        await store.RemoveAsync(global, "book:sisyphus", default);
        Assert.Equal(0, await store.GetAsync<int>(global, "book:sisyphus", default));
    }
}
