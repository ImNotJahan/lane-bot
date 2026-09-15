using System.ComponentModel;
using Lane.Core.Pipeline.Stages;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Tools.Sessions;

/// <summary>
/// Lets Lane set how interested a message in this conversation has to be before she replies,
/// overriding <see cref="ResponsePolicyOptions.DefaultThreshold"/>. Keyed by memory group, like
/// <see cref="SetSessionDescriptionTool"/>.
/// </summary>
[LaneTool]
public sealed class SetResponseThresholdTool(
    ISessionThresholds thresholds,
    IOptions<ResponsePolicyOptions> options,
    ILogger<SetResponseThresholdTool> log)
    : Tool<SetResponseThresholdTool.Args>
{
    public sealed record Args(
        [property: Description(
            "Between 0 and 1. Leave it out to go back to the default.")]
        float? Threshold = null);

    protected override string Name => "set_response_threshold";

    protected override string Description =>
        "Set how much a message in this conversation has to call for a reply before you answer it. " +
        "0 means you answer almost everything; 1 means you answer almost nothing. It applies only here, " +
        "until you change it. Leave the threshold out to go back to the default.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ToolAvailability Availability =>
        new() { AllowedTurns = TurnKind.Respond, RequiresSession = true };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!thresholds.Durable)
            return ToolResult.Error("I cannot change that — nothing here would remember it past a restart.");

        if (context.Descriptor is not { } session)
            return ToolResult.Error("I cannot tell which conversation this is.");

        float fallback = options.Value.DefaultThreshold;
        float? current = thresholds.For(session);

        if (args.Threshold is not { } requested)
        {
            if (current is null)
                return ToolResult.Ok($"{session.DisplayName} already uses the default of {fallback:0.00}.");

            await thresholds.SetAsync(session.MemoryGroup, null, ct).ConfigureAwait(false);

            return ToolResult.Ok($"{session.DisplayName} is back to the default of {fallback:0.00}.")
                             .RememberAs($"[reset the response threshold of {session.DisplayName} to the default]");
        }

        if (float.IsNaN(requested) || requested is < 0f or > 1f)
            return ToolResult.Error($"The threshold has to be between 0 and 1, not {requested}.");

        if (current == requested)
            return ToolResult.Ok($"The threshold in {session.DisplayName} is already {requested:0.00}.");

        await thresholds.SetAsync(session.MemoryGroup, requested, ct).ConfigureAwait(false);

        log.LogInformation("Set the response threshold of {Session} to {Threshold:0.00}", session.DisplayName, requested);

        string note = options.Value.SkipInDirectSessions && session.IsDirect
            ? " This is a one-to-one conversation, though, so you answer everything here regardless."
            : "";

        return ToolResult
            .Ok($"The threshold in {session.DisplayName} is now {requested:0.00} (was {current ?? fallback:0.00}).{note}")
            .RememberAs($"[set the response threshold of {session.DisplayName} to {requested:0.00}]");
    }
}
