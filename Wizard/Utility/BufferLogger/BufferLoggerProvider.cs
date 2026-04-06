using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Wizard.Utility.BufferLogger
{
    [ProviderAlias("BufferLogger")]
    public sealed class BufferLoggerProvider(BufferLog log, LogLevel minLevel) : ILoggerProvider
    {
        readonly ConcurrentDictionary<string, BufferLogger> loggers = new(StringComparer.OrdinalIgnoreCase);

        public ILogger CreateLogger(string name) => loggers.GetOrAdd(name, new BufferLogger(log, minLevel));

        public void Dispose()
        {
            loggers.Clear();
        }
    }
}