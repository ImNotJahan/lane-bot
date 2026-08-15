using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Discord.Presence;

/// <summary>
/// The things that can appear in Lane's Discord status, in the order they are rendered.
///
/// One member per source. Her face is the only one so far; a part added later — what she is
/// reading, who she is listening to, how busy she is — declares itself here, in the position
/// it should occupy, and nothing that already writes to the line has to know about it.
/// </summary>
public enum DiscordStatusSlot
{
    /// <summary>Her emoticon, from <c>PresenceChanged</c>.</summary>
    Face = 0
}

/// <summary>
/// The status as a set of independently owned parts, and the one line they render to.
///
/// Discord gives a bot a single string, so anything that wants to be seen there has to share
/// it. Each source owns its own slot and never sees the others: it sets or clears its part,
/// and the line is composed here. Deliberately pure and free of the gateway — what belongs
/// in the line is worth testing without a socket.
/// </summary>
public sealed class DiscordStatusLine
{
    /// <summary>Discord's own limit on a custom status.</summary>
    public const int MaxLength = 128;

    /// <summary>Between two parts. Wide enough to read as a gap in a proportional font.</summary>
    public const string Separator = "  ";

    private readonly SortedDictionary<DiscordStatusSlot, string> _parts = [];

    /// <summary>
    /// Sets or — with empty text — clears one part. Returns whether the rendered line
    /// actually changed, so a source that republishes the same thing costs no gateway write.
    /// </summary>
    public bool Set(DiscordStatusSlot slot, string? text)
    {
        string before  = Text;
        string cleaned = Clean(text);

        if (cleaned.Length == 0) _parts.Remove(slot);
        else                     _parts[slot] = cleaned;

        return !string.Equals(before, Text, StringComparison.Ordinal);
    }

    /// <summary>The composed line. Empty means Lane has nothing to show, not a blank status.</summary>
    public string Text
    {
        get
        {
            if (_parts.Count == 0) return "";

            string line = string.Join(Separator, _parts.Values);

            // Cut rather than refuse: a status is decoration, and losing the tail of it is
            // better than showing nothing because one part ran long.
            return line.Length <= MaxLength ? line : line[..(MaxLength - 1)].TrimEnd() + "…";
        }
    }

    /// <summary>
    /// A part is a face an assistant chose, or text from wherever the next slot reads. It is
    /// one line by the time it gets here: control characters come out and runs of whitespace
    /// collapse, because a newline in a status is a part that has overrun into its neighbour.
    /// </summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        System.Text.StringBuilder builder = new(text.Length);
        bool space = false;

        foreach (char c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space) builder.Append(' ');

            builder.Append(c);
            space = false;
        }

        return builder.ToString();
    }
}

/// <summary>
/// Keeps Discord's copy of the status in step with the line.
///
/// Presence is rate limited per gateway session, and the things that write to the line —
/// today her face, which changes with her mood on every turn — change far faster than that
/// budget allows. So writes are coalesced rather than queued: only the latest line survives
/// the wait, because an expression from four moods ago is not worth a slot in the budget.
/// </summary>
public sealed class DiscordStatusPublisher : IAsyncDisposable
{
    /// <summary>
    /// Discord allows a handful of presence updates per twenty seconds on one session. Kept
    /// well inside that, since a status arriving a few seconds late costs nothing and being
    /// rate limited on the gateway costs the connection.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly Func<string, CancellationToken, Task> _write;
    private readonly ILogger                               _log;
    private readonly TimeSpan                              _interval;

    private readonly Lock              _gate = new();
    private readonly DiscordStatusLine _line = new();

    // Capacity one, latest wins: a burst of changes is one write of the newest line.
    private readonly Channel<string> _pending = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private CancellationTokenSource? _lifetime;
    private Task?                    _pump;
    private bool                     _disposed;

    public DiscordStatusPublisher(
        Func<string, CancellationToken, Task> write, ILogger log, TimeSpan? interval = null)
    {
        _write    = write;
        _log      = log;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Sets or clears one part of the status. Cheap, non-blocking, safe from any thread.</summary>
    public void Set(DiscordStatusSlot slot, string? text)
    {
        string line;

        lock (_gate)
        {
            if (!_line.Set(slot, text)) return;

            line = _line.Text;
        }

        _pending.Writer.TryWrite(line);
    }

    /// <summary>
    /// Sends the current line again. Discord forgets a bot's presence when the session is
    /// re-identified, so a reconnect leaves her face behind unless it is repeated. Nothing to
    /// show is nothing to send — an empty line here would be a gateway write that changes
    /// nothing, on every reconnect.
    /// </summary>
    public void Refresh()
    {
        string line;

        lock (_gate) line = _line.Text;

        if (line.Length > 0) _pending.Writer.TryWrite(line);
    }

    public void Start(CancellationToken ct)
    {
        if (_pump is not null) return;

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pump     = Task.Run(() => PumpAsync(_lifetime.Token), CancellationToken.None);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (string line in _pending.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _write(line, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A status is how she looks, not how she works. Failing to set it must
                    // never cost the surface the messages it exists to carry.
                    _log.LogDebug(ex, "Could not update the Discord status");
                }

                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;

        _pending.Writer.TryComplete();

        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);

            if (_pump is not null)
            {
                try { await _pump.ConfigureAwait(false); }
                catch (Exception ex) { _log.LogDebug(ex, "The Discord status pump did not stop cleanly"); }
            }

            _lifetime.Dispose();
            _lifetime = null;
        }
    }
}
