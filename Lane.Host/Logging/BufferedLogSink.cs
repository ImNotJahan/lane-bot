using Microsoft.Extensions.Logging;

namespace Lane.Host.Logging;

public sealed record LogEntry(DateTimeOffset At, LogLevel Level, string Category, string Message)
{
    public string Short => $"{At.ToLocalTime():HH:mm:ss} {Abbreviate(Level)} {Category}: {Message}";

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace       => "trce",
        LogLevel.Debug       => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning     => "warn",
        LogLevel.Error       => "fail",
        LogLevel.Critical    => "crit",
        _                    => "none"
    };
}

/// <summary>
/// A ring buffer of recent log lines for the dashboard's tail pane.
///
/// The dashboard cannot read the console, because it *is* the console — Terminal.Gui owns
/// the screen, and anything written to stdout by a logger would tear the layout apart.
/// </summary>
public sealed class BufferedLogSink(int capacity = 500)
{
    private readonly Queue<LogEntry> _entries = new();
    private readonly Lock _gate = new();

    public event Action<LogEntry>? EntryAdded;

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > capacity) _entries.Dequeue();
        }

        // Raised outside the lock: a subscriber that logs while handling would otherwise
        // deadlock against itself.
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return [.. _entries];
    }
}

public sealed class BufferedLoggerProvider(BufferedLogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferedLogger(sink, Shorten(categoryName));

    public void Dispose() { }

    /// <summary>"Lane.Core.Sessions.SessionRegistry" reads better as "SessionRegistry" in a narrow pane.</summary>
    private static string Shorten(string category)
    {
        int last = category.LastIndexOf('.');
        return last >= 0 && last < category.Length - 1 ? category[(last + 1)..] : category;
    }

    private sealed class BufferedLogger(BufferedLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);

            if (exception is not null) message += $" — {exception.GetType().Name}: {exception.Message}";

            sink.Add(new LogEntry(DateTimeOffset.UtcNow, level, category, message));
        }
    }
}
