using System.Text.RegularExpressions;
using Lane.Core.Energy;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Core.Pipeline.Stages;

/// <summary>
/// Whether she is awake for this at all.
///
/// Sleep is not a hard stop — she still costs money the moment she is spoken to by name — it is
/// a change in what reaches her. Everything else in the turn keeps working; this stage only
/// decides whether the rest of it happens, and hands the model turn her energy on the way past.
///
/// It sits before the response policy on purpose. That gate is a model call, and asking a small
/// model "was this meant for Lane" about a message she is not awake for is paying to find out
/// something that cannot matter. Sleeping through a message costs no model call at all.
/// </summary>
public sealed class SleepGateStage : ITurnStage
{
    /// <summary>Set on <see cref="TurnContext.Items"/> so the model turn can pitch the reply.</summary>
    public const string EnergyKey = "energy";

    /// <summary>Present only on the turn that woke her, or the one she answered half-asleep.</summary>
    public const string RousedKey = "energy.roused";

    private readonly IEnergyService _energy;
    private readonly EnergyOptions  _options;
    private readonly ILogger<SleepGateStage> _log;
    private readonly Regex?         _name;

    public SleepGateStage(
        IEnergyService energy,
        IOptions<EnergyOptions> options,
        ILogger<SleepGateStage> log)
    {
        _energy  = energy;
        _options = options.Value;
        _log     = log;
        _name    = BuildPattern(_options.WakeWords);
    }

    public async Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct)
    {
        Gate(ctx);

        await next().ConfigureAwait(false);
    }

    private void Gate(TurnContext ctx)
    {
        // A directive is something she already decided to say, and a monologue never reaches
        // the pipeline. Neither is a question about whether she is awake.
        if (ctx.Suppressed || ctx.Kind != TurnKind.Respond) return;

        // Reading is what accrues, so the arrival of a message is enough to keep her honest
        // even if nothing else has looked at the number for hours.
        EnergyState state = _energy.Current;

        ctx.Items[EnergyKey] = state;

        if (!state.Asleep) return;

        if (_options.SkipInDirectSessions && ctx.Descriptor.IsDirect) return;

        if (!Named(ctx))
        {
            // Suppressed rather than dropped, and the transcript still gets it — but memory
            // does not. A conversation she slept through is one she can read back later; it is
            // not one she should be summarising or forming impressions from as it happens.
            ctx.Suppressed        = true;
            ctx.SuppressionReason = "asleep";
            ctx.RecordToMemory    = false;

            _log.LogInformation("Asleep through {Count} message(s) in {Session}",
                ctx.Incoming.Count, ctx.Session.Id);

            return;
        }

        bool woke = _energy.Wake("named");

        // Re-read: waking cleared the latch, and the model turn should see her as she is now.
        ctx.Items[EnergyKey]  = _energy.Current;
        ctx.Items[RousedKey]  = woke;

        _log.LogInformation("Named in {Session} while asleep; {Outcome}",
            ctx.Session.Id, woke ? "awake again" : "answering once, then back to sleep");
    }

    /// <summary>
    /// Whether anyone in this batch actually said her name.
    ///
    /// Deliberately a plain text match rather than anything surface-specific: the Discord
    /// surface already resolves a mention of Lane to the literal word "Lane" in the message
    /// text, so one pattern covers being @-mentioned, being typed at, the terminal and the API.
    /// Word boundaries are the whole point — "Laney" and "multi-lane" are not her name, while
    /// "Lane," and "Lane's" are.
    /// </summary>
    private bool Named(TurnContext ctx)
    {
        if (_name is null) return false;

        return ctx.Incoming.Any(m => m.Role == LaneRole.User && _name.IsMatch(m.TextContent));
    }

    private static Regex? BuildPattern(IReadOnlyList<string> words)
    {
        string[] cleaned = [.. words.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => Regex.Escape(w.Trim()))];

        if (cleaned.Length == 0) return null;

        return new Regex($@"\b(?:{string.Join('|', cleaned)})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
