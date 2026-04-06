using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Wizard.Head;

namespace Wizard.UI
{
    public sealed class NextThoughtView : FrameView
    {
        public NextThoughtView(Bot bot)
        {
            Title = "NEXT THOUGHT";

            Label timeLabel = new()
            {
                Text = "00:00:00 ",

                X = Pos.Center(),
                Y = Pos.Center()
            };
            
            Add(timeLabel);

            bot.TimeUntilThoughtChanged += (newTime) =>
            {
                TimeSpan timespan = TimeSpan.FromSeconds(newTime);            

                timeLabel.Text = timespan.ToString(@"hh\:mm\:ss");
            };
        }
    }
}