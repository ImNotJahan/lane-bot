using System.Collections.Concurrent;
using Lane.Core.Events;
using Lane.Core.Sessions;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Lane.Host.Ui;

/// <summary>
/// Every live conversation at once — which is the whole point of the rewrite, so it is
/// worth being able to see.
/// </summary>
public sealed class SessionsView : FrameView
{
    private readonly SessionTable _table;
    private readonly TableView    _view;

    public SessionsView(IEventBus bus, ISessionRegistry sessions)
    {
        Title = "SESSIONS";

        _table = new SessionTable(sessions);

        _view = new TableView(_table)
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            Style = new TableStyle { ShowHorizontalHeaderOverline = false }
        };

        Add(_view);

        bus.Subscribe<SessionEvent>(_ => Refresh());
        bus.Subscribe<TurnStarted>(evt => { _table.Note(evt.Session.Value, "running"); Refresh(); });
        bus.Subscribe<TurnCompleted>(evt => { _table.Note(evt.Session.Value, "idle"); Refresh(); });
        bus.Subscribe<TurnFailed>(evt => { _table.Note(evt.Session.Value, "failed"); Refresh(); });
        bus.Subscribe<ToolInvokedEvent>(evt =>
        {
            if (evt.Session is not null) _table.Note(evt.Session, $"tool: {evt.Tool}");
            Refresh();
        });
    }

    private void Refresh() => App?.Invoke(() =>
    {
        _view.Update();
        _view.SetNeedsDraw();
    });

    private sealed class SessionTable(ISessionRegistry sessions) : ITableSource
    {
        private readonly ConcurrentDictionary<string, string> _notes = new();

        public void Note(string session, string note) => _notes[session] = note;

        public string[] ColumnNames => ["session", "name", "state", "activity", "idle"];

        public int Columns => ColumnNames.Length;

        public int Rows => Math.Max(1, sessions.Active.Count);

        public object this[int row, int col]
        {
            get
            {
                Session[] active = [.. sessions.Active.OrderBy(s => s.Id.Value, StringComparer.Ordinal)];

                if (active.Length == 0) return col == 0 ? "(no sessions)" : "";
                if (row < 0 || row >= active.Length) return "";

                Session session = active[row];

                return col switch
                {
                    0 => session.Id.Value,
                    1 => session.Descriptor.DisplayName,
                    2 => session.State.ToString(),
                    3 => _notes.GetValueOrDefault(session.Id.Value, "-"),
                    4 => Idle(session.LastActivity),
                    _ => ""
                };
            }
        }

        private static string Idle(DateTimeOffset since)
        {
            TimeSpan elapsed = DateTimeOffset.UtcNow - since;

            return elapsed switch
            {
                { TotalSeconds: < 60 } => $"{(int)elapsed.TotalSeconds}s",
                { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m",
                _                      => $"{(int)elapsed.TotalHours}h"
            };
        }
    }
}
