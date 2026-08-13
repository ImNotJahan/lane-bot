using Lane.Core.Events;
using Lane.Core.Monologue;
using Lane.Core.Presence;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Lane.Host.Ui;

/// <summary>
/// Lane's inner life, as far as it can be seen from outside: what she last thought, how
/// she is currently showing herself, and how long until she thinks again.
///
/// The countdown renders from a deadline rather than a decremented integer — v2 ticked a
/// counter down once a second from the monologue's own loop, which meant the display and
/// the schedule could drift apart.
/// </summary>
public sealed class MonologueView : FrameView
{
    private readonly Label _next;
    private readonly Label _face;
    private readonly TextView _thought;

    private DateTimeOffset? _nextThoughtAt;

    public MonologueView(IEventBus bus, IMonologueScheduler scheduler)
    {
        Title = "INNER LIFE";

        _next = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "next thought: —" };
        _face = new Label { X = 0, Y = 1, Width = Dim.Fill(), Text = "face: ( ._.)" };

        _thought = new TextView
        {
            X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(),
            ReadOnly = true, WordWrap = true, CanFocus = false,
            Text = "(nothing yet)"
        };

        Add(_next, _face, _thought);

        MonologueStatus status = scheduler.Status;
        _nextThoughtAt = status.NextThoughtAt;
        if (status.LastThought is { Length: > 0 } last) _thought.Text = last;

        bus.Subscribe<MonologueTick>(tick =>
        {
            _nextThoughtAt = tick.NextThoughtAt;
            Refresh(tick.Reason);
        });

        bus.Subscribe<ThoughtHad>(had => App?.Invoke(() =>
        {
            _thought.Text = had.Spoke ? $"(said aloud) {had.Thought}" : had.Thought;
            _thought.SetNeedsDraw();
        }));

        bus.Subscribe<PresenceChanged>(change => App?.Invoke(() =>
        {
            _face.Text = $"face: {change.Emoticon}";
            _face.SetNeedsDraw();
        }));

        // One timer for the countdown, rather than a redraw per event.
        Timer timer = new(_ => Refresh(null), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Disposing += (_, _) => timer.Dispose();
    }

    private void Refresh(string? reason) => App?.Invoke(() =>
    {
        _next.Text = _nextThoughtAt is not { } at
            ? "next thought: —"
            : $"next thought: {Countdown(at - DateTimeOffset.UtcNow)}" +
              (reason is { Length: > 0 } ? $"  ({reason})" : "");

        _next.SetNeedsDraw();
    });

    private static string Countdown(TimeSpan remaining) => remaining switch
    {
        { Ticks: <= 0 }        => "any moment",
        { TotalMinutes: < 1 }  => $"{(int)remaining.TotalSeconds}s",
        { TotalHours: < 1 }    => $"{(int)remaining.TotalMinutes}m {remaining.Seconds:00}s",
        _                      => $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m"
    };
}
