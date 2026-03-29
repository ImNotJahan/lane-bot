using Microsoft.Extensions.Logging;

namespace Wizard.Utility
{
    public static class Logger
    {
        const LogLevel MinimumLevel = LogLevel.Information;

        static readonly ILogger logger;

        static Logger()
        {
            using ILoggerFactory factory = LoggerFactory.Create(builder => 
            {
                builder.AddConsole();
                builder.SetMinimumLevel(MinimumLevel);
            });
            
            logger = factory.CreateLogger("Program");
        }

        public static void LogInformation(string? message, params object[] args) => logger.LogInformation(message, args);
        public static void LogWarning    (string? message, params object[] args) => logger.LogWarning(message, args);
        public static void LogError      (string? message, params object[] args) => logger.LogError(message, args);
        public static void LogDebug      (string? message, params object[] args) => logger.LogDebug(message, args);
    }
}