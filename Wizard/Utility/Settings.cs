namespace Wizard.Utility
{
    public sealed class Settings
    {
        public static Settings? instance;

        public required HandlerSettings[] MemoryHandlers        { get; set; }
        public required ulong             DefaultDiscordChannel { get; set; }
        public          bool              ExclusiveToChannel    { get; set; }
        public required TimezoneSettings  TimezoneSettings      { get; set; }
        public required string            LoggingLevel          { get; set; }
        public required int               RespondToThought      { get; set; }
    }

    public sealed class HandlerSettings
    {
        public required string                  Handler { get; set; }
        public required Dictionary<string, int> Args    { get; set; }
    }

    public sealed class TimezoneSettings
    {
        public required int HourShift   { get; set; }
        public required int MinuteShift { get; set; }
    }
}