using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Wizard.Head;
using Wizard.LLM;

namespace Wizard.UI
{
    public sealed class DashboardView : Window
    {
        public DashboardView(Bot bot, ILLM[] llms)
        {
            Title = "lane dashboard";

            X = Y = 0;

            Width  = Dim.Fill();
            Height = Dim.Fill();

            ConfigView configView = new()
            {
                X = Y = 0,

                Width  = Dim.Percent(30),
                Height = Dim.Auto()
            };
            NextThoughtView nextThoughtView = new(bot)
            {
                X      = 0,
                Y      = Pos.Bottom(configView),
                Width  = Dim.Percent(30),
                Height = Dim.Percent(40) - Dim.Height(configView)
            };
            LogTailView logTailView = new(200)
            {
                X = 0,
                Y = Pos.Bottom(nextThoughtView),

                Width  = Dim.Fill(),
                Height = Dim.Fill()
            };
            TokenView tokenView = new(llms)
            {
                X      = Pos.Right(nextThoughtView),
                Y      = 0,
                Width  = Dim.Fill(),
                Height = Dim.Percent(40)
            };

            Add(configView);
            Add(nextThoughtView);
            Add(logTailView);
            Add(tokenView);
        }
    }
}