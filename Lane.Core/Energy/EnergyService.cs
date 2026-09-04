using Lane.Core.Events;
using Lane.Core.Memory;
using Lane.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Core.Energy;

/// <summary>
/// One number for all of Lane, spent by every model call and accrued back in whole steps.
///
/// Accrual is lazy: the persisted state is <c>(remaining, accruedThrough)</c>, and any read
/// replays however many whole intervals have elapsed since. So a restart, a suspended laptop
/// and a test with a fake clock all behave identically, and the background timer is a heartbeat
/// for whatever is drawing her — not the mechanism. A timer that ticked the number along would
/// make "how tired is she after the process was down for three hours" a question about how many
/// ticks were missed.
///
/// The phase advances by whole intervals rather than to <c>now</c>, so reading the value often
/// does not drag the next accrual forward.
/// </summary>
public sealed class EnergyService : IEnergyService, IHostedService, IDisposable
{
    /// <summary>Where the number lives between runs. Global, like book positions and notes.</summary>
    private static readonly ScopeKey Scope = new("global");

    private const string Key = "energy";

    private readonly IEventBus     _bus;
    private readonly EnergyOptions _options;
    private readonly ILogger<EnergyService> _log;
    private readonly IKeyValueStore? _store;
    private readonly TimeProvider  _time;

    /// <summary>How much one accrual interval gives back. Derived once, never configured directly.</summary>
    private readonly long _perStep;

    private readonly Lock _gate = new();

    private long           _remaining;
    private DateTimeOffset _accruedThrough;
    private bool           _asleep;
    private bool           _dirty;
    private bool           _clockWarned;

    private IDisposable?             _subscription;
    private CancellationTokenSource? _lifetime;
    private Task?                    _pump;

    public EnergyService(
        IEventBus bus,
        IOptions<EnergyOptions> options,
        ILogger<EnergyService> log,
        IKeyValueStore? store = null,
        TimeProvider? time = null)
    {
        _bus     = bus;
        _options = options.Value;
        _log     = log;
        _store   = store;
        _time    = time ?? TimeProvider.System;

        _perStep = Math.Max(1, _options.Budget / _options.StepsPerWindow);

        _remaining      = _options.StartFull ? _options.Budget : 0;
        _accruedThrough = _time.GetUtcNow();
    }

    public EnergyState Current => Read(out _);

    public TimeSpan? TimeUntilRested
    {
        get
        {
            TimeSpan? until;
            string? transition;

            lock (_gate)
            {
                transition = AccrueLocked();
                until      = _asleep ? UntilRestedLocked() : null;
            }

            Announce(transition);

            return until;
        }
    }

    public void Spend(TokenUsage usage, string? role)
    {
        // This runs on an event bus callback, on whichever thread published it. A metabolism
        // that can fail a turn would be worse than one that occasionally miscounts.
        try
        {
            long cost = usage.Input + usage.Output + usage.CacheWrite
                      + (long)(usage.CacheRead * _options.CacheReadWeight);

            if (cost <= 0) return;

            bool        fellAsleep;
            EnergyState state;

            lock (_gate)
            {
                AccrueLocked();

                // Floored at a whole window's worth of debt: one runaway turn must not be able
                // to put her under for a week.
                _remaining = Math.Max(-_options.Budget, _remaining - cost);
                _dirty     = true;

                fellAsleep = !_asleep && _remaining <= 0;

                if (fellAsleep) _asleep = true;

                state = SnapshotLocked();
            }

            if (!fellAsleep) return;

            _log.LogInformation("Out of energy after a {Role} call; going to sleep", role ?? "model");

            // The one transition that cannot be recomputed from the clock, so it is written
            // down immediately rather than waiting for the next tick.
            Flush();

            _bus.Publish(new EnergyChanged(state, "ran out"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not account for a {Role} call", role ?? "model");
        }
    }

    public bool Wake(string reason)
    {
        bool        woke;
        bool        ended;
        EnergyState state;

        lock (_gate)
        {
            AccrueLocked();

            // Already awake is trivially "yes, she is awake". Asleep with something left ends
            // the sleep; asleep with nothing left answers this one message and stays latched.
            ended = _asleep && _remaining > 0;
            woke  = !_asleep || ended;

            if (ended) _asleep = false;

            state = SnapshotLocked();
        }

        if (!ended) return woke;

        _log.LogInformation("Woken up ({Reason}) with {Percent:0}% energy left", reason, state.Fraction * 100);

        Flush();

        _bus.Publish(new EnergyChanged(state, reason));

        return woke;
    }

    // ---- accrual ----------------------------------------------------------

    /// <summary>
    /// Brings the number up to now. Returns the reason for a transition, or null. Must be
    /// called under <see cref="_gate"/>; publishing happens after the lock is released.
    /// </summary>
    private string? AccrueLocked()
    {
        DateTimeOffset now = _time.GetUtcNow();

        if (_options.AccrualInterval <= TimeSpan.Zero) return null;

        if (now < _accruedThrough)
        {
            // A clock that stepped backwards — a VM restored, an NTP correction. Re-anchoring
            // rather than accruing means she neither gains a windfall nor stalls forever
            // waiting for a boundary that has already passed.
            _accruedThrough = now;

            if (!_clockWarned)
            {
                _clockWarned = true;
                _log.LogWarning("The clock moved backwards; energy accrual re-anchored to now");
            }

            return null;
        }

        long steps = (long)((now - _accruedThrough) / _options.AccrualInterval);

        if (steps <= 0) return null;

        if (steps > _options.StepsPerWindow)
        {
            // Off for longer than a whole window. She wakes up full either way, and keeping
            // last week's phase would only make the first accrual land at an odd moment.
            steps           = _options.StepsPerWindow;
            _accruedThrough = now;
        }
        else
        {
            _accruedThrough += _options.AccrualInterval * steps;
        }

        _remaining = Math.Min(_options.Budget, _remaining + steps * _perStep);
        _dirty     = true;

        if (!_asleep || FractionLocked() < _options.WakeAt) return null;

        _asleep = false;

        _log.LogInformation("Rested enough to wake up ({Percent:0}%)", FractionLocked() * 100);

        return "rested";
    }

    private double FractionLocked() =>
        _options.Budget <= 0 ? 1 : Math.Clamp((double)_remaining / _options.Budget, 0, 1);

    private EnergyState SnapshotLocked() =>
        new(_remaining, _options.Budget, _asleep, _accruedThrough)
        {
            Tier = _options.TierFor(FractionLocked())
        };

    /// <summary>When accrual alone will next carry her over the wake threshold.</summary>
    private TimeSpan? UntilRestedLocked()
    {
        long target = (long)(_options.Budget * _options.WakeAt);

        if (_remaining >= target || _perStep <= 0) return TimeSpan.Zero;

        long steps = (target - _remaining + _perStep - 1) / _perStep;

        TimeSpan until = _accruedThrough + _options.AccrualInterval * steps - _time.GetUtcNow();

        return until > TimeSpan.Zero ? until : TimeSpan.Zero;
    }

    private EnergyState Read(out string? transition)
    {
        EnergyState state;

        lock (_gate)
        {
            transition = AccrueLocked();
            state      = SnapshotLocked();
        }

        Announce(transition, state);

        return state;
    }

    private void Announce(string? transition, EnergyState? state = null)
    {
        if (transition is null) return;

        _bus.Publish(new EnergyChanged(state ?? Snapshot(), transition));
    }

    private EnergyState Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    // ---- lifetime ---------------------------------------------------------

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);

        // One subscription catches respond, monologue, routing and summarize at once, because
        // every model in the host is wrapped in TelemetryLanguageModel. Nothing has to know
        // how many models exist, or that a new one was added.
        _subscription = _bus.Subscribe<TokenUsageEvent>(evt => Spend(evt.Usage, evt.Role));

        // Not linked to the startup token, which is cancelled once startup finishes.
        _lifetime = new CancellationTokenSource();
        _pump     = Task.Run(() => PumpAsync(_lifetime.Token), CancellationToken.None);

        EnergyState state = Current;

        _log.LogInformation(
            "Energy: {Remaining:N0} of {Budget:N0} tokens ({Percent:0}%, {Tier}){Asleep}; " +
            "{Step:N0} back every {Interval}",
            state.Remaining, state.Budget, state.Fraction * 100, state.Tier,
            state.Asleep ? ", asleep" : "", _perStep, _options.AccrualInterval);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;

        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);

