using Microsoft.Extensions.Logging;

namespace Wizard.Utility
{
    public static class Logger
    {
        static readonly ILogger logger;

        static Logger()
        {
            LogLevel minimumLevel = Settings.instance is null ? LogLevel.Information :
                                    Settings.instance.LoggingLevel switch
            {
                "Debug"       => LogLevel.Debug,
                "Information" => LogLevel.Information,
                "Warning"     => LogLevel.Warning,
                "Trace"       => LogLevel.Trace,
                "Critical"    => LogLevel.Critical,
                "Error"       => LogLevel.Error,
                "None"        => LogLevel.None,
                _             => throw new Exception("Invalid log level " + Settings.instance.LoggingLevel)
            };

            using ILoggerFactory factory = LoggerFactory.Create(builder => 
            {
                builder.AddConsole();
                builder.SetMinimumLevel(minimumLevel);
            });
            
            logger = factory.CreateLogger("Program");
        }

        public static void LogInformation(string? message, params object[] args) => logger.LogInformation(message, args);
        public static void LogWarning    (string? message, params object[] args) => logger.LogWarning(message, args);
        public static void LogError      (string? message, params object[] args) => logger.LogError(message, args);
        public static void LogDebug      (string? message, params object[] args) => logger.LogDebug(message, args);
    }
}