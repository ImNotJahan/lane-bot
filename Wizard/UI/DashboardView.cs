using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Wizard.Head;
using Wizard.LLM;

namespace Wizard.UI
{
    public sealed class DashboardView : Window
    {
        public DashboardView(Bot bot, ILLM llm)
        {
            Title = "lane dashboard";

            X = Y = 0;

            Width  = Dim.Fill();
            Height = Dim.Fill();

            NextThoughtView nextThoughtView = new(bot)
            {
                X = Y = 0,

                Width  = Dim.Percent(20),
                Height = Dim.Percent(40)
            };
            LogTailView logTailView = new(200)
            {
                X = 0,
                Y = Pos.Bottom(nextThoughtView),

                Width  = Dim.Fill(),
                Height = Dim.Fill()
            };
            TokenView tokenView = new(llm)
            {
                X      = Pos.Right(nextThoughtView),
                Y      = 0,
                Width  = Dim.Fill(),
                Height = Dim.Percent(40)
            };

            Add(nextThoughtView);
            Add(logTailView);
            Add(tokenView);
        }
    }
}