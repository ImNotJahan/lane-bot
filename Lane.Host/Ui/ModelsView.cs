using Lane.Core.Events;
using Lane.Core.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Lane.Host.Ui;

/// <summary>
/// One row per model instance: what it has cost and how well the prompt cache is working.
///
/// Fed entirely from the event bus. v2's equivalent had to be handed a reference to every
/// model in order to subscribe to each one's usage event, which is precisely the shape that
/// stops working once "how many models are there" is a configuration question.
/// </summary>
public sealed class ModelsView : FrameView
{
    private readonly UsageTable _table;
    private readonly TableView  _view;

    public ModelsView(IEventBus bus, ILanguageModelRegistry models)
    {
        Title = "MODELS";

        _table = new UsageTable(models);

        _view = new TableView(_table)
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            Style = new TableStyle { ShowHorizontalHeaderOverline = false }
        };

        Add(_view);

        bus.Subscribe<TokenUsageEvent>(evt =>
        {
            _table.Record(evt);

            // Marshalled onto the UI thread; the bus fires from whichever session pump
            // happened to make the call.
            App?.Invoke(() =>
            {
                _view.Update();
                _view.SetNeedsDraw();
            });
        });
    }

    private sealed class UsageTable : ITableSource
    {
        private readonly Lock _gate = new();
        private readonly List<Row> _rows = [];

        public UsageTable(ILanguageModelRegistry models)
        {
            // Seeded from configuration so an unused model is visibly present at zero,
            // rather than looking like it does not exist.
            foreach (ILanguageModel model in models.All)
                _rows.Add(new Row(model.Descriptor.InstanceId, RolesFor(models, model.Descriptor.InstanceId)));
        }

        public void Record(TokenUsageEvent evt)
        {
            lock (_gate)
            {
                Row? row = _rows.FirstOrDefault(r => r.Instance == evt.ModelInstanceId);

                if (row is null)
                {
                    row = new Row(evt.ModelInstanceId, "");
                    _rows.Add(row);
                }

                row.Calls++;
                row.Input      += evt.Usage.Input;
                row.Output     += evt.Usage.Output;
                row.CacheRead  += evt.Usage.CacheRead;
                row.CacheWrite += evt.Usage.CacheWrite;
                row.LastLatency = evt.Usage.Latency;
            }
        }

        public string[] ColumnNames => ["model", "roles", "calls", "in", "out", "cached", "hit", "last"];

        public int Columns => ColumnNames.Length;

        public int Rows { get { lock (_gate) return Math.Max(1, _rows.Count); } }

        public object this[int row, int col]
        {
            get
            {
                lock (_gate)
                {
                    if (_rows.Count == 0) return col == 0 ? "(none)" : "";
                    if (row < 0 || row >= _rows.Count) return "";

                    Row r = _rows[row];

                    return col switch
                    {
                        0 => r.Instance,
                        1 => r.Roles,
                        2 => r.Calls,
                        3 => r.Input,
                        4 => r.Output,
                        5 => r.CacheRead,
                        6 => r.CacheRate,
                        7 => r.LastLatency == TimeSpan.Zero ? "-" : $"{(int)r.LastLatency.TotalMilliseconds}ms",
                        _ => ""
                    };
                }
            }
        }

        private static string RolesFor(ILanguageModelRegistry models, string instance) =>
            string.Join(",", models.RoleBindings
                .Where(kv => string.Equals(kv.Value, instance, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key));

        private sealed class Row(string instance, string roles)
        {
            public string Instance { get; } = instance;
            public string Roles    { get; } = roles;

            public int Calls;
            public int Input;
            public int Output;
            public int CacheRead;
            public int CacheWrite;
            public TimeSpan LastLatency;

            /// <summary>
            /// Cache reads as a share of all input. The number to watch after changing a
            /// prompt or a tool list, since a broken cache costs money rather than erroring.
            /// </summary>
            public string CacheRate => Input == 0 ? "-" : $"{100 * CacheRead / Input}%";
        }
    }
}
