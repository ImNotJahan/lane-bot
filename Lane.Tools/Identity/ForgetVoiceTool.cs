using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Identity;

/// <summary>
/// Makes Lane forget a voice.
///
/// A voiceprint is biometric data about a person, and the person it describes has to be able
/// to withdraw it — that is not a nicety, it is the condition on which keeping it at all is
/// reasonable. So the tool exists, it is easy to reach, and it takes effect immediately
/// rather than being an operator's job.
///
/// It acts on the voice speaking now, or on the voices belonging to whoever is asking. Like
/// the tools beside it, it takes nobody else's identifier: forgetting somebody else's voice
/// on their behalf would be a way to detach them from their own memory, which is the same
/// harm as misfiling them and about as easy to talk a model into.
/// </summary>
[LaneTool]
public sealed class ForgetVoiceTool(IVoiceprintDirectory voiceprints, ILogger<ForgetVoiceTool> log)
    : Tool<NoArgs>
{
    protected override string Name => "forget_voice";

    protected override string Description =>
        "Forget how someone sounds, so you no longer recognise them by voice. Acts on whoever is " +
        "asking — the voice speaking to you, or the voices you have learned for their account. " +
        "What they have said stays; only the recognising goes.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ToolAvailability Availability =>
        new() { AllowedTurns = TurnKind.Respond, RequiresSession = true };

    protected override async ValueTask<ToolResult> InvokeAsync(
        NoArgs args, ToolContext context, CancellationToken ct)
    {
        if (!voiceprints.Durable)
            return ToolResult.Error("There are no voices stored here, so there is nothing to forget.");

        if (context.Requester is not { IsLane: false } requester)
            return ToolResult.Error("I cannot tell who is asking, so I do not know what to forget.");

        // Speaking now, or asking from an account whose voices have been claimed. Both are
        // the requester's own; neither can be pointed at anybody else.
        List<string> mine = [];

        if (VoiceAccounts.VoiceprintOf(requester.Id) is { } speaking) mine.Add(speaking);

        mine.AddRange(voiceprints.BoundTo(requester.StableKey)
            .Select(p => p.Id)
            .Where(id => !mine.Contains(id)));

        if (mine.Count == 0)
            return ToolResult.Ok("I have not learned your voice, so there is nothing to forget.");

        foreach (string id in mine) await voiceprints.ForgetAsync(id, ct).ConfigureAwait(false);

        log.LogInformation("Forgot {Count} voice(s) for {Person}", mine.Count, requester.StableKey);

        return ToolResult.Ok(
            mine.Count == 1
                ? "Forgotten. I will not know you by your voice any more."
                : $"Forgotten, all {mine.Count} of them. I will not know you by your voice any more.")
            .RememberAs("[forgot a voice at their request]", MemoryScopeHint.Global);
    }
}
