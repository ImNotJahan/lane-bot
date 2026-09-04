using Lane.Core.Agent;
using Lane.Core.Events;
using Lane.Core.Models;

namespace Lane.Core.Energy;

/// <summary>
/// How worn out Lane is, as three bands rather than a continuous number.
///
/// Bands rather than a curve because every consequence of tiredness is discrete anyway: one
/// line of persona text, one set of loop ceilings, one list of tools she will not reach for.
/// A continuous scale would give each turn slightly different numbers and nothing that could
/// be stated about her, which is the part worth being able to explain.
/// </summary>
public enum EnergyTier { Rested, Tired, Weary }

/// <summary>
/// What is left of the day's budget, at one moment.
///
/// <paramref name="Remaining"/> may be negative: the budget is a rate, not admission control,
/// and several turns can each start with tokens in hand and all finish. <paramref name="Asleep"/>
/// is deliberately not derived from it — she can be asleep at thirty percent, having been woken
/// and gone back under, and awake at sixty on the way down.
/// </summary>
public readonly record struct EnergyState(long Remaining, long Budget, bool Asleep, DateTimeOffset AsOf)
{
    /// <summary>Clamped at zero below, so a debt does not render as a negative percentage.</summary>
    public double Fraction => Budget <= 0 ? 1 : Math.Clamp((double)Remaining / Budget, 0, 1);

    public EnergyTier Tier { get; init; } = EnergyTier.Rested;

    public static EnergyState Full { get; } = new(1, 1, false, DateTimeOffset.MinValue);
}

/// <summary>Published whenever the number moves enough to be worth redrawing, and on every transition.</summary>
public sealed record EnergyChanged(EnergyState State, string Reason) : ILaneEvent;

/// <summary>
/// Lane's metabolism: one number for all of her, spent by every model call and accrued back
/// over a rolling window.
///
/// Global on purpose, like the monologue loop. She is one continuous person who happens to be
/// reachable from several places, and a budget per conversation would mean a busy channel could
/// not tire her out of a quiet one.
/// </summary>
public interface IEnergyService
{
    /// <summary>Accrues up to now, then snapshots. Cheap, and safe from any thread.</summary>
    EnergyState Current { get; }

    /// <summary>
    /// Debits a completed model call. Never throws: this runs on an event bus callback, on
    /// whichever thread published it, and a metabolism that can fail a turn is worse than one
    /// that occasionally miscounts.
    /// </summary>
    void Spend(TokenUsage usage, string? role);

    /// <summary>
    /// Someone said her name while she was asleep. True when that ended the sleep; false means
    /// she answers this one message and goes straight back under.
    /// </summary>
    bool Wake(string reason);

    /// <summary>How long until accrual alone would wake her. Null when she is already awake.</summary>
    TimeSpan? TimeUntilRested { get; }
}

/// <summary>
/// Lane with no metabolism: always rested, never asleep.
///
/// The default until <c>AddLaneEnergy</c> replaces it, which is what lets every existing test
/// and a bare checkout behave exactly as they did before tiredness existed.
/// </summary>
public sealed class BoundlessEnergy : IEnergyService
{
    public EnergyState Current => EnergyState.Full;

    public void Spend(TokenUsage usage, string? role) { }

    public bool Wake(string reason) => true;

    public TimeSpan? TimeUntilRested => null;
}

/// <summary>
/// What one tier costs her, as ceilings rather than as advice.
///
/// A settable class rather than a record so the configuration binder can fill it in place; the
/// constructor exists only to make the defaults above readable. <see cref="DenyTools"/> is
/// enforced when a call arrives rather than by hiding the tool, because the advertised list is
/// hashed into the prompt cache lineage — see the note in <c>ToolRegistry.Gate</c>.
/// </summary>
public sealed class EnergyTierOptions
{
    public EnergyTierOptions() { }

    public EnergyTierOptions(
        double outputScale, int maxSteps, int maxToolCalls, double monologueScale, List<string> denyTools)
    {
        OutputScale    = outputScale;
        MaxSteps       = maxSteps;
        MaxToolCalls   = maxToolCalls;
        MonologueScale = monologueScale;
        DenyTools      = denyTools;
    }

