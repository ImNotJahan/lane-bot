using Lane.Surfaces.Discord.Presence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// What Lane shows about herself on Discord: one line, composed from parts nobody but their
/// own source owns, and written to the gateway at a rate the gateway will accept.
/// </summary>
public sealed class DiscordStatusTests
{
    /// <summary>
    /// Stands in for a part added later — what she is reading, who she is listening to. The
    /// point of the slot enum is that such a part gets a fixed place in the line without
    /// anything already on it being touched.
    /// </summary>
    private const DiscordStatusSlot Later = (DiscordStatusSlot)100;

    // ---- composition -------------------------------------------------------

    [Fact]
    public void Her_face_is_the_whole_status_while_it_is_the_only_part()
    {
        DiscordStatusLine line = new();

        Assert.True(line.Set(DiscordStatusSlot.Face, "( ._.)"));
        Assert.Equal("( ._.)", line.Text);
    }

    [Fact]
    public void Nothing_to_show_is_an_empty_line_rather_than_a_blank_status()
    {
        DiscordStatusLine line = new();

        Assert.Equal("", line.Text);
    }

    [Fact]
    public void Parts_render_in_slot_order_however_they_were_set()
    {
        // Whoever writes last does not get to reorder the line.
        DiscordStatusLine line = new();

        line.Set(Later, "reading Solaris");
        line.Set(DiscordStatusSlot.Face, "(o_o)");

        Assert.Equal($"(o_o){DiscordStatusLine.Separator}reading Solaris", line.Text);
    }

    [Fact]
    public void Clearing_one_part_leaves_the_others_standing()
    {
        DiscordStatusLine line = new();

        line.Set(DiscordStatusSlot.Face, "(^_^)");
        line.Set(Later, "reading Solaris");

        Assert.True(line.Set(DiscordStatusSlot.Face, null));
        Assert.Equal("reading Solaris", line.Text);
    }

    [Fact]
    public void Setting_a_part_to_what_it_already_says_is_not_a_change()
    {
        // The face is republished on every turn; only a different line is worth a gateway write.
        DiscordStatusLine line = new();

        Assert.True(line.Set(DiscordStatusSlot.Face, "(^_^)"));
        Assert.False(line.Set(DiscordStatusSlot.Face, "(^_^)"));
        Assert.False(line.Set(DiscordStatusSlot.Face, "  (^_^) "));
    }

    [Fact]
    public void A_part_cannot_break_the_line_it_shares()
    {
        // Parts are model-authored; a newline in one is a part overrunning into its neighbour.
        DiscordStatusLine line = new();

        line.Set(DiscordStatusSlot.Face, "(o_o)\n\nreading\tSolaris");

        Assert.Equal("(o_o) reading Solaris", line.Text);
    }

    [Fact]
    public void An_overlong_line_is_cut_rather_than_refused()
    {
        DiscordStatusLine line = new();

        line.Set(DiscordStatusSlot.Face, new string('x', 400));

        Assert.Equal(DiscordStatusLine.MaxLength, line.Text.Length);
        Assert.EndsWith("…", line.Text);
    }

    // ---- publishing --------------------------------------------------------

    [Fact]
    public async Task A_burst_of_changes_costs_one_update_rather_than_one_each()
    {
        // Presence is rate limited per gateway session and her face moves with her mood, so
        // an expression from three moods ago must not spend a slot in that budget.
        TaskCompletionSource release = new();
        SemaphoreSlim        wrote   = new(0);
        List<string>         written = [];

        await using DiscordStatusPublisher publisher = new(
            async (line, _) =>
            {
                lock (written) written.Add(line);

                wrote.Release();

                if (written.Count == 1) await release.Task;
            },
            NullLogger.Instance,
            TimeSpan.Zero);

        publisher.Start(CancellationToken.None);

        publisher.Set(DiscordStatusSlot.Face, "( ._.)");
        Assert.True(await wrote.WaitAsync(TimeSpan.FromSeconds(5)));    // the first write is in flight

        publisher.Set(DiscordStatusSlot.Face, "(o_o)");
        publisher.Set(DiscordStatusSlot.Face, "(^_^)");

        release.SetResult();

        Assert.True(await wrote.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await wrote.WaitAsync(TimeSpan.FromMilliseconds(200)));

        lock (written) Assert.Equal(["( ._.)", "(^_^)"], written);
    }

    [Fact]
    public async Task A_reconnect_shows_the_status_again_because_discord_forgets_it()
    {
        SemaphoreSlim wrote   = new(0);
        List<string>  written = [];

        await using DiscordStatusPublisher publisher = new(
            (line, _) =>
            {
                lock (written) written.Add(line);

                wrote.Release();

                return Task.CompletedTask;
            },
            NullLogger.Instance,
            TimeSpan.Zero);

        publisher.Start(CancellationToken.None);

        // Nothing to show is nothing to send, or every reconnect writes a line that says nothing.
        publisher.Refresh();
        Assert.False(await wrote.WaitAsync(TimeSpan.FromMilliseconds(200)));

        publisher.Set(DiscordStatusSlot.Face, "(^_^)");
        Assert.True(await wrote.WaitAsync(TimeSpan.FromSeconds(5)));

        publisher.Refresh();
        Assert.True(await wrote.WaitAsync(TimeSpan.FromSeconds(5)));

        lock (written) Assert.Equal(["(^_^)", "(^_^)"], written);
    }

    [Fact]
    public async Task A_failed_update_does_not_end_the_status()
    {
        // The gateway can refuse a presence update — while reconnecting, or when rate
        // limited. A status is decoration; it must not take the surface down with it.
        SemaphoreSlim wrote   = new(0);
        List<string>  written = [];

        await using DiscordStatusPublisher publisher = new(
            (line, _) =>
            {
                lock (written) written.Add(line);

                wrote.Release();

                return written.Count == 1 ? Task.FromException(new InvalidOperationException("not connected")) : Task.CompletedTask;
            },
            NullLogger.Instance,
            TimeSpan.Zero);

        publisher.Start(CancellationToken.None);

        publisher.Set(DiscordStatusSlot.Face, "( ._.)");
        Assert.True(await wrote.WaitAsync(TimeSpan.FromSeconds(5)));

        publisher.Set(DiscordStatusSlot.Face, "(^_^)");
        Assert.True(await wrote.WaitAsync(TimeSpan.FromSeconds(5)));

        lock (written) Assert.Equal(["( ._.)", "(^_^)"], written);
    }
}
