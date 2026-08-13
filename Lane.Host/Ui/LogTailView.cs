using System.Collections.ObjectModel;
using Lane.Host.Logging;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Lane.Host.Ui;

public sealed class LogTailView : FrameView
{
    private readonly ObservableCollection<string> _lines = [];
    private readonly ListView _list;
    private readonly int _maxLines;

    public LogTailView(BufferedLogSink sink, int maxLines = 300)
    {
        Title = "LOG";
        _maxLines = maxLines;

        _list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _list.SetSource(_lines);

        Add(_list);

        foreach (LogEntry entry in sink.Snapshot()) Append(entry);

        sink.EntryAdded += entry => App?.Invoke(() =>
        {
            Append(entry);
            _list.SetNeedsDraw();
        });

        // Long lines are truncated in the list, so full text is a keypress away.
        _list.Activated += (_, _) =>
        {
            if (App is null || _list.SelectedItem is not int index) return;
            if (index < 0 || index >= _lines.Count) return;

            MessageBox.Query(App, "LOG ENTRY", _lines[index], wrapMessage: true, buttons: ["OK"]);
        };
    }

    private void Append(LogEntry entry)
    {
        _lines.Add(entry.Short);

        while (_lines.Count > _maxLines) _lines.RemoveAt(0);

        _list.SelectedItem = _lines.Count - 1;
    }
}
