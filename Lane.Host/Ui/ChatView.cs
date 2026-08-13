using System.Collections.ObjectModel;
using System.Threading.Channels;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Lane.Host.Ui;

/// <summary>
/// The terminal conversation, as a pane rather than as stdout.
///
/// Terminal.Gui owns the screen, so the terminal surface cannot read from
/// <c>Console.In</c> or write to <c>Console.Out</c> while the dashboard is running. It does
/// not need to know that: the surface takes a <see cref="TextReader"/> and a
/// <see cref="TextWriter"/>, and this view supplies a pair backed by the UI.
/// </summary>
public sealed class ChatView : FrameView
{
    private readonly ObservableCollection<string> _lines = [];
    private readonly ListView _transcript;
    private readonly TextField _input;
    private readonly Channel<string> _typed = Channel.CreateUnbounded<string>();
    private readonly string _userName;

    public ChatView(string userName)
    {
        Title = "CONVERSATION";

        _userName = userName;

        _transcript = new ListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1),

            // A transcript is for reading, and one that can hold focus is one that can take
            // it away from the input — after which typing silently goes nowhere.
            CanFocus = false
        };

        _transcript.SetSource(_lines);

        _input = new TextField
        {
            X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill()
        };

        // Accepting rather than a KeyDown comparison against Key.Enter. Enter reaches a
        // view through the Accept command, which every driver binds for itself — matching
        // one specific Key value instead means any driver that reports Enter with a
        // modifier bit set, or as a newline, simply does nothing when you press it.
        _input.Accepting += (_, args) =>
        {
            args.Handled = true;                 // do not let Accept bubble to the window

            Submit();
        };

        Add(_transcript, _input);

        // Focus has to be claimed once the view is actually live; setting it during
        // construction happens before there is an application to hold it.
        Initialized += (_, _) => _input.SetFocus();
    }

    private void Submit()
    {
        string text = _input.Value ?? "";

        if (string.IsNullOrWhiteSpace(text)) return;

        Append($"{_userName}: {text}");

        _input.Value = "";
        _input.InsertionPoint = 0;

        _typed.Writer.TryWrite(text.Trim());
    }

    /// <summary>Hand these to the terminal surface in place of the console.</summary>
    public TextReader Reader => new ChannelReader(_typed.Reader);

    public TextWriter Writer => new ViewWriter(this);

    public void FocusInput() => App?.Invoke(() => _input.SetFocus());

    private void Append(string line)
    {
        foreach (string part in line.Split('\n'))
        {
            _lines.Add(part);
            while (_lines.Count > 500) _lines.RemoveAt(0);
        }

        _transcript.SelectedItem = _lines.Count - 1;
        _transcript.SetNeedsDraw();

        // A reply arriving must not leave the keyboard pointing at nothing. Focus is only
        // reclaimed when no view holds it — someone who tabbed over to read the log keeps
        // it, which they would not if this reclaimed unconditionally.
        if (!_input.HasFocus && SuperView?.MostFocused is null) _input.SetFocus();
    }

    /// <summary>A reader whose lines arrive from a text field instead of a pipe.</summary>
    private sealed class ChannelReader(ChannelReader<string> lines) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await lines.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public override string? ReadLine() =>
            ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    private sealed class ViewWriter(ChatView view) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(string? value)
        {
            // The surface writes a "> " prompt the pane does not need; only whole lines
            // are worth showing.
            if (string.IsNullOrWhiteSpace(value)) return;

            view.App?.Invoke(() => view.Append(value.TrimEnd()));
        }

        public override void WriteLine(string? value) => Write(value);

        public override void WriteLine() { }
    }
}
