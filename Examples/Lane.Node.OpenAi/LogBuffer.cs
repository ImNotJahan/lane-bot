using System.Collections.Concurrent;

namespace Lane.Node.OpenAi;

/// <summary>Keeps the most recent log lines for the page.</summary>
public sealed class LogBuffer : ILoggerProvider
{
    private const int Capacity = 300;

    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose()
    {
    }

    private void Add(string line)
    {
        _lines.Enqueue(line);

        while (_lines.Count > Capacity) _lines.TryDequeue(out _);
    }

    private sealed class Logger(LogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string level = logLevel switch
            {
                LogLevel.Information => "info",
                LogLevel.Warning     => "warn",
                LogLevel.Error       => "fail",
                _                    => "crit"
            };

            buffer.Add($"{DateTime.Now:HH:mm:ss} {level} {category}: {formatter(state, exception)}");
        }
    }
}
