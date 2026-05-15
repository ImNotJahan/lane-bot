using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Wizard.LLM;

namespace Wizard.UI
{
    public sealed class TokenView : FrameView
    {
        public TokenView(ILLM[] llms)
        {
            Title = "TOKEN/CACHE USAGE";

            TokenTable table = new(llms);

            TableView infoTable = new(table)
            {
                X = Y = 0,

                Width  = Dim.Fill(),
                Height = Dim.Auto(),

                Style = new()
                {
                    ShowHorizontalHeaderOverline = false,
                }
            };

            GraphView tokenGraph = new()
            {
                X            = 0,
                Y            = Pos.Bottom(infoTable),
                Width        = Dim.Fill(),
                Height       = Dim.Fill(),
                MarginBottom = 1,
                MarginLeft   = 3,
            };

            const int BarCount = 3;
            const int BarWidth = 1;
            const float SpaceBetweenBars = 0.25f;

            MultiBarSeries bars = new(BarCount, BarWidth, SpaceBetweenBars, [
                new Terminal.Gui.Drawing.Attribute(ColorName16.Black, ColorName16.Blue),
                new Terminal.Gui.Drawing.Attribute(ColorName16.Black, ColorName16.Red),
                new Terminal.Gui.Drawing.Attribute(ColorName16.Black, ColorName16.Yellow),
            ]);

            tokenGraph.Series.Add(bars);

            tokenGraph.AxisY.Increment = 1000;
            tokenGraph.CellSize        = new(0.25f, 200);

            Add(infoTable, tokenGraph);

            table.Updated += infoTable.Update;

            int maxValue = 1;

            foreach (ILLM llm in llms)
            {
                llm.TokenUsage += (input, output, cached) =>
                {
                    bars.AddBars("", new Rune(' '), [input, output, cached]);

                    int barMax = Math.Max(input, Math.Max(output, cached));
                    if (barMax > maxValue)
                    {
                        maxValue = barMax;
                        int graphHeight            = (int)(tokenGraph.Viewport.Height - tokenGraph.MarginBottom);
                        float cellHeight           = graphHeight > 0 ? (float)maxValue / graphHeight : maxValue;
                        tokenGraph.CellSize        = new(tokenGraph.CellSize.X, cellHeight);
                        tokenGraph.AxisY.Increment = cellHeight * (graphHeight / 4f);
                    }

                    App?.Invoke(tokenGraph.SetNeedsDraw);
                };
            }
        }

        private sealed class TokenTable : ITableSource
        {
            public event Action? Updated;

            int inputRun  = 0;
            int outputRun = 0;
            int cachedRun = 0;

            public TokenTable(ILLM[] llms)
            {
                foreach (ILLM llm in llms)
                {
                    llm.TokenUsage += (input, output, cached) =>
                    {
                        inputRun  += input;
                        outputRun += output;
                        cachedRun += cached;

                        Updated?.Invoke();
                    };
                }
            }

            public object this[int row, int col]
            {
                get
                {
                    if (row != 0) throw new IndexOutOfRangeException();

                    return col switch
                    {
                        0 => inputRun,
                        1 => outputRun,
                        2 => cachedRun,
                        3 => inputRun + outputRun == 0 ? "0%" : 100 * cachedRun / (inputRun + outputRun) + "%",
                        _ => throw new IndexOutOfRangeException()
                    };
                }
            }

            public string[] ColumnNames => ["input", "output", "cached", "cache rate"];

            public int Columns => 4;
            public int Rows    => 1;
        }
    }
}
