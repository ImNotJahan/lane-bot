using Lane.Core.Identity;
using Lane.Core.Sessions;

namespace Lane.Surfaces.Terminal;

/// <summary>
/// Writes Lane's replies to stdout.
///
/// Console access is serialised through a lock because several sessions can share one
/// terminal process, and interleaved half-lines are unreadable.
/// </summary>
public sealed class TerminalChannel(SessionId id, string laneName, TextWriter output)
    : SessionChannelBase(id, id.Surface, ChannelCapabilities.Text), ITextOutput
{
    private static readonly Lock ConsoleLock = new();

    public Task SendAsync(OutboundText text, CancellationToken ct)
    {
        lock (ConsoleLock)
        {
            output.WriteLine();
            output.WriteLine($"{laneName}: {text.Text}");
            output.WriteLine();
            output.Flush();
        }

        return Task.CompletedTask;
    }
}
