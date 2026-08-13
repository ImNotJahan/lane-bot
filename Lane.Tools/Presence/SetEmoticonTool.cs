using System.ComponentModel;
using Lane.Core.Presence;
using Lane.Core.Tools;

namespace Lane.Tools.Presence;

/// <summary>
/// Replaces v2's <c>data["emoticon"]</c> field, which only the monologue could set and
/// which reached exactly one listener.
/// </summary>
[LaneTool]
public sealed class SetEmoticonTool(IPresenceSink presence) : Tool<SetEmoticonTool.Args>
{
    private const int MaxLength = 24;

    public sealed record Args(
        [property: Description("A short text emoticon, e.g. ( ._.) or (^_^)")] string Emoticon);

    protected override string Name => "set_emoticon";

    protected override string Description =>
        "Change the little face you show. Use it when your mood shifts — it is how you look, not something you say.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        string emoticon = args.Emoticon?.Trim() ?? "";

        if (emoticon.Length == 0) return ValueTask.FromResult(ToolResult.Error("An emoticon is required."));

        if (emoticon.Length > MaxLength)
            return ValueTask.FromResult(ToolResult.Error(
                $"That is {emoticon.Length} characters; keep it under {MaxLength}."));

        presence.Publish(new PresenceChanged(emoticon, context.Session));

        return ValueTask.FromResult(ToolResult.Ok($"Face set to {emoticon}."));
    }
}
