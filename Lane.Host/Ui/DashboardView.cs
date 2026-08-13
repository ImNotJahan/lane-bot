using Lane.Core.Events;
using Lane.Core.Models;
using Lane.Core.Monologue;
using Lane.Core.Sessions;
using Lane.Host.Logging;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Lane.Host.Ui;

/// <summary>
/// The dashboard. Everything on it is driven by the event bus, so it sees any number of
/// models, sessions and surfaces without holding a reference to any of them.
/// </summary>
public sealed class DashboardView : Window
{
    public DashboardView(
        IEventBus bus,
        ISessionRegistry sessions,
        ILanguageModelRegistry models,
        BufferedLogSink logs,
        IMonologueScheduler monologue,
        ChatView? chat)
    {
        Title = "lane";

        X = Y = 0;
        Width  = Dim.Fill();
        Height = Dim.Fill();

        // The two tables get the full width. Side by side they lose their right-hand
        // columns to truncation on any ordinary terminal, and the cache hit rate — the
        // number actually worth watching — is the first thing to go.
        ModelsView modelsView = new(bus, models)
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Percent(30)
        };

        SessionsView sessionsView = new(bus, sessions)
        {
            X = 0, Y = Pos.Bottom(modelsView), Width = Dim.Percent(60), Height = Dim.Percent(25)
        };

        MonologueView monologueView = new(bus, monologue)
        {
            X = Pos.Right(sessionsView), Y = Pos.Bottom(modelsView),
            Width = Dim.Fill(), Height = Dim.Percent(25)
        };

        Add(modelsView, sessionsView, monologueView);

        if (chat is not null)
        {
            chat.X = 0;
            chat.Y = Pos.Bottom(sessionsView);
            chat.Width  = Dim.Percent(55);
            chat.Height = Dim.Fill();

            LogTailView logView = new(logs)
            {
                X = Pos.Right(chat), Y = Pos.Bottom(sessionsView), Width = Dim.Fill(), Height = Dim.Fill()
            };

            Add(chat, logView);
        }
        else
        {
            LogTailView logView = new(logs)
            {
                X = 0, Y = Pos.Bottom(sessionsView), Width = Dim.Fill(), Height = Dim.Fill()
            };

            Add(logView);
        }
    }
}
