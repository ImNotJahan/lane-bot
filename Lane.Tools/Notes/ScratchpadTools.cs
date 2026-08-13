using System.ComponentModel;
using System.Text;
using Lane.Core.Memory;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Notes;

/// <summary>One note: what Lane called it, what it says, and when she last touched it.</summary>
public sealed record Note(string Title, string Text, DateTimeOffset UpdatedAt);

/// <summary>
/// Shared rules for the scratchpad, kept in one place so the four tools cannot disagree
/// about what a title means or how large a note may get.
/// </summary>
internal static class Scratchpad
{
    /// <summary>
    /// Global, like book positions. A note is something Lane wrote to herself — the value
    /// of a scratchpad is being able to write in one conversation and read in the next, or
    /// during the monologue, which belongs to no conversation at all.
    /// </summary>
    internal static readonly ScopeKey Scope = new("global");

    internal const string Prefix = "note:";

    internal const int MaxTitle  = 80;
    internal const int MaxText   = 4000;
    internal const int MaxNotes  = 64;

    /// <summary>
    /// The storage key for a title. Case- and whitespace-insensitive, so the note she wrote
    /// as "Tide pools" is the one she gets back asking for "tide  pools" — a scratchpad that
    /// answers "no such note" to a title off by a capital is worse than no scratchpad.
    /// </summary>
    internal static string? KeyFor(string? title)
    {
        string? clean = Clean(title);

        return clean is null ? null : Prefix + clean.ToLowerInvariant();
    }

    /// <summary>Titles come from the model: collapsed to one line, trimmed and capped.</summary>
    internal static string? Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        StringBuilder sb = new();
        bool space = false;

        foreach (char c in title)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space) sb.Append(' ');

            sb.Append(c);
            space = false;

            if (sb.Length == MaxTitle) break;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    internal static string Ago(DateTimeOffset when)
    {
        TimeSpan age = DateTimeOffset.UtcNow - when;

        return age switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours:   < 1 } => $"{(int)age.TotalMinutes}m ago",
            { TotalDays:    < 1 } => $"{(int)age.TotalHours}h ago",
            _                     => $"{(int)age.TotalDays}d ago"
        };
    }
}

/// <summary>
/// Lane's scratchpad. Memory handlers decide what she carries; this is the one store she
/// writes on purpose — a name to remember, a plan she wants to survive the sliding window,
/// something worked out once that would otherwise be worked out again next week.
/// </summary>
[LaneTool]
public sealed class WriteNoteTool(IKeyValueStore notes, ILogger<WriteNoteTool> log) : Tool<WriteNoteTool.Args>
{
    public sealed record Args(
        [property: Description("Short title for the note. Reusing one replaces that note, unless append is true.")] string Title,
        [property: Description("What to write.")] string Text,
        [property: Description("Add to the end of the existing note instead of replacing it. Default false.")] bool Append = false);

    protected override string Name => "write_note";

    protected override string Description =>
        "Write something down on your scratchpad so it survives past what you can remember. " +
        "Reusing a title replaces that note; pass append to add to it instead.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        string? title = Scratchpad.Clean(args.Title);
        string? key   = Scratchpad.KeyFor(args.Title);

        if (title is null || key is null) return ToolResult.Error("A note needs a title.");

        if (string.IsNullOrWhiteSpace(args.Text)) return ToolResult.Error("There is nothing to write.");

        Note? existing = await notes.GetAsync<Note>(Scratchpad.Scope, key, ct).ConfigureAwait(false);

        string text = args.Append && existing is not null
            ? existing.Text + "\n" + args.Text.Trim()
            : args.Text.Trim();

        // Refused rather than truncated: a note silently cut in half is one she will later
        // read back and act on as though it were whole.
        if (text.Length > Scratchpad.MaxText)
            return ToolResult.Error(
                $"That would make '{title}' {text.Length} characters, over the {Scratchpad.MaxText} limit. " +
                "Split it across notes, or rewrite it shorter.");

        if (existing is null)
        {
            int count = (await notes.ListKeysAsync(Scratchpad.Scope, Scratchpad.Prefix, ct).ConfigureAwait(false)).Count;

            if (count >= Scratchpad.MaxNotes)
                return ToolResult.Error(
                    $"You already have {count} notes, which is the limit. Delete one with delete_note first.");
        }

