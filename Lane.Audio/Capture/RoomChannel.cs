using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Capture;

/// <summary>
/// Somewhere to mirror a room's conversation as text.
///
/// Declared here, and deliberately not as "the dashboard": this project knows what a
/// microphone is and nothing about what is drawing on the terminal. The host joins the two
/// up, because knowing that a TUI exists and that it owns the screen is the host's job.
/// </summary>
public interface IRoomEcho
{
    TextWriter Writer { get; }
}

/// <summary>
/// The room's write-back handle: how Lane answers people who spoke to a microphone.
///
/// Without one of these a microphone is a one-way conversation. Everything upstream works —
/// the words are heard, attributed, answered and written to memory — and then
/// <c>DeliveryStage</c> finds nothing attached and drops the reply, which from inside the
/// room is indistinguishable from her having nothing to say.
///
/// It carries voice and text together, the way the API's voice socket does, because they are
/// two renderings of one reply rather than two places to send it. The speaking is the real
/// answer: <c>VoiceObserver</c> resolves the voice output and streams synthesis into it a
/// clause at a time, so she starts talking before the sentence is finished. The text is a
/// mirror for whoever is watching, and where nobody is it goes to the log rather than
/// nowhere — a reply that vanished silently is the bug this class exists to fix, and it
/// would be a poor showing to reintroduce it one layer up.
/// </summary>
public sealed class RoomChannel(
    SessionId id,
    IVoiceOutput speaker,
    IRoomEcho? echo,
    string laneName,
    ILogger log)
    : SessionChannelBase(
          id, id.Surface,
          ChannelCapabilities.Voice | ChannelCapabilities.Text | ChannelCapabilities.Interrupt),
      IVoiceOutput, ITextOutput
{
    public AudioFormat Format => speaker.Format;

    public Task PlayAsync(IAsyncEnumerable<AudioFrame> audio, CancellationToken ct) =>
        speaker.PlayAsync(audio, ct);

    public Task StopAsync() => speaker.StopAsync();

    public Task SendAsync(OutboundText text, CancellationToken ct)
    {
        if (echo is null)
        {
            log.LogDebug("[{Lane}] {Text}", laneName, text.Text);

            return Task.CompletedTask;
        }

        try
        {
            echo.Writer.WriteLine($"{laneName}: {text.Text}");
            echo.Writer.Flush();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The pane going away must not fail a turn she has already spoken aloud.
            log.LogDebug(ex, "Could not mirror the room's reply on {Session}", Id);
        }

        return Task.CompletedTask;
    }
}
