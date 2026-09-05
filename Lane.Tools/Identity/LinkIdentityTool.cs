using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Identity;

/// <summary>
/// Joins two of one person's accounts, so User-scoped memory follows them from Discord to
/// the terminal to the API instead of fragmenting per surface.
///
/// The link is <b>proven, never inferred</b>. Lane asks for a code on one account and the
/// same person repeats it on the other; each half only ever links the account that is
/// actually speaking in that turn. That matters more here than anywhere else in the
/// codebase: the configured map exists precisely because guessing two accounts are one
/// person merges two people's memories, and a tool that let the model assert a link from
/// "they both say they are Jahan" would be that guess with extra steps. It cannot state
/// somebody else's account either, so the worst a model can do with this tool is offer a
/// code to the person in front of it.
///
/// The code can be turned off — see <see cref="IdentityToolOptions.RequireProof"/> — and
/// then being present on both accounts is the whole of the proof. Everything else here is
/// unchanged, including the part that matters most: each half still only ever acts on the
/// account actually speaking, so no setting makes the model able to assert a link.
[LaneTool]
public sealed class LinkIdentityTool(
    IIdentityResolver resolver,
    IIdentityDirectory directory,
    IdentityToolOptions options,
    TimeProvider time,
    ILogger<LinkIdentityTool> log) : Tool<LinkIdentityTool.Args>
{
    /// <summary>Long enough to switch device or app, short enough that a code left lying about expires.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Keyed on the account so asking twice replaces rather than accumulates; the display
    /// name rides along because the claiming half needs it to resolve the issuer.
    /// </summary>
    private readonly ProofCodes<ParticipantId, string> _pending = new(time, Lifetime);

    public sealed record Args(
        [property: Description(
            "The code you were given on your other account. Leave it out to be given a code here.")]
        string Code = "");

    protected override string Name => "link_identity";

    protected override string Description => options.RequireProof
        ? "Join two accounts belonging to the same person, so you remember them as one across surfaces. " +
          "Call it with no code to be given one, then call it again with that code when they repeat it " +
          "to you on their other account. Only ever links whoever is speaking to you now — you cannot " +
          "link somebody on their behalf, and you should not offer it unless they ask."

        : "Join two accounts belonging to the same person, so you remember them as one across surfaces. " +
          "Call it with nothing on one account, then call it again — still with nothing — when they say " +
          "so on their other one. Only ever links whoever is speaking to you now — you cannot link " +
          "somebody on their behalf, and you should not offer it unless they ask.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    /// <summary>
    /// A conversation only, and the session gate is checked again inside: a code read aloud
    /// in a channel is a code a bystander can claim on their own account.
    /// </summary>
    protected override ToolAvailability Availability =>
        new() { AllowedTurns = TurnKind.Respond, RequiresSession = true };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!directory.Durable)
            return ToolResult.Error(
                "Linking is not available — nothing here would remember it past a restart.");

        if (context.Requester is not { IsLane: false } requester)
            return ToolResult.Error("I cannot tell who is speaking, so there is nobody to link.");

        if (context.Descriptor?.IsDirect != true)
            return ToolResult.Error(
                "Not here — anyone reading this channel could claim it was them. Ask me one to one.");

        // Which half this is. A code settles it; without one there is nothing to go on but
        // order, so a link already waiting is one this account is completing.
        //
        // Which is also the sharpest edge of trusting people: somebody asking to start their
        // own link, while another is outstanding, finishes that one instead. Two accounts
        // that asked in turn get joined whether or not they are the same person. There is no
        // fixing that from in here — it is what the code was for.
        bool claiming = !string.IsNullOrWhiteSpace(args.Code)
                     || (!options.RequireProof && _pending.Outstanding(requester.Id) > 0);

        return claiming
            ? await ClaimAsync(args.Code, requester, ct).ConfigureAwait(false)
            : Issue(requester);
    }

    private ToolResult Issue(Participant requester)
    {
        if (_pending.Issue(requester.Id, requester.DisplayName) is not { } code)
            return ToolResult.Error("Too many links are part-way through. Try again in a few minutes.");

        log.LogInformation("Expecting {Account} on another account", requester.Id);

        // Staged either way — being on both accounts is what is left of the proof once the
        // code goes. What changes is only whether there is something to carry across.
        if (!options.RequireProof)
            return ToolResult.Ok(
                $"Right. Within the next {Lifetime.TotalMinutes:0} minutes, say so on your other " +
                "account — one to one there as well — and I will take your word for it and join them.");

        return ToolResult.Ok(
            $"Code {code}, good for {Lifetime.TotalMinutes:0} minutes. Tell them to say it to me on their " +
            "other account — one to one there as well — and I will know the two accounts are one person.");
    }

    private async ValueTask<ToolResult> ClaimAsync(string code, Participant claimant, CancellationToken ct)
    {
        // Consumed whatever happens next, including the refusals below: a code that survives
        // a failed attempt is one somebody else can still try.
        if (!Redeem(code, claimant, out ParticipantId account, out string displayName))
            return ToolResult.Error("That is not a code I gave out, or it has expired. Ask for a new one.");

        if (account == claimant.Id)
            return ToolResult.Error(
                "That is the account the code was issued to. Say it on the other one, which is the whole point.");

        string? issuerGlobal   = resolver.Resolve(account, displayName).GlobalUserId;
        string? claimantGlobal = claimant.GlobalUserId;

        if (issuerGlobal is not null && claimantGlobal is not null)
        {
            if (string.Equals(issuerGlobal, claimantGlobal, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Ok("Those two are already the same person to me.");

            // Merging two people who each already have a history is not this tool's to do:
            // memory is already written under both ids, and nothing here can unpick which
            // half belongs to whom afterwards.
            return ToolResult.Error(
                $"Both accounts are already people I know — '{issuerGlobal}' and '{claimantGlobal}'. " +
                "Joining those two takes a change to my configuration.");
        }

        string globalId = issuerGlobal ?? claimantGlobal ?? Mint(claimant.DisplayName);

        await directory.LinkAsync(globalId, [account, claimant.Id], ct).ConfigureAwait(false);

        // A name chosen before the link was stored against the account; carry it onto the
        // person, or "call me Jax" quietly stops working the moment they link a second
        // account. The claimant's wins, since that is the account they are speaking from.
        string? chosen = directory.NameFor(claimant.StableKey)
                      ?? directory.NameFor(issuerGlobal ?? account.ToString());

        if (chosen is not null && directory.NameFor(globalId) is null)
            await directory.SetNameAsync(globalId, chosen, ct).ConfigureAwait(false);

        log.LogInformation("Linked {A} and {B} as {GlobalId}", account, claimant.Id, globalId);

        string summary = $"{displayName} ({account}) and {claimant.DisplayName} ({claimant.Id})";

        // Participants are resolved as each message arrives, so the one being answered right
        // now still carries the old id. Saying so beats her claiming a change that the very
        // next thing she says will contradict.
        return ToolResult.Ok(
            $"Linked: {summary} are one person to me now, from their next message on.")
            .RememberAs($"[linked two accounts as one person: {summary}]", MemoryScopeHint.Global);
    }

    /// <summary>
    /// A readable id beats an opaque one in the store and the logs, but it must not land on
    /// somebody who already exists — that would be exactly the silent merge the configured
    /// map is careful to avoid — so it is checked and given random digits to fall back on.
    /// </summary>
    private string Mint(string displayName)
    {
        string slug = Slug(displayName);

        for (int attempt = 0; attempt < 8; attempt++)
        {
            string candidate = $"{slug}-{RandomNumberGenerator.GetHexString(4, lowercase: true)}";

            if (!resolver.IsPerson(candidate)) return candidate;
        }

        return $"{slug}-{Guid.NewGuid():n}";
    }

    private static string Slug(string displayName)
    {
        StringBuilder sb = new();

        foreach (char c in displayName.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');

            if (sb.Length == 24) break;
        }

        string slug = sb.ToString().Trim('-');

        // Display names are surface input and can be emoji end to end, which slugs to nothing.
        return slug.Length == 0 ? "person" : slug;
    }

    /// <summary>
    /// Turns whatever the claiming half was given into the account it stands for.
    ///
    /// A code is looked up as a code in either mode, so one already issued still works after
    /// the setting changes underneath it. Only the empty-handed case differs.
    /// </summary>
    private bool Redeem(string code, Participant claimant, out ParticipantId account, out string displayName)
    {
        if (options.RequireProof || !string.IsNullOrWhiteSpace(code))
            return _pending.TryRedeem(code, out account, out displayName);

        // Only reached with something waiting, since that is what decided this was the
        // claiming half at all. The count is still checked rather than assumed.
        return _pending.TryRedeemSole(claimant.Id, out account, out displayName, out _);
    }
}
