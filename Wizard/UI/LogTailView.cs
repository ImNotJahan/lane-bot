using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Wizard.Utility;

namespace Wizard.UI
{
    public sealed class LogTailView : FrameView
    {
        readonly ListView listView;

        private readonly ObservableCollection<string> entries = [];
         
        public LogTailView(int maxEntries)
        {
            Title  = "LOG TAIL";

            listView = new ListView
            {
                X      = 0, 
                Y      = 0,
                Width  = Dim.Fill(),
                Height = Dim.Fill()
            };

            listView.SetSource(entries);

            Add(listView);

            Logger.Buffer().EntryAdded += (entry) => App?.Invoke(() =>
            {
                foreach (string line in entry.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    entries.Add(line);

                    if(entries.Count > maxEntries) entries.RemoveAt(0);
                } 

                listView.SelectedItem = entries.Count - 1;
                listView.SetNeedsDraw();
            });

            listView.Activated += (sender, args) =>
            {
                if(
                    App is null 
                    || listView.SelectedItem is null 
                    || listView.SelectedItem < 0 
                    || listView.SelectedItem >= entries.Count
                ) return;

                MessageBox.Query(App, "LOG ENTRY", entries[(int) listView.SelectedItem], wrapMessage: true, buttons: ["OK"]);
            };
        }
    }
}