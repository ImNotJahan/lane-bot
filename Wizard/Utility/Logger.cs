using Karambolo.Extensions.Logging.File;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Wizard.Utility.BufferLogger;

namespace Wizard.Utility
{
    public static class Logger
    {
        static readonly ILogger   logger;
        static readonly BufferLog log;

        static Logger()
        {
            LoggingSettings settings;

            if(Settings.instance is null)
            {
                settings = new()
                {
                    ConsoleLevel = "Warning",
                    FileLevel    = "Debug",
                    FileLogPath  = "lane.log"
                };
            }
            else
            {
                settings = Settings.instance.Logging;
            }

            log = new(200);

            ILoggerFactory factory = LoggerFactory.Create(builder => 
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                
                /*builder.AddConsole()
                       .AddFilter<ConsoleLoggerProvider>(null, StringToLogLevel(settings.ConsoleLevel));*/
                
                builder.AddFile(options =>
                {
                    options.RootPath = AppContext.BaseDirectory;
                    options.Files    = [new LogFileOptions { 
                        Path       = settings.FileLogPath,
                        DateFormat = "yyyyMMdd"
                    }];
                }).AddFilter<FileLoggerProvider>(null, StringToLogLevel(settings.FileLevel));

                builder.AddProvider(new BufferLoggerProvider(log, StringToLogLevel(settings.ConsoleLevel)));
            });

            logger = factory.CreateLogger("Program");
        }

        private static LogLevel StringToLogLevel(string level)
        {
            return level switch
            {
                "Debug"       => LogLevel.Debug,
                "Information" => LogLevel.Information,
                "Warning"     => LogLevel.Warning,
                "Trace"       => LogLevel.Trace,
                "Critical"    => LogLevel.Critical,
                "Error"       => LogLevel.Error,
                "None"        => LogLevel.None,
                _             => throw new Exception("Invalid log level " + level)
            };
        }

        public static void LogInformation(string? message, params object[] args) => logger.LogInformation(message, args);
        public static void LogWarning    (string? message, params object[] args) => logger.LogWarning(message, args);
        public static void LogError      (string? message, params object[] args) => logger.LogError(message, args);
        public static void LogDebug      (string? message, params object[] args) => logger.LogDebug(message, args);

        public static BufferLog Buffer() => log;
    }
}