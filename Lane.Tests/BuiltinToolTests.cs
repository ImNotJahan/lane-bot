using System.Text.Json;
using Lane.Core.Memory;
using Lane.Core.Presence;
using Lane.Core.Tools;
using Lane.Memory.Sqlite;
using Lane.Tools.Presence;
using Lane.Tools.Reading;
using Lane.Tools.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The abilities v2 dispatched with an if-chain in the orchestrator, now as ordinary
/// classes that the kernel knows nothing about.
/// </summary>
public sealed class BuiltinToolTests : IDisposable
{
    private readonly string _books = Path.Combine(Path.GetTempPath(), $"lane-books-{Guid.NewGuid():n}");

    public BuiltinToolTests()
    {
        Directory.CreateDirectory(_books);

        File.WriteAllLines(Path.Combine(_books, "sisyphus.txt"),
            ["one must imagine", "sisyphus", "happy", "", "42", "the end"]);

        File.WriteAllText(Path.Combine(_books, "wired.txt"), "hello from the wired");
    }

    private static ToolContext Context() =>
        new() { Services = new ServiceCollection().BuildServiceProvider() };

    private static ValueTask<ToolResult> Invoke(ITool tool, object args) =>
        tool.InvokeAsync(new ToolInvocation("c1", JsonSerializer.SerializeToElement(args), Context()), default);

    private IKeyValueStore Positions() =>
        new SqliteKeyValueStore(new LaneDatabase(
            new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance));

    // ---- reading -----------------------------------------------------------

    [Fact]
    public async Task Reading_advances_through_a_book_and_wraps_at_the_end()
    {
        ReadBookTool tool = new(Positions(), new BookOptions { Directory = _books },
            NullLogger<ReadBookTool>.Instance);

        // Blank lines and bare page numbers are skipped, as they were in v2.
        Assert.Equal("one must imagine\nsisyphus", (await Invoke(tool, new { title = "sisyphus", lines = 2 })).Text);
        Assert.Equal("happy\nthe end", (await Invoke(tool, new { title = "sisyphus", lines = 2 })).Text);

        // A book she has finished is one she can read again.
        Assert.Equal("one must imagine", (await Invoke(tool, new { title = "sisyphus", lines = 1 })).Text);
    }

    [Fact]
    public async Task Each_book_keeps_its_own_position()
    {
        IKeyValueStore positions = Positions();
        ReadBookTool tool = new(positions, new BookOptions { Directory = _books },
            NullLogger<ReadBookTool>.Instance);

        await Invoke(tool, new { title = "sisyphus", lines = 2 });

        Assert.Equal("hello from the wired", (await Invoke(tool, new { title = "wired", lines = 1 })).Text);
        Assert.Equal("happy", (await Invoke(tool, new { title = "sisyphus", lines = 1 })).Text);
    }

    [Fact]
    public async Task What_was_read_is_remembered_globally()
    {
        // Something Lane read belongs to her, not to whichever conversation prompted it.
        ReadBookTool tool = new(Positions(), new BookOptions { Directory = _books },
            NullLogger<ReadBookTool>.Instance);

        ToolResult result = await Invoke(tool, new { title = "sisyphus", lines = 1 });

        ToolObservation observation = Assert.Single(result.Observations);
        Assert.Equal(MemoryScopeHint.Global, observation.Scope);
        Assert.Contains("one must imagine", observation.Text);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\..\\secrets")]
    public async Task A_title_cannot_escape_the_books_directory(string title)
    {
        // Titles come from the model, so they are treated as untrusted input.
        ReadBookTool tool = new(Positions(), new BookOptions { Directory = _books },
            NullLogger<ReadBookTool>.Instance);

        ToolResult result = await Invoke(tool, new { title, lines = 1 });

        Assert.True(result.IsError);
        Assert.Contains("no book", result.Text);
    }

    [Fact]
    public async Task Listing_books_shows_what_is_on_the_shelf()
    {
        ITool tool = new ListBooksTool(new BookOptions { Directory = _books });

        ToolResult result = await Invoke(tool, new { });

        Assert.Contains("sisyphus", result.Text);
        Assert.Contains("wired", result.Text);
    }

    // ---- presence ----------------------------------------------------------

    [Fact]
    public async Task Setting_an_emoticon_publishes_it()
    {
        RecordingPresence presence = new();
        SetEmoticonTool tool = new(presence);

        ToolResult result = await Invoke(tool, new { emoticon = "( ._.)" });

        Assert.False(result.IsError);
        Assert.Equal("( ._.)", Assert.Single(presence.Published).Emoticon);
    }

    [Fact]
    public async Task An_absurdly_long_emoticon_is_refused()
    {
        RecordingPresence presence = new();
        SetEmoticonTool tool = new(presence);

        ToolResult result = await Invoke(tool, new { emoticon = new string('x', 200) });

        Assert.True(result.IsError);
        Assert.Empty(presence.Published);
    }

    private sealed class RecordingPresence : IPresenceSink
    {
        public List<PresenceChanged> Published { get; } = [];
        public void Publish(PresenceChanged change) => Published.Add(change);
    }

    // ---- web ---------------------------------------------------------------

    [Fact]
    public async Task Search_results_are_formatted_and_remembered_globally()
    {
        StubSearch search = new([new SearchHit("Tide pools", "https://example.test/a", "Rocky shore habitats.")]);

        WebSearchTool tool = new(search);

        ToolResult result = await Invoke(tool, new { query = "tide pools", count = 3 });

        Assert.Contains("Tide pools (https://example.test/a): Rocky shore habitats.", result.Text);
        Assert.Equal(3, search.LastCount);
        Assert.Equal(MemoryScopeHint.Global, Assert.Single(result.Observations).Scope);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 10)]
    public async Task The_result_count_is_clamped_to_something_sensible(int requested, int expected)
    {
        StubSearch search = new([]);
        WebSearchTool tool = new(search);

        await Invoke(tool, new { query = "x", count = requested });

        Assert.Equal(expected, search.LastCount);
    }

    [Fact]
    public async Task An_empty_query_is_refused_before_any_request_is_made()
    {
        StubSearch search = new([]);
        WebSearchTool tool = new(search);

        Assert.True((await Invoke(tool, new { query = "   " })).IsError);
        Assert.Equal(0, search.Searches);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test")]
    public async Task Fetching_refuses_anything_that_is_not_http(string url)
    {
        StubSearch search = new([]);
        FetchUrlTool tool = new(search);

        ToolResult result = await Invoke(tool, new { url });

        Assert.True(result.IsError);
        Assert.Equal(0, search.Fetches);
    }

    private sealed class StubSearch(IReadOnlyList<SearchHit> hits) : IWebSearch
    {
        public int Searches;
        public int Fetches;
        public int LastCount;

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct)
        {
            Searches++;
            LastCount = count;
            return Task.FromResult(hits);
        }

        public Task<string> FetchAsync(string url, int maxLength, CancellationToken ct)
        {
            Fetches++;
            return Task.FromResult("page text");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_books)) Directory.Delete(_books, recursive: true);
    }
}
