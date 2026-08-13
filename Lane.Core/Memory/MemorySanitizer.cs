using Lane.Core.Messages;

namespace Lane.Core.Memory;

/// <summary>
/// Keeps tool traffic out of memory that is replayed as conversation turns.
///
/// The hazard is specific and expensive. A sliding window stores an assistant message that
/// asked for a tool; the tool results land in the same window, or do not; the window later
/// trims and cuts between them. The next request then carries a tool_use with no matching
/// tool_result, and the provider rejects it — not once, but every time, until the window
/// rolls past the orphan.
///
/// Within a single agent run the model already sees its own tool traffic as real turns.
/// Once the run is over, only the text it produced is worth remembering, so stripping here
/// costs nothing and removes the whole class of failure.
/// </summary>
public static class MemorySanitizer
{
    /// <summary>
    /// The message as it should be stored, or null when nothing remains worth keeping.
    /// </summary>
    public static LaneMessage? ForMemory(LaneMessage message)
    {
        if (message.Role == LaneRole.Tool) return null;

        bool hasToolParts = false;
        List<ContentPart> kept = [];

        foreach (ContentPart part in message.Content)
        {
            switch (part)
            {
                case ToolUsePart or ToolResultPart:
                    hasToolParts = true;
                    break;

                // Thinking blocks carry signatures that are only valid alongside the turn
                // they came from, so they are not replayed either.
                case ThinkingPart:
                    hasToolParts = true;
                    break;

                default:
                    kept.Add(part);
                    break;
            }
        }

        if (!hasToolParts) return message;

        if (kept.Count == 0) return null;

        return message with { Content = kept };
    }
}