    /// <summary>What fraction of her usual reply length she is good for.</summary>
    public double OutputScale { get; set; } = 1;

    /// <summary>Ceilings on one agent run, clamped against the configured ones rather than raising them.</summary>
    public int MaxSteps     { get; set; } = int.MaxValue;
    public int MaxToolCalls { get; set; } = int.MaxValue;

    /// <summary>How much further apart her thoughts are spread. 1 is the usual cadence.</summary>
    public double MonologueScale { get; set; } = 1;

    /// <summary>Glob patterns, matched the same way <c>Lane:Tools:Deny</c> is.</summary>
    public List<string> DenyTools { get; set; } = [];

    /// <summary>Scales a ceiling without ever taking it to nothing.</summary>
    public int Scale(int tokens, int floor) => Math.Max(floor, (int)(tokens * OutputScale));

    public AgentBudget Scale(AgentBudget budget) => budget with
    {
        MaxSteps     = Math.Clamp(MaxSteps,     1, budget.MaxSteps),
        MaxToolCalls = Math.Clamp(MaxToolCalls, 0, budget.MaxToolCalls)
    };
}

public sealed class EnergyOptions
{
    /// <summary>
    /// Off by default. A harness that can decide on its own to ignore you is a surprising
    /// thing for a bare checkout to do — the same argument the monologue makes.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Tokens she may spend across <see cref="Window"/>.</summary>
    public long Budget { get; set; } = 20_000_000;

    public TimeSpan Window { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How often the budget accrues back. One step is <c>Budget / (Window / AccrualInterval)</c>,
    /// derived rather than configured, so changing either of the two above stays coherent.
    /// </summary>
    public TimeSpan AccrualInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Below this she is <see cref="EnergyTier.Tired"/>; below <see cref="WearyBelow"/>, weary.</summary>
    public double TiredBelow { get; set; } = 0.70;

    public double WearyBelow { get; set; } = 0.30;

    /// <summary>Accrual alone ends sleep here. Being named ends it earlier, if there is anything left.</summary>
    public double WakeAt { get; set; } = 0.70;

    /// <summary>
    /// What a cached input token counts for.
    ///
    /// Anthropic reports cache reads outside <c>Input</c>, so a long well-cached conversation
    /// would otherwise debit almost nothing while still costing real money. A tenth tracks
    /// billing rather than tokens on the wire.
    /// </summary>
    public double CacheReadWeight { get; set; } = 0.1;

    /// <summary>A first run starts rested. Booting asleep reads as broken rather than as tired.</summary>
    public bool StartFull { get; set; } = true;

    /// <summary>
    /// Skip the sleep gate in one-to-one conversations.
    ///
    /// False by default, which is the literal reading: asleep means only her name gets through,
    /// wherever it is said. <c>ResponsePolicyOptions.SkipInDirectSessions</c> makes the opposite
    /// argument for the routing gate — being ignored by something you are talking to directly
    /// reads as broken — and this is the knob if that argument wins here too.
    /// </summary>
    public bool SkipInDirectSessions { get; set; }

    /// <summary>What counts as being addressed while she is asleep. Matched on word boundaries.</summary>
    public List<string> WakeWords { get; set; } = ["Lane"];

    public EnergyTierOptions Tired { get; set; } = new(0.5, 3, 5, 2.0, ["web_search"]);

    public EnergyTierOptions Weary { get; set; } = new(0.25, 2, 2, 4.0, ["web_*", "fetch_url", "read_book"]);

    /// <summary>Null for <see cref="EnergyTier.Rested"/>, which means nothing changes.</summary>
    public EnergyTierOptions? For(EnergyTier tier) => tier switch
    {
        EnergyTier.Tired => Tired,
        EnergyTier.Weary => Weary,
        _                => null
    };

    public EnergyTier TierFor(double fraction) =>
        fraction >= TiredBelow ? EnergyTier.Rested
      : fraction >= WearyBelow ? EnergyTier.Tired
      :                          EnergyTier.Weary;

    /// <summary>How many accrual steps fit in one window. At least one, so a misconfiguration cannot divide by zero.</summary>
    public long StepsPerWindow => AccrualInterval <= TimeSpan.Zero
        ? 1
        : Math.Max(1, (long)(Window / AccrualInterval));
}
