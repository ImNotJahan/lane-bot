using Lane.Audio.Capture;
using Lane.Surfaces.Terminal;

namespace Lane.Host.Ui;

/// <summary>
/// Shows a spoken conversation in the dashboard's conversation pane.
///
/// The join between two things that should not know about each other: Lane.Audio knows what
/// a microphone is and nothing about what is drawing on the terminal, and the terminal I/O
/// knows nothing about rooms. Both are already here, so the host puts them together — the
/// same reason <see cref="ChatTerminalIo"/> exists one line above it in the registration.
/// </summary>
public sealed class TerminalRoomEcho(ITerminalIo io) : IRoomEcho
{
    public TextWriter Writer => io.Writer;
}
