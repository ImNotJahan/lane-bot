namespace Lane.Core.Agent;

public sealed class AgentOptions
{
    /// <summary>Template name in the prompt library. Takes precedence over <see cref="Persona"/>.</summary>
    public string PersonaPrompt { get; set; } = "Persona";

    /// <summary>Fallback persona for when no template file is present — tests, a bare checkout.</summary>
    public string Persona { get; set; } =
        "You are Lane. You are curious, dry, and brief. You speak like a person, not an assistant. " +
        "Do not narrate your actions or offer help you were not asked for.";

    public int MaxOutputTokens { get; set; } = 1024;

    public float Temperature { get; set; } = 1f;

    /// <summary>Timezone offset used when rendering timestamps into prompts.</summary>
    public TimeSpan DisplayOffset { get; set; } = TimeSpan.Zero;

    /// <summary>Ceilings for one agent run. See <see cref="AgentBudget"/>.</summary>
    public AgentBudget Budget { get; set; } = AgentBudget.Default;
}

/// <summary>
/// Ceilings for one agentic run. Without these, a model that keeps calling tools turns a
/// single message into an unbounded spend.
/// </summary>
public sealed record AgentBudget(
    int      MaxSteps             = 6,
    int      MaxToolCalls         = 12,
    int      MaxOutputTokensTotal = 8192)
{
    public static AgentBudget Default { get; } = new();
}
