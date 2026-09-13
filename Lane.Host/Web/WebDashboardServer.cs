using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Lane.Core.Energy;
using Lane.Core.Events;
using Lane.Core.Models;
using Lane.Core.Monologue;
using Lane.Core.Sessions;
using Lane.Host.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Host.Web;

public sealed class DashboardOptions
{
    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = 5090;

    /// <summary>The file the config editor reads and writes. Defaults beside the executable.</summary>
    public string ConfigPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
}

/// <summary>
/// A dashboard served over HTTP, replacing the old Terminal.Gui pane layout.
///
/// It never touches the process's console — the terminal surface goes back to plain
/// <c>Console.In</c>/<c>Console.Out</c> now that nothing else claims the screen. The browser
/// polls <c>/api/snapshot</c> rather than holding a streaming connection open, which keeps
/// this server as simple as <see cref="Lane.Host.Presence.FaceServer"/> beside it. Loopback
/// only, and unauthenticated for the same reason the face server is: this speaks for Lane's
/// own configuration, and it is not meant to be reachable from anywhere but the machine
/// running her.
/// </summary>
public sealed class WebDashboardServer(
    IEventBus bus,
    ISessionRegistry sessions,
    ILanguageModelRegistry models,
    BufferedLogSink logs,
    IMonologueScheduler monologue,
    IEnergyService energy,
    IOptions<DashboardOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<WebDashboardServer> log) : BackgroundService
{
    private readonly DashboardOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, string> _sessionNotes = new();
    private readonly ConcurrentDictionary<string, ModelUsage> _usage = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        foreach (ILanguageModel model in models.All)
            _usage[model.Descriptor.InstanceId] = new ModelUsage(model.Descriptor.InstanceId, RolesFor(model.Descriptor.InstanceId));

        using IDisposable s1 = bus.Subscribe<TokenUsageEvent>(RecordUsage);
        using IDisposable s2 = bus.Subscribe<TurnStarted>(evt => _sessionNotes[evt.Session.Value] = "running");
        using IDisposable s3 = bus.Subscribe<TurnCompleted>(evt => _sessionNotes[evt.Session.Value] = "idle");
        using IDisposable s4 = bus.Subscribe<TurnFailed>(evt => _sessionNotes[evt.Session.Value] = "failed");
        using IDisposable s5 = bus.Subscribe<ToolInvokedEvent>(evt =>
        {
            if (evt.Session is not null) _sessionNotes[evt.Session] = $"tool: {evt.Tool}";
        });

        using HttpListener listener = new();
        listener.Prefixes.Add($"http://localhost:{_options.Port}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Dashboard could not listen on port {Port}", _options.Port);
            return;
        }

        log.LogInformation("Dashboard at http://localhost:{Port}/", _options.Port);

        using CancellationTokenRegistration stop = stoppingToken.Register(listener.Close);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Dashboard dropped a connection");
                continue;
            }

            _ = HandleAsync(context, stoppingToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            string path   = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;

            switch (path)
            {
                case "/api/snapshot" when method == "GET":
                    await WriteJsonAsync(context, BuildSnapshot(), ct).ConfigureAwait(false);
                    break;

                case "/api/config" when method == "GET":
                    await GetConfigAsync(context, ct).ConfigureAwait(false);
                    break;

                case "/api/config" when method == "POST":
                    await SaveConfigAsync(context, ct).ConfigureAwait(false);
                    break;

                case "/api/restart" when method == "POST":
                    await WriteJsonAsync(context, new { restarting = true }, ct).ConfigureAwait(false);
                    Restart();
                    break;

                case "/config":
                    await WriteHtmlAsync(context, ConfigPage.Html, ct).ConfigureAwait(false);
                    break;

                default:
                    await WriteHtmlAsync(context, DashboardPage.Html, ct).ConfigureAwait(false);
                    break;
            }

            context.Response.Close();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Dashboard failed to serve a request");
        }
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext context, T value, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = json.Length;

        await context.Response.OutputStream.WriteAsync(json, ct).ConfigureAwait(false);
    }

    private static async Task WriteHtmlAsync(HttpListenerContext context, string html, CancellationToken ct)
    {
        byte[] page = Encoding.UTF8.GetBytes(html);

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;

        await context.Response.OutputStream.WriteAsync(page, ct).ConfigureAwait(false);
    }

    private async Task GetConfigAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!File.Exists(_options.ConfigPath))
        {
            context.Response.StatusCode = 404;
            await WriteJsonAsync(context, new { error = $"'{_options.ConfigPath}' does not exist." }, ct)
                .ConfigureAwait(false);
            return;
        }

        string content = await File.ReadAllTextAsync(_options.ConfigPath, ct).ConfigureAwait(false);

        await WriteJsonAsync(context, new { path = _options.ConfigPath, content }, ct).ConfigureAwait(false);
    }

    private async Task SaveConfigAsync(HttpListenerContext context, CancellationToken ct)
    {
        using StreamReader reader = new(context.Request.InputStream, context.Request.ContentEncoding);
        string body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        // Caught rather than left to throw: a bad edit should tell the person in the
        // browser what is wrong, not take the dashboard's request thread down with it.
        try
        {
            using JsonDocument _ = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context, new { error = $"Not valid JSON: {ex.Message}" }, ct).ConfigureAwait(false);
            return;
        }

        await File.WriteAllTextAsync(_options.ConfigPath, body, ct).ConfigureAwait(false);

        log.LogInformation("Configuration saved to {Path} from the dashboard", _options.ConfigPath);

        await WriteJsonAsync(context, new { saved = true }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Config changes only take effect at startup — nothing here re-runs the composition
    /// root — so "reload" means relaunching the whole process. Relaunched with the same
    /// executable and arguments it was started with, then this instance shuts down; whatever
    /// started it (a shell, a service manager) sees an ordinary exit and the new process
    /// take its place.
    /// </summary>
    private void Restart()
    {
        _ = Task.Run(async () =>
        {
            // Long enough for the response above to reach the browser before the listener
            // that would carry it goes away.
            await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);

            try
            {
                string? exe = Environment.ProcessPath;

                if (!string.IsNullOrEmpty(exe))
                {
                    string[] cliArgs = Environment.GetCommandLineArgs();

                    // Framework-dependent runs start as "dotnet lane.dll ...", where
                    // ProcessPath is dotnet itself and the dll is the first command-line
                    // argument; a published apphost's ProcessPath already is the same
                    // binary, so command-line argument zero is dropped instead.
                    bool viaDotnet = string.Equals(
                        Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase);

                    ProcessStartInfo psi = new(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };

                    foreach (string arg in viaDotnet ? cliArgs : cliArgs.Skip(1)) psi.ArgumentList.Add(arg);

                    Process.Start(psi);
                }
                else
                {
                    log.LogWarning("Could not determine the current executable; not relaunching after restart");
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to relaunch after a dashboard restart request");
            }
            finally
            {
                lifetime.StopApplication();
            }
        });
    }

    private void RecordUsage(TokenUsageEvent evt)
    {
        ModelUsage usage = _usage.GetOrAdd(evt.ModelInstanceId, id => new ModelUsage(id, RolesFor(id)));

        usage.Record(evt.Usage);
    }

    private string RolesFor(string instance) => string.Join(",", models.RoleBindings
        .Where(kv => string.Equals(kv.Value, instance, StringComparison.OrdinalIgnoreCase))
        .Select(kv => kv.Key));

    private DashboardSnapshot BuildSnapshot()
    {
        Session[] active = [.. sessions.Active.OrderBy(s => s.Id.Value, StringComparer.Ordinal)];

        SessionRow[] sessionRows = [.. active.Select(s => new SessionRow(
            s.Id.Value,
            s.Descriptor.DisplayName,
            s.State.ToString(),
            _sessionNotes.GetValueOrDefault(s.Id.Value, "-"),
            s.LastActivity))];

        ModelRow[] modelRows = [.. _usage.Values.Select(u => u.ToRow())];

        EnergyState energyState = energy.Current;

        EnergySnapshot energySnapshot = new(
            energyState.Fraction,
            energyState.Remaining,
            energyState.Budget,
            energyState.Asleep,
            energyState.Asleep ? energy.TimeUntilRested?.TotalSeconds : null,
            energyState.Tier.ToString());

        MonologueStatus status = monologue.Status;

        MonologueSnapshot monologueSnapshot = new(
            status.NextThoughtAt,
            status.LastThought,
            status.ThoughtCount,
            status.Thinking);

        string[] logLines = [.. logs.Snapshot().TakeLast(300).Select(e => e.Short)];

        return new DashboardSnapshot(sessionRows, modelRows, energySnapshot, monologueSnapshot, logLines);
    }

    private sealed class ModelUsage(string instance, string roles)
    {
        private readonly Lock _gate = new();

        private int _calls;
        private long _input;
        private long _output;
        private long _cacheRead;
        private long _cacheWrite;
        private TimeSpan _lastLatency;

        public void Record(TokenUsage usage)
        {
            lock (_gate)
            {
                _calls++;
                _input       += usage.Input;
                _output      += usage.Output;
                _cacheRead   += usage.CacheRead;
                _cacheWrite  += usage.CacheWrite;
                _lastLatency  = usage.Latency;
            }
        }

        public ModelRow ToRow()
        {
            lock (_gate)
            {
                string cacheRate = _input == 0 ? "-" : $"{100 * _cacheRead / _input}%";

                return new ModelRow(instance, roles, _calls, _input, _output, _cacheRead, cacheRate,
                    _lastLatency == TimeSpan.Zero ? null : (int)_lastLatency.TotalMilliseconds);
            }
        }
    }
}

public sealed record SessionRow(string Id, string Name, string State, string Activity, DateTimeOffset LastActivity);

public sealed record ModelRow(
    string Instance, string Roles, int Calls, long Input, long Output, long CacheRead, string CacheRate, int? LastLatencyMs);

public sealed record EnergySnapshot(
    double Fraction, long Remaining, long Budget, bool Asleep, double? RestedInSeconds, string Tier);

public sealed record MonologueSnapshot(
    DateTimeOffset? NextThoughtAt, string? LastThought, int ThoughtCount, bool Thinking);

public sealed record DashboardSnapshot(
    IReadOnlyList<SessionRow> Sessions,
    IReadOnlyList<ModelRow> Models,
    EnergySnapshot Energy,
    MonologueSnapshot Monologue,
    IReadOnlyList<string> Logs);
