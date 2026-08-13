using Lane.Surfaces.Terminal;

namespace Lane.Host.Ui;

/// <summary>Points the terminal surface at the dashboard's chat pane instead of the console.</summary>
public sealed class ChatTerminalIo(ChatView chat) : ITerminalIo
{
    public TextReader Reader { get; } = chat.Reader;
    public TextWriter Writer { get; } = chat.Writer;

    /// <summary>The pane has its own input line.</summary>
    public bool ShowPrompt => false;

    /// <summary>Input only ends when the dashboard is already shutting down.</summary>
    public bool ExitOnEndOfInput => false;
}