            if (_pump is not null)
            {
                try { await _pump.ConfigureAwait(false); }
                catch (Exception ex) { _log.LogDebug(ex, "The energy pump did not stop cleanly"); }
            }
        }

        await SaveAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// A heartbeat, not the accrual. It flushes and publishes so anything drawing her moves
    /// while nothing else is happening — a missed tick costs nothing, because the next read
    /// accrues for the whole gap anyway.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        if (_options.AccrualInterval <= TimeSpan.Zero) return;

        using PeriodicTimer timer = new(_options.AccrualInterval, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                EnergyState state = Read(out string? transition);

                await SaveAsync(ct).ConfigureAwait(false);

                // Already announced by Read; a second event for the same moment would only
                // make the dashboard redraw twice.
                if (transition is null) _bus.Publish(new EnergyChanged(state, "accrued"));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The energy pump stopped");
        }
    }

    // ---- persistence ------------------------------------------------------

    /// <summary>What survives a restart. The clock supplies everything else.</summary>
    private sealed record Stored(long Remaining, DateTimeOffset AccruedThrough, bool Asleep);

    private async Task LoadAsync(CancellationToken ct)
    {
        if (_store is null) return;

        try
        {
            if (await _store.GetAsync<Stored>(Scope, Key, ct).ConfigureAwait(false) is not { } stored)
            {
                _log.LogInformation("No energy on record; starting {State}", _options.StartFull ? "rested" : "empty");
                return;
            }

            lock (_gate)
            {
                // Clamped to the *current* budget: lowering it between runs would otherwise
                // leave her permanently over-full and never tired.
                _remaining      = Math.Clamp(stored.Remaining, -_options.Budget, _options.Budget);
                _accruedThrough = stored.AccruedThrough;
                _asleep         = stored.Asleep;
            }
        }
        catch (Exception ex)
        {
            // Starting rested is the right failure: a broken row must not leave her asleep
            // with no way to find out why.
            _log.LogError(ex, "Could not read stored energy; starting fresh");
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        if (_store is null) return;

        Stored stored;

        lock (_gate)
        {
            if (!_dirty) return;

            _dirty = false;
            stored = new Stored(_remaining, _accruedThrough, _asleep);
        }

        try
        {
            await _store.SetAsync(Scope, Key, stored, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate) _dirty = true;

            _log.LogWarning(ex, "Could not write energy down");
        }
    }

    /// <summary>
    /// Writes without waiting, for the transitions that happen on a synchronous callback.
    ///
    /// Everything else rides the ten-minute tick: one SQLite write per model call would buy
    /// nothing, since the worst a crash can do is restart her slightly better rested than she
    /// should be.
    /// </summary>
    private void Flush() => _ = SaveAsync(CancellationToken.None);

    public void Dispose()
    {
        _subscription?.Dispose();
        _lifetime?.Dispose();
    }
}
