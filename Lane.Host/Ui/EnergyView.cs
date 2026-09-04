using Lane.Core.Energy;
using Lane.Core.Events;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Lane.Host.Ui;

/// <summary>
/// How much of the day's budget is left, and whether she is awake for it.
///
/// Seeded from the service and then driven by the bus, like every other pane — the number moves
/// on its own between events, so there is also a one-second timer, for the same reason the
/// monologue's countdown has one: a display that only redraws when something happens cannot
/// show time passing.
/// </summary>
public sealed class EnergyView : FrameView
{
    private const int BarWidth = 24;

    private readonly IEnergyService _energy;

    private readonly Label _bar;
    private readonly Label _tokens;
    private readonly Label _state;

    public EnergyView(IEventBus bus, IEnergyService energy)
    {
        Title = "ENERGY";

        _energy = energy;

        _bar    = new Label { X = 0, Y = 0, Width = Dim.Fill() };
        _tokens = new Label { X = 0, Y = 1, Width = Dim.Fill() };
        _state  = new Label { X = 0, Y = 2, Width = Dim.Fill() };

        Add(_bar, _tokens, _state);

        bus.Subscribe<EnergyChanged>(_ => Refresh());

        Refresh();

        Timer timer = new(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Disposing += (_, _) => timer.Dispose();
    }

    private void Refresh()
    {
        EnergyState state = _energy.Current;

        // Read outside the UI thread, drawn on it: Current accrues, and doing that inside
        // Invoke would put a lock on the redraw path.
        TimeSpan? rested = state.Asleep ? _energy.TimeUntilRested : null;

        App?.Invoke(() =>
        {
            int filled = (int)Math.Round(state.Fraction * BarWidth);

            _bar.Text = new string('█', filled) + new string('░', BarWidth - filled) +
                        $"  {state.Fraction * 100:0}%";

            _tokens.Text = $"{Compact(state.Remaining)} / {Compact(state.Budget)} tokens";

            _state.Text = state.Asleep
                ? $"asleep — rested in {Countdown(rested)}"
                : state.Tier switch
                {
                    EnergyTier.Tired => "tired",
                    EnergyTier.Weary => "worn out",
                    _                => "rested"
                };

            _bar.SetNeedsDraw();
            _tokens.SetNeedsDraw();
            _state.SetNeedsDraw();
        });
    }

    /// <summary>Twenty million is not a number anybody reads off a dashboard.</summary>
    private static string Compact(long tokens) => Math.Abs(tokens) switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.0}M",
        >= 1_000     => $"{tokens / 1_000.0:0.0}k",
        _            => tokens.ToString("N0")
    };

    private static string Countdown(TimeSpan? remaining) => remaining switch
    {
        null                        => "—",
        { Ticks: <= 0 }             => "any moment",
        { TotalMinutes: < 1 } value => $"{(int)value.TotalSeconds}s",
        { TotalHours: < 1 } value   => $"{(int)value.TotalMinutes}m",
        { } value                   => $"{(int)value.TotalHours}h {value.Minutes:00}m"
    };
}
