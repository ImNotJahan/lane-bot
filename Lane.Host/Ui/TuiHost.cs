using Lane.Core.Events;
using Lane.Core.Models;
using Lane.Core.Monologue;
using Lane.Core.Sessions;
using Lane.Host.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Terminal.Gui.App;

namespace Lane.Host.Ui;

/// <summary>
/// Runs the dashboard on its own thread and shuts the process down when it closes.
///
/// Terminal.Gui has a single-threaded UI loop that wants to own the process's main
/// interaction with the terminal, so it gets a dedicated thread rather than fighting the
/// generic host's.
/// </summary>
public sealed class TuiHost(
    IEventBus bus,
    ISessionRegistry sessions,
    ILanguageModelRegistry models,
    BufferedLogSink logs,
    IMonologueScheduler monologue,
    IHostApplicationLifetime lifetime,
    ILogger<TuiHost> log,
    ChatView? chat = null) : IHostedService
{
    private Thread? _thread;
    private IApplication? _app;

    public Task StartAsync(CancellationToken ct)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "lane-tui"
        };

        _thread.Start();

        return Task.CompletedTask;
    }

    private void Run()
    {
        try
        {
            using IApplication app = Application.Create();
            _app = app;

            app.Init();

            // Which driver got picked decides how keys are decoded, so it is the first
            // thing worth knowing if input ever misbehaves on one terminal but not another.
            log.LogInformation("Dashboard driver: {Driver}", app.Driver?.GetType().Name ?? "(none)");

            DashboardView dashboard = new(bus, sessions, models, logs, monologue, chat);

            // The chat pane claims focus from its own Initialized event, once there is a
            // running application to hold it.
            app.Run(dashboard);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The dashboard failed; falling back to headless");
        }
        finally
        {
            // Closing the dashboard means quitting Lane, not leaving her running invisibly.
            lifetime.StopApplication();
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        try { _app?.RequestStop(); }
        catch (Exception ex) { log.LogDebug(ex, "The dashboard did not stop cleanly"); }

        return Task.CompletedTask;
    }
}
