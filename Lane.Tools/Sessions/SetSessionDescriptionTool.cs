using System.ComponentModel;
using System.Globalization;
using System.Text;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Sessions;

/// <summary>
/// Lets Lane write down what a conversation is, in her own words, and have it in front of
/// her every time she speaks there.
///
/// The scratchpad already lets her write things down, but a note only exists when she thinks
/// to read it — which is exactly the wrong shape for "everyone here speaks German" or "this
/// channel is the D&amp;D game and I am running it". Those have to be true of every reply,
/// including the first one after a restart, so they belong in the prompt rather than behind
/// a tool call.
///
/// One description per conversation, keyed by memory group like session-scoped memory, so
/// describing a Discord text channel also describes the voice channel beside it.
/// </summary>
[LaneTool]
public sealed class SetSessionDescriptionTool(
    ISessionDescriptions descriptions, ILogger<SetSessionDescriptionTool> log)
    : Tool<SetSessionDescriptionTool.Args>
{
    /// <summary>
    /// Room for a few sentences and no more. Every character here is re-sent on every turn
    /// in that conversation for as long as it stands, so the cap is what keeps a standing
    /// note from quietly becoming a second persona.
    /// </summary>
    private const int MaxLength = 500;

    public sealed record Args(
        [property: Description(
            "What to write down about this conversation. Replaces whatever is there now. " +
            "Leave it out to erase it.")]
        string Description = "");

    protected override string Name => "set_session_description";

    protected override string Description =>
        "Write down what this conversation is — what it is for, who is in it, how you mean to be in " +
        "it. What you write stays in front of you every time you speak here, and only here, until you " +
        "change it. Use it for things that hold for the whole conversation rather than for one moment " +
        "in it; a note on your scratchpad is the better place for anything you only need now and then. " +
        "Leave the description out to erase it.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    /// <summary>
    /// Describing a conversation requires being in one. The monologue is deliberately left
    /// out even though it can see every session: a thought that quietly rewrote what Lane
    /// believes about a channel she is not in would be indistinguishable, from the inside,
    /// from having always believed it.
    /// </summary>
    protected override ToolAvailability Availability =>
        new() { AllowedTurns = TurnKind.Respond, RequiresSession = true };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!descriptions.Durable)
            return ToolResult.Error("I cannot write that down — nothing here would remember it past a restart.");

        if (context.Descriptor is not { } session)
            return ToolResult.Error("I cannot tell which conversation this is, so there is nothing to describe.");

        string? current = descriptions.For(session);

        if (string.IsNullOrWhiteSpace(args.Description))
        {
            if (current is null)
                return ToolResult.Ok($"There was nothing written down about {session.DisplayName}.");

            await descriptions.SetAsync(session.MemoryGroup, null, ct).ConfigureAwait(false);

            return ToolResult.Ok($"Erased what you had written about {session.DisplayName}.")
                             .RememberAs($"[erased the description of {session.DisplayName}]");
        }

        string description = Clean(args.Description);

        // Cleaned to nothing is not the same as asked for nothing: one is a request to
        // erase, the other is text there is no way to write down.
        if (description.Length == 0)
            return ToolResult.Error("There is nothing in that I could write down.");

        // Refused rather than truncated, as an overlong note is: a description cut off
        // mid-sentence is one she reads back every turn and acts on as though it were whole.
        if (description.Length > MaxLength)
            return ToolResult.Error(
                $"That is {description.Length} characters, over the {MaxLength} this can hold. " +
                "It has to fit in every prompt here, so keep it to the part that always applies.");

        if (string.Equals(description, current, StringComparison.Ordinal))
            return ToolResult.Ok($"That is already what you have written about {session.DisplayName}.");

        await descriptions.SetAsync(session.MemoryGroup, description, ct).ConfigureAwait(false);

        log.LogInformation("Described {Session} as: {Description}", session.DisplayName, description);

        // Tool traffic is stripped from memory, so without this the fact that she wrote
        // anything is gone by the next turn — leaving her to explain a note she has no
        // recollection of making.
        return ToolResult
            .Ok(current is null
                ? $"Written down about {session.DisplayName}. You will see it here from now on."
                : $"Replaced what you had written about {session.DisplayName}.")
            .RememberAs($"[wrote down what {session.DisplayName} is]\n{description}");
    }

    /// <summary>
    /// A description is not a name — it is a paragraph, so newlines stay. What comes out is
    /// everything that could make it read as something other than a note Lane wrote: the
    /// other control characters, and every Unicode format character.
    ///
    /// By category rather than by a list, for the reason
    /// <see cref="Lane.Tools.Identity.SetMyNameTool"/> gives — the left-to-right and
    /// right-to-left marks sit outside the override range and would otherwise go straight
    /// through. Here that matters for the operator as much as for the model: text in the
    /// system prompt that renders as one thing in the dashboard and means another is the
    /// sort of thing nobody finds by reading.
    ///
    /// What this cannot do is stop the description from *saying* anything in particular.
    /// Someone in the conversation can talk Lane into writing a standing instruction to
    /// herself, and no amount of character stripping changes that; the cap, the label the
    /// prompt puts on it, and the fact that she can be asked to erase it are the answer.
    /// </summary>
    private static string Clean(string description)
    {
        StringBuilder clean = new(description.Length);

        int newlines = 0;

        foreach (char c in description.ReplaceLineEndings("\n"))
        {
            if (c == '\n')
            {
                // Two at most: a run of blank lines is how a paragraph gets pushed far
                // enough down a prompt to read as a section of its own.
                if (clean.Length > 0 && newlines < 2)
                {
                    clean.Append('\n');
                    newlines++;
                }

                continue;
            }

            // Tabs become spaces rather than vanishing, so a list written with them still
            // reads as one.
            char kept = c == '\t' ? ' ' : c;

            if (char.IsControl(kept)) continue;

            if (CharUnicodeInfo.GetUnicodeCategory(kept) == UnicodeCategory.Format) continue;

            clean.Append(kept);
            newlines = 0;
        }

        return clean.ToString().Trim();
    }
}