        // Titles are kept as she first wrote them: an append should not quietly restyle the
        // title on the strength of how it was capitalised the second time.
        string display = existing?.Title ?? title;

        await notes.SetAsync(Scratchpad.Scope, key, new Note(display, text, DateTimeOffset.UtcNow), ct)
                   .ConfigureAwait(false);

        log.LogDebug("Note '{Title}' {Action}, {Length} characters",
            display, existing is null ? "written" : args.Append ? "appended to" : "replaced", text.Length);

        // Tool traffic is stripped from memory, so without this the fact that she wrote
        // anything down would be gone by the next turn — leaving a note she never revisits.
        return ToolResult.Ok(existing is null ? $"Noted as '{display}'." : $"Updated '{display}'.")
                         .RememberAs($"[wrote a note, '{display}']\n{text}", MemoryScopeHint.Global);
    }
}

[LaneTool]
public sealed class ReadNoteTool(IKeyValueStore notes) : Tool<ReadNoteTool.Args>
{
    public sealed record Args(
        [property: Description("Title of the note, as given by list_notes.")] string Title);

    protected override string Name => "read_note";

    protected override string Description =>
        "Read back one of your notes. Call list_notes first if you are not sure what you wrote down.";

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        string? key = Scratchpad.KeyFor(args.Title);

        if (key is null) return ToolResult.Error("Which note? Try list_notes.");

        Note? note = await notes.GetAsync<Note>(Scratchpad.Scope, key, ct).ConfigureAwait(false);

        if (note is null)
            return ToolResult.Error($"You have no note called '{args.Title.Trim()}'. Try list_notes.");

        return ToolResult.Ok(note.Text)
                         .RememberAs($"[read a note, '{note.Title}']\n{note.Text}", MemoryScopeHint.Global);
    }
}

[LaneTool]
public sealed class ListNotesTool(IKeyValueStore notes) : Tool<NoArgs>
{
    protected override string Name => "list_notes";

    protected override string Description => "List what is on your scratchpad, most recently written first.";

    protected override async ValueTask<ToolResult> InvokeAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        IReadOnlyList<string> keys =
            await notes.ListKeysAsync(Scratchpad.Scope, Scratchpad.Prefix, ct).ConfigureAwait(false);

        List<Note> found = [];

        foreach (string key in keys)
        {
            Note? note = await notes.GetAsync<Note>(Scratchpad.Scope, key, ct).ConfigureAwait(false);

            // A row that no longer deserialises is skipped rather than fatal, for the same
            // reason handler state is: one unreadable note must not cost her the rest.
            if (note is not null) found.Add(note);
        }

        if (found.Count == 0) return ToolResult.Ok("(nothing on your scratchpad)");

        StringBuilder sb = new();

        foreach (Note note in found.OrderByDescending(n => n.UpdatedAt))
        {
            string first = note.Text.Split('\n')[0];

            sb.Append("- ").Append(note.Title)
              .Append(" (").Append(Scratchpad.Ago(note.UpdatedAt)).Append("): ")
              .AppendLine(first.Length > 80 ? first[..80] + "…" : first);
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }
}

/// <summary>
/// A scratchpad she cannot erase fills up and then refuses to take anything new, which is
/// the state a scratchpad exists to avoid.
/// </summary>
[LaneTool]
public sealed class DeleteNoteTool(IKeyValueStore notes) : Tool<DeleteNoteTool.Args>
{
    public sealed record Args(
        [property: Description("Title of the note to throw away.")] string Title);

    protected override string Name => "delete_note";

    protected override string Description => "Throw away one of your notes once it is no longer any use.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        string? key = Scratchpad.KeyFor(args.Title);

        if (key is null) return ToolResult.Error("Which note? Try list_notes.");

        Note? note = await notes.GetAsync<Note>(Scratchpad.Scope, key, ct).ConfigureAwait(false);

        if (note is null)
            return ToolResult.Error($"You have no note called '{args.Title.Trim()}'. Try list_notes.");

        await notes.RemoveAsync(Scratchpad.Scope, key, ct).ConfigureAwait(false);

        return ToolResult.Ok($"Threw away '{note.Title}'.")
                         .RememberAs($"[threw away the note '{note.Title}']", MemoryScopeHint.Global);
    }
}
