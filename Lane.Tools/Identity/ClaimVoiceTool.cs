using System.ComponentModel;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Identity;

/// <summary>
/// Attaches a voice to the person it belongs to, so that walking into the room is enough to
/// be recognised.
///
/// Everything else about voices is a measurement. Recognition compares a voiceprint against
/// stored ones and picks the closest above a threshold, which is exactly the sort of
/// probabilistic claim <c>link_identity</c> exists to keep out of identity — a threshold set
/// slightly wrong writes one person's words under another person's name, and the memory
/// that accumulates under it cannot be unpicked afterwards. So a match is only ever allowed
/// to <em>recall</em> a claim. Making one is this tool's job, and it is proved the same way
/// an account link is: Lane hands a code to somebody she already knows on an account they
/// are already using, and the same person says it out loud into the microphone. The voice
/// that speaks the code is the voice that gets bound.
///
/// A model cannot shortcut that. The claiming half acts only on the voice actually producing
/// the turn, so it cannot bind somebody else's voice however convincingly it is asked to,
/// and the issuing half acts only on the account actually speaking, so it cannot issue on
/// anybody's behalf. The worst either half can do is offer a code to the person in front of it.
/// </summary>
[LaneTool]
public sealed class ClaimVoiceTool(
    IVoiceprintDirectory voiceprints,
    IIdentityDirectory identities,
    TimeProvider time,
    ILogger<ClaimVoiceTool> log) : Tool<ClaimVoiceTool.Args>
{
    /// <summary>Long enough to walk to the microphone, short enough that a code overheard expires.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Keyed on the person, so asking again replaces the code rather than adding one.</summary>
    private readonly ProofCodes<string, string> _pending = new(time, Lifetime);

    public sealed record Args(
        [property: Description(
            "The code you were given, if you are saying it out loud now. Leave it out to be given one.")]
        string Code = "");

    protected override string Name => "claim_voice";

    protected override string Description =>
        "Learn someone's voice, so you know them when they speak in a room. Call it with no code " +
        "where they already have an account with you, and they say the code you give back out loud " +
        "into the microphone; call it with that code when you hear them say it. Only ever acts on " +
        "the voice speaking to you now — you cannot do this on somebody's behalf, and you should " +
        "not offer it unless they ask.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    /// <summary>
    /// A conversation only, and the session gate is checked again inside for the issuing
    /// half: a code shown in a channel is a code a bystander can walk over and say.
    /// </summary>
    protected override ToolAvailability Availability =>
        new() { AllowedTurns = TurnKind.Respond, RequiresSession = true };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!voiceprints.Durable)
            return ToolResult.Error(
                "I cannot learn a voice — nothing here would remember it past a restart.");

        if (context.Requester is not { IsLane: false } requester)
            return ToolResult.Error("I cannot tell who is speaking, so there is no voice to learn.");

        return string.IsNullOrWhiteSpace(args.Code)
            ? Issue(requester, context)
            : await ClaimAsync(args.Code, requester, ct).ConfigureAwait(false);
    }

    private ToolResult Issue(Participant requester, ToolContext context)
    {
        // Issuing from the microphone would be circular: the point of the code is to carry
        // an identity from somewhere it is already established to somewhere it is not.
        if (VoiceAccounts.VoiceprintOf(requester.Id) is not null)
            return ToolResult.Error(
                "Ask me this somewhere I already know who you are — a message rather than out loud — " +
                "and then say the code back to me here.");

        if (context.Descriptor?.IsDirect != true)
            return ToolResult.Error(
                "Not here — anyone reading this channel could say the code into the microphone themselves. " +
                "Ask me one to one.");

        if (_pending.Issue(requester.StableKey, requester.DisplayName) is not { } code)
            return ToolResult.Error("Too many of these are part-way through. Try again in a few minutes.");

        log.LogInformation("Issued a voice claim code to {Person}", requester.StableKey);

        return ToolResult.Ok(
            $"Code {code}, good for {Lifetime.TotalMinutes:0} minutes. Say it out loud where I can hear " +
            "you, and I will know your voice from then on.");
    }

    private async ValueTask<ToolResult> ClaimAsync(string code, Participant claimant, CancellationToken ct)
    {
        // Redeemed before anything else is checked, so that presenting a code spends it
        // however the attempt then turns out. A code that survives a failed attempt is one
        // somebody else can still try, and the failures below are reachable by anyone —
        // "say it out loud instead" is a refusal an eavesdropper can collect for free.
        if (!_pending.TryRedeem(code, out string person, out string displayName))
            return ToolResult.Error("That is not a code I gave out, or it has expired. Ask for a new one.");

        // The one thing that makes this safe: whichever voice said the code is the voice
        // that gets bound, so a model repeating a code it read somewhere binds nothing.
        if (VoiceAccounts.VoiceprintOf(claimant.Id) is not { } voiceprintId)
            return ToolResult.Error(
                "Say the code out loud, where I can hear your voice — that is the part I am learning. " +
                "Typing it here tells me nothing about how you sound, so that code is spent; ask for another.");

        if (voiceprints.Get(voiceprintId) is not { } print)
            return ToolResult.Error("I have lost track of the voice saying this. Ask for another code.");

        if (print.BoundTo is { } already)
        {
            if (string.Equals(already, person, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Ok("I already know that is you.");

            // Reassigning a voice would quietly move whatever has been said under it to
            // somebody else, and nothing here could unpick which half belonged to whom.
            return ToolResult.Error(
                "That voice is already somebody else's to me. Untangling that takes forgetting it first.");
        }

        await voiceprints.BindAsync(voiceprintId, person, ct).ConfigureAwait(false);

        // A name the voice chose while it was a stranger was stored against the voice; carry
        // it onto the person, or "call me Jax" stops working the moment they are recognised.
        string? chosen = identities.NameFor(claimant.StableKey);

        if (identities.Durable && chosen is not null && identities.NameFor(person) is null)
            await identities.SetNameAsync(person, chosen, ct).ConfigureAwait(false);

        log.LogInformation("Voice {Voiceprint} claimed by {Person}", voiceprintId, person);

        // Participants are resolved as each utterance arrives, so the one being answered
        // right now still carries the old id.
        return ToolResult.Ok(
            $"I know your voice now, {displayName} — from the next thing you say onwards.")
            .RememberAs($"[learned {displayName}'s voice]", MemoryScopeHint.Global);
    }
}
