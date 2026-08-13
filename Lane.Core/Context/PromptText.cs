using Lane.Core.Messages;

namespace Lane.Core.Context;

/// <summary>
/// Renders a message's text for the position it occupies in a provider request.
///
/// This exists because no provider's wire format has a per-message author field: a turn is
/// just "user" or "assistant". When a coalesced batch or a multi-party channel puts several
/// speakers into one turn, attribution has to be carried in the text or it is lost — Lane
/// would see "hey lanewhat's up" and have no idea two different people were talking.
///
/// Shared by every provider adapter so the convention cannot drift between them.
/// </summary>
public static class PromptText
{
    /// <summary>
    /// The speaker prefix for a message, or empty when it needs none.
    ///
    /// Assistant messages deliberately go unprefixed: prefixing Lane's own turns would
    /// teach her to emit the prefix herself, which is the bug v2 papered over by stripping
    /// a leading "Lane says:" off every message it handled.
    /// </summary>
    public static string Prefix(LaneMessage message) => message switch
    {
        { Role: LaneRole.Assistant }     => "",
        { Kind: MessageKind.Observation } => "",   // tool output is already self-describing
        _                                 => $"{message.Author.DisplayName}: "
    };

    /// <summary>
    /// One text part rendered for a provider turn. The trailing newline is what keeps a
    /// coalesced batch from running together — providers concatenate adjacent text blocks
    /// with nothing between them.
    /// </summary>
    public static string Render(LaneMessage message, string text, bool isFirstTextPart)
    {
        if (message.Role == LaneRole.Assistant) return text;

        string body = isFirstTextPart ? Prefix(message) + text : text;

        return body.EndsWith('\n') ? body : body + "\n";
    }
}
