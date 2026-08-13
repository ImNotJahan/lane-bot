using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Host;
using Lane.Host.Logging;
using Lane.Host.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Secrets come from .env in development. Loaded before configuration binds so that
// "env:NAME" references resolve.
DotNetEnv.Env.TraversePath().Load();

bool migrating = args.Contains("migrate");

// The dashboard owns the terminal when it runs, so the choice has to be made before
// anything decides where log output goes.
bool headless = migrating || args.Contains("--no-tui")
                || Console.IsInputRedirected || Console.IsOutputRedirected;

BufferedLogSink logs = new();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    // Anchored to the assembly directory, not the shell's cwd, so `dotnet run` from the
    // repo root and a published binary behave the same.
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("LANE_")
    .AddCommandLine(args);

builder.Logging.ClearProviders();

// Every log line goes to the buffer that feeds the dashboard's tail pane, whether or not
// the dashboard is running — it costs nothing and means the pane opens with history.
builder.Logging.AddProvider(new BufferedLoggerProvider(logs));

// Console output is only safe when Terminal.Gui is not drawing over it. Even then it goes
// to stderr, so a piped conversation on stdout stays clean.
if (headless) builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.AddLane(useDashboard: !headless && !migrating, logs);

builder.Services.AddSingleton<V2Migrator>();

IHost host = builder.Build();

if (migrating)
{
    await RunMigrationAsync(host, args);
    return;
}

host.ValidateLane();

await host.RunAsync();

// ---------------------------------------------------------------------------

static async Task RunMigrationAsync(IHost host, string[] args)
{
    string? data = Argument(args, "--data") ?? DefaultDataPath();

    if (data is null || !File.Exists(data))
    {
        Console.Error.WriteLine(
            "Could not find v2's data.json. Pass --data <path>.\n" +
            "Usage: lane migrate --data <path> [--session <id>] [--dry-run]");

        Environment.ExitCode = 1;
        return;
    }

    string sessionText = Argument(args, "--session") ?? "terminal/Text/local";

    if (!SessionId.TryParse(sessionText, out SessionId? session))
    {
        Console.Error.WriteLine($"'{sessionText}' is not a session id, e.g. discord.main/Text/123456.");

        Environment.ExitCode = 1;
        return;
    }

    bool dryRun = args.Contains("--dry-run");

    V2Migrator migrator = host.Services.GetRequiredService<V2Migrator>();

    try
    {
        MigrationReport report = await migrator.MigrateAsync(data, session, dryRun, CancellationToken.None);

        Console.WriteLine($"{(dryRun ? "Would migrate" : "Migrated")} into {session}: {report}");

        if (dryRun) Console.WriteLine("Nothing was written. Run again without --dry-run to commit.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Migration failed: {ex.Message}");

        Environment.ExitCode = 1;
    }
}

static string? Argument(string[] args, string name)
{
    int index = Array.IndexOf(args, name);

    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>Where v2 wrote its data, if the legacy project is still beside us.</summary>
static string? DefaultDataPath()
{
    string candidate = Path.Combine(AppContext.BaseDirectory, "data.json");

    return File.Exists(candidate) ? candidate : null;
}
