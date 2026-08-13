using Lane.Core.Messages;

namespace Lane.Core.Models;

public enum StopReason
{
    EndTurn,

    /// <summary>The model wants tools run. The loop must answer every requested call.</summary>
    ToolUse,

    MaxTokens,
    StopSequence,
    Refusal,
    Other
}

public readonly record struct TokenUsage(
    int      Input,
    int      Output,
    int      CacheRead,
    int      CacheWrite,
    string   ModelInstanceId,
    TimeSpan Latency)
{
    public int Total => Input + Output;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(
        a.Input + b.Input,
        a.Output + b.Output,
        a.CacheRead + b.CacheRead,
        a.CacheWrite + b.CacheWrite,
        string.IsNullOrEmpty(a.ModelInstanceId) ? b.ModelInstanceId : a.ModelInstanceId,
        a.Latency + b.Latency);
}

public sealed record ModelResponse(
    IReadOnlyList<ContentPart> Content,
    StopReason                 Stop,
    TokenUsage                 Usage)
{
    public string Text => string.Concat(Content.OfType<TextPart>().Select(p => p.Text));

    public IReadOnlyList<ToolUsePart> ToolCalls => [.. Content.OfType<ToolUsePart>()];
}

public abstract record ModelStreamEvent
{
    public sealed record TextDelta(string Text) : ModelStreamEvent;
    public sealed record ThinkingDelta(string Text) : ModelStreamEvent;
    public sealed record ToolUseStarted(string CallId, string Name) : ModelStreamEvent;
    public sealed record ToolArgsDelta(string CallId, string JsonFragment) : ModelStreamEvent;

    /// <summary>Always the final event. Carries the assembled response.</summary>
    public sealed record Completed(ModelResponse Response) : ModelStreamEvent;
}
