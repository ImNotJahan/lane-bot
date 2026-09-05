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
/// an account link is: Lane hands a phrase to somebody she already knows on an account they
/// are already using, and the same person says it out loud into the microphone. The voice
/// that speaks the phrase is the voice that gets bound.
///
/// Three words rather than the letters and digits <c>link_identity</c> hands out, because
/// this one has to come back through a speech recogniser and those do not survive the trip —
/// see <see cref="ProofCodeShape"/>.
///
/// A model cannot shortcut that. The claiming half acts only on the voice actually producing
/// the turn, so it cannot bind somebody else's voice however convincingly it is asked to,
/// and the issuing half acts only on the account actually speaking, so it cannot issue on
/// anybody's behalf. The worst either half can do is offer a phrase to the person in front of it.
/// </summary>
[LaneTool]
public sealed class ClaimVoiceTool(
    IVoiceprintDirectory voiceprints,
    IIdentityDirectory identities,
    IdentityToolOptions options,
    TimeProvider time,
    ILogger<ClaimVoiceTool> log) : Tool<ClaimVoiceTool.Args>
{
    /// <summary>Long enough to walk to the microphone, short enough that a phrase overheard expires.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Keyed on the person, so asking again replaces the phrase rather than adding one.
    ///
    /// Spoken rather than typed, and that is the whole design of this half: the proof only
    /// counts if it arrives through the microphone, so it has to be something a speech
    /// recogniser will actually hand back. A string of letters and digits is not.
    /// </summary>
    private readonly ProofCodes<string, string> _pending = new(time, Lifetime, ProofCodeShape.Spoken);

    public sealed record Args(
        [property: Description(
            "The words you heard them say, if they are saying them out loud now — as close to what " +
            "you heard as you can, in order. Leave it out to be given a phrase to hand them.")]
        string Phrase = "");

    protected override string Name => "claim_voice";

    protected override string Description => options.RequireProof
        ? "Learn someone's voice, so you know them when they speak in a room. Call it with nothing " +
          "where they already have an account with you, and you get back three words for them to say " +
          "out loud into the microphone; call it with those words when you hear somebody say them. " +
          "Only ever acts on the voice speaking to you now — you cannot do this on somebody's behalf, " +
          "and you should not offer it unless they ask."

        : "Learn someone's voice, so you know them when they speak in a room. Call it with nothing " +
          "where they already have an account with you, then call it again — still with nothing — " +
          "when you hear them speak into the microphone. Only ever acts on the voice speaking to you " +
          "now, so you cannot do this on somebody's behalf, and you should not offer it unless they ask.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    /// <summary>
    /// A conversation only, and the session gate is checked again inside for the issuing
    /// half: a phrase shown in a channel is a phrase a bystander can walk over and say.
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

        // Which half this is. With a phrase it is unambiguous; without one it comes down to
        // where the call is coming from, and a voice is the half that cannot be the issuer.
        bool claiming = !string.IsNullOrWhiteSpace(args.Phrase)
                     || (!options.RequireProof && VoiceAccounts.VoiceprintOf(requester.Id) is not null);

        return claiming
            ? await ClaimAsync(args.Phrase, requester, ct).ConfigureAwait(false)
            : Issue(requester, context);
    }

    private ToolResult Issue(Participant requester, ToolContext context)
    {
        // Issuing from the microphone would be circular: the point of the phrase is to carry
        // an identity from somewhere it is already established to somewhere it is not.
        if (VoiceAccounts.VoiceprintOf(requester.Id) is not null)
            return ToolResult.Error(
                "Ask me this somewhere I already know who you are — a message rather than out loud — " +
                "and then say the words back to me here.");

        if (context.Descriptor?.IsDirect != true)
            return ToolResult.Error(
                "Not here — anyone reading this channel could say the words into the microphone themselves. " +
                "Ask me one to one.");

        if (_pending.Issue(requester.StableKey, requester.DisplayName) is not { } phrase)
            return ToolResult.Error("Too many of these are part-way through. Try again in a few minutes.");

        log.LogInformation("Expecting {Person} at the microphone", requester.StableKey);

        // Staged either way — being on both channels is what is left of the proof once the
        // phrase goes. What changes is only whether there is something to say.
        if (!options.RequireProof)
            return ToolResult.Ok(
                $"Go and say something where I can hear you, in the next {Lifetime.TotalMinutes:0} " +
                "minutes, and tell me it is you — I will take your word for it and learn your voice.");

        // Said as a sentence rather than spelled out, which is the entire point of words: a
        // recogniser hands back three ordinary nouns intact where it would have mangled a
        // string of letters into something else entirely.
        return ToolResult.Ok(
            $"Say these three words out loud where I can hear you: {phrase}. They are good for " +
            $"{Lifetime.TotalMinutes:0} minutes, and after that I will know your voice.");
    }

    private async ValueTask<ToolResult> ClaimAsync(string phrase, Participant claimant, CancellationToken ct)
    {
        // Redeemed before anything else is checked, so that presenting a phrase spends it
        // however the attempt then turns out. A phrase that survives a failed attempt is one
        // somebody else can still try, and the failures below are reachable by anyone —
        // "say it out loud instead" is a refusal an eavesdropper can collect for free.
        if (!Redeem(phrase, claimant, out string person, out string displayName, out int waiting))
            return ToolResult.Error(waiting switch
            {
                0 when !options.RequireProof =>
                    "Nobody has asked me to learn their voice. Ask me that somewhere I already know " +
                    "who you are, and then come back and say something here.",

                > 1 when !options.RequireProof =>
                    "More than one person is waiting for me to learn their voice, and with nothing to " +
                    "go on I cannot tell which of you this is. Ask me again in a few minutes, one at " +
                    "a time.",

                _ => "Those are not words I gave out, or they have expired — say them again if I " +
                     "misheard, or ask for a new set."
            });

        // The one thing that makes this safe: whichever voice said the words is the voice
        // that gets bound, so a model repeating a phrase it read somewhere binds nothing.
        if (VoiceAccounts.VoiceprintOf(claimant.Id) is not { } voiceprintId)
            return ToolResult.Error(
                "Say the words out loud, where I can hear your voice — that is the part I am learning. " +
                "Typing them here tells me nothing about how you sound, so those are spent; ask for more.");

        if (voiceprints.Get(voiceprintId) is not { } print)
            return ToolResult.Error("I have lost track of the voice saying this. Ask for another phrase.");

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

    /// <summary>
    /// Turns whatever the claiming half was given into the person it stands for.
    ///
    /// A phrase is looked up as a phrase in either mode, so one already issued still works
    /// after the setting changes underneath it. Only the empty-handed case differs.
    /// </summary>
    private bool Redeem(
        string phrase, Participant claimant, out string person, out string displayName, out int waiting)
    {
        if (options.RequireProof || !string.IsNullOrWhiteSpace(phrase))
        {
            waiting = 0;
            return _pending.TryRedeem(phrase, out person, out displayName);
        }

        return _pending.TryRedeemSole(claimant.StableKey, out person, out displayName, out waiting);
    }
}
