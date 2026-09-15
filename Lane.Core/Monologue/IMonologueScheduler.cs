using Lane.Core.Agent;
using Lane.Core.Events;

namespace Lane.Core.Monologue;

public sealed class MonologueOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How long Lane waits between thoughts when she does not say otherwise.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Where the next thought is pushed to when someone speaks to her.
    ///
    /// Not zero: thinking the instant a conversation starts means interrupting it. v2 used
    /// the same idea, reset by a flag polled once a second; here it is an event.
    /// </summary>
    public TimeSpan AfterMessage { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Floor and ceiling on what she may schedule for herself.</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaxInterval { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Quiet on startup, so surfaces have time to connect before she has opinions.</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(45);

    public string Prompt { get; set; } = "Monologue";

    public int MaxOutputTokens { get; set; } = 512;

    /// <summary>
    /// Shows each thought the most recent utterances from every conversation, labelled by
    /// session, drawn from the transcript rather than any memory handler.
    /// </summary>
    public bool SeeAllMessages { get; set; }

    /// <summary>How many utterances <see cref="SeeAllMessages"/> shows.</summary>
    public int AllMessagesLimit { get; set; } = 40;

    /// <summary>
    /// Roomier than a reply's budget on purpose: a thought that looks around, reads
    /// something and then schedules itself has spent three steps before it has said
    /// anything, and running out mid-way leaves her with no thought at all.
    /// </summary>
    public AgentBudget Budget { get; set; } = new(MaxSteps: 6, MaxToolCalls: 8);
}

public sealed record MonologueStatus(
    DateTimeOffset? NextThoughtAt,
    string?         LastThought,
    DateTimeOffset? LastThoughtAt,
    int             ThoughtCount,
    bool            Thinking);

/// <summary>Published every time the schedule moves, so the dashboard can count down.</summary>
public sealed record MonologueTick(DateTimeOffset? NextThoughtAt, string Reason) : ILaneEvent;

/// <summary>Published when a thought completes, whether or not she chose to say anything.</summary>
public sealed record ThoughtHad(string Thought, bool Spoke) : ILaneEvent;

/// <summary>
/// Lets anything nudge Lane's inner life without owning it.
///
/// The <c>schedule_next_thought</c> tool is the interesting caller: it is how Lane decides
/// for herself when to think again, which in v2 was a JSON field the orchestrator read.
/// </summary>
public interface IMonologueScheduler
{
    MonologueStatus Status { get; }

    /// <summary>Think again after this long. Clamped to the configured bounds.</summary>
    void Schedule(TimeSpan delay, string reason);

    /// <summary>Something happened worth reconsidering the schedule over.</summary>
    void Interrupt(string reason);
}

/// <summary>Used when the monologue is turned off, so tools that reference it still resolve.</summary>
public sealed class InertMonologueScheduler : IMonologueScheduler
{
    public MonologueStatus Status { get; } = new(null, null, null, 0, false);

    public void Schedule(TimeSpan delay, string reason) { }

    public void Interrupt(string reason) { }
}
