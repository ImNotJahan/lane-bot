using System.ComponentModel;
using System.Globalization;
using System.Text;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Identity;

/// <summary>
/// Changes what Lane calls the person she is talking to, at their own request.
///
/// It renames <em>the speaker</em> and takes no account to act on, for the same reason
/// <see cref="LinkIdentityTool"/> does not: a tool that accepted "call Jahan something else"
/// would let anyone rename anyone, and a name is how the transcript attributes what was
/// said. The name is stored against the person rather than the account, so someone who has
/// linked their surfaces is called the same thing on all of them.
/// </summary>
[LaneTool]
public sealed class SetMyNameTool(IIdentityDirectory directory, ILogger<SetMyNameTool> log)
    : Tool<SetMyNameTool.Args>
{
    /// <summary>Long enough for a name, short enough that it cannot become a paragraph.</summary>
    private const int MaxLength = 40;

    public sealed record Args(
        [property: Description(
            "What to call them from now on. Leave it out to go back to the name their account carries.")]
        string Name = "");

    protected override string Name => "set_my_name";

    protected override string Description =>
        "Change what you call the person you are speaking to, when they ask you to — a nickname, a " +
        "different spelling, a new name. Only ever renames whoever is speaking to you now, and only " +
        "their name with you: it changes nothing on their account. Leave the name out to go back to " +
        "whatever their account is called.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    /// <summary>
    /// No one-to-one requirement, unlike linking: nothing secret changes hands, and "call me
    /// Jax" is a perfectly ordinary thing to say in a channel.
    /// </summary>
    protected override ToolAvailability Availability =>
        new() { AllowedTurns = TurnKind.Respond, RequiresSession = true };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!directory.Durable)
            return ToolResult.Error("I cannot change that — nothing here would remember it past a restart.");

        if (context.Requester is not { IsLane: false } requester)
            return ToolResult.Error("I cannot tell who is speaking, so there is nobody to rename.");

        string person = requester.StableKey;

        if (string.IsNullOrWhiteSpace(args.Name))
        {
            if (directory.NameFor(person) is null)
                return ToolResult.Ok($"I was already calling them {requester.DisplayName}.");

            await directory.SetNameAsync(person, null, ct).ConfigureAwait(false);

            return ToolResult.Ok("Back to whatever their account is called, from their next message on.")
                             .RememberAs($"[{requester.DisplayName} went back to their account name]",
                                          MemoryScopeHint.Global);
        }

        string name = Clean(args.Name);

        // Cleaned to nothing is different from asked for nothing: one is a request to go back
        // to the account name, the other is a name there is no way to write down.
        if (name.Length == 0)
            return ToolResult.Error("There is nothing in that I could write down as a name.");

        if (name.Length > MaxLength)
            return ToolResult.Error($"That is longer than a name I can use — {MaxLength} characters at most.");

        // Her own turns are deliberately unprefixed, so someone answering to "Lane" makes the
        // transcript ambiguous about who said what — including to her.
        if (string.Equals(name, Participant.LaneInternal.DisplayName, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Error("That one is taken — it is my name.");

        if (directory.NameIsTaken(name, person))
            return ToolResult.Error($"Somebody else already goes by {name}, and two of them would be one too many.");

        // Only chosen names are enumerable, so this catches the case that matters most: taking
        // the name of somebody in the room, whose messages would then read as theirs.
        Participant? impersonated = context.Descriptor?.KnownParticipants
            .FirstOrDefault(p => p.StableKey != person
                              && string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));

        if (impersonated is not null)
            return ToolResult.Error($"{name} is somebody else here. Pick something that is not already theirs.");

        await directory.SetNameAsync(person, name, ct).ConfigureAwait(false);

        log.LogInformation("{Person} asked to be called {Name}", person, name);

        // Participants are resolved as each message arrives, so the one being answered right
        // now still carries the old name.
        return ToolResult.Ok($"Calling them {name} from their next message on.")
                         .RememberAs($"[{requester.DisplayName} asked to be called {name}]",
                                      MemoryScopeHint.Global);
    }

    /// <summary>
    /// A name is not free text: it becomes the literal <c>"{name}: "</c> prefix on every one
    /// of their turns, so anything that could forge a second speaker or a system line has to
    /// come out first. Newlines and control characters go, and so does every formatting
    /// character — zero-width marks, bidirectional overrides, the joiners.
    ///
    /// By category rather than by a list of the ones worth worrying about: a list is a thing
    /// to be incomplete, and this one already was — the left-to-right and right-to-left
    /// *marks* sit outside the override range and would have gone straight through. The cost
    /// is that a name built out of a zero-width-joiner emoji sequence comes apart, which is a
    /// worse-looking name rather than a name that can be read as somebody else.
    /// </summary>
    private static string Clean(string name)
    {
        StringBuilder clean = new(name.Length);

        foreach (char c in name)
        {
            if (char.IsControl(c) || c is '\n' or '\r' or '\t')
            {
                if (clean.Length > 0 && clean[^1] != ' ') clean.Append(' ');
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;

            clean.Append(c);
        }

        // A trailing colon would double the one attribution already adds.
        return clean.ToString().Trim().TrimEnd(':').Trim();
    }
}
