using Microsoft.Extensions.Logging;

namespace Wizard.Utility.BufferLogger
{
    public sealed class BufferLogger(BufferLog buffer, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public void Log<TState>(
            LogLevel                         logLevel,
            EventId                          eventId,
            TState                           state,
            Exception?                       exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if(logLevel < minLevel) return;
            
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string prefix    = logLevel switch
            {
                LogLevel.Warning  => "WARN ",
                LogLevel.Error    => "ERR  ",
                LogLevel.Critical => "CRIT ",
                LogLevel.Debug    => "DBG  ",
                _                 => "INFO "
            };
            
            buffer.Add($"{timestamp} {prefix} {formatter(state, exception)}");
        }
    }
}