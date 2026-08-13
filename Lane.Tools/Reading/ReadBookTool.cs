using System.ComponentModel;
using System.Text;
using Lane.Core.Memory;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Reading;

public sealed class BookOptions
{
    /// <summary>Directory of <c>*.txt</c> books, relative to the assembly if not rooted.</summary>
    public string Directory { get; set; } = "Books";
}

/// <summary>
/// Reads Lane's books, a few lines at a time, remembering where she got to.
///
/// v2 kept reading positions in a dictionary on the orchestrator, serialised into the same
/// blob as memory. Here the position is just a scoped key-value entry, which is why this
/// tool needs nothing from the kernel to exist.
/// </summary>
[LaneTool]
public sealed class ReadBookTool(
    IKeyValueStore positions,
    BookOptions options,
    ILogger<ReadBookTool> log) : Tool<ReadBookTool.Args>
{
    private static readonly ScopeKey Scope = new("global");

    public sealed record Args(
        [property: Description("Title of the book, as given by list_books.")] string Title,
        [property: Description("How many lines to read, 1-40. Default 8.")] int Lines = 8);

    protected override string Name => "read_book";

    protected override string Description =>
        "Read the next few lines of one of your books, continuing from where you left off. " +
        "Call list_books first if you are not sure what you have.";

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        string[]? lines = LoadLines(args.Title);

        if (lines is null) return ToolResult.Error($"You have no book called '{args.Title}'. Try list_books.");
        if (lines.Length == 0) return ToolResult.Error($"'{args.Title}' is empty.");

        string key = $"book:{args.Title}";

        int position = await positions.GetAsync<int>(Scope, key, ct).ConfigureAwait(false);

        // Wrap rather than stop: a book Lane has finished is one she can read again.
        if (position >= lines.Length) position = 0;

        int take = Math.Clamp(args.Lines, 1, 40);
        int end  = Math.Min(position + take, lines.Length);

        StringBuilder passage = new();
        for (int i = position; i < end; i++) passage.AppendLine(lines[i]);

        await positions.SetAsync(Scope, key, end, ct).ConfigureAwait(false);

        log.LogDebug("Read {Title} lines {Start}-{End} of {Total}", args.Title, position, end, lines.Length);

        string text = passage.ToString().TrimEnd();

        return ToolResult.Ok(text)
                         .RememberAs($"[read from {args.Title}]\n{text}", MemoryScopeHint.Global);
    }

    private string[]? LoadLines(string title)
    {
        // Titles come from the model, so the path is confined to the books directory —
        // a title of "../../.env" must not read anything.
        string safe = Path.GetFileNameWithoutExtension(title);

        if (string.IsNullOrWhiteSpace(safe)) return null;

        string path = Path.Combine(Directory(), safe + ".txt");

        if (!File.Exists(path)) return null;

        return [.. File.ReadAllLines(path)
                       .Where(line => !string.IsNullOrWhiteSpace(line) && !int.TryParse(line.Trim(), out _))];
    }

    private string Directory() =>
        Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(AppContext.BaseDirectory, options.Directory);
}

[LaneTool]
public sealed class ListBooksTool(BookOptions options) : Tool<NoArgs>
{
    protected override string Name => "list_books";

    protected override string Description => "List the books you have, and how far through each one you are.";

    protected override ValueTask<ToolResult> InvokeAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        string directory = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(AppContext.BaseDirectory, options.Directory);

        if (!System.IO.Directory.Exists(directory)) return ValueTask.FromResult(ToolResult.Ok("(no books)"));

        string[] titles = [.. System.IO.Directory
            .EnumerateFiles(directory, "*.txt")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order()];

        return ValueTask.FromResult(ToolResult.Ok(
            titles.Length == 0 ? "(no books)" : string.Join("\n", titles.Select(t => $"- {t}"))));
    }
}
