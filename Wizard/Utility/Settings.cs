namespace Wizard.Utility
{
    public sealed class Settings
    {
        public static Settings? instance;

        public required HandlerSettings[] MemoryHandlers        { get; set; }
        public required ulong             DefaultDiscordChannel { get; set; }
        public          bool              ExclusiveToChannel    { get; set; }
        public required TimezoneSettings  TimezoneSettings      { get; set; }
        public required int               RespondToThought      { get; set; }
        public required LoggingSettings   Logging               { get; set; }
        public required string            Body                  { get; set; }
        public          SpeechSettings?   Speech                { get; set; }
        public          HearingSettings?  Hearing               { get; set; }
        public required string            LLM                   { get; set; }
        public          FaceSettings?     Face                  { get; set; }
        public          BookSettings?     Books                 { get; set; }
    }

    public sealed class HandlerSettings
    {
        public required string                  ID      { get; set; }
        public required string                  Handler { get; set; }
        public required Dictionary<string, int> Args    { get; set; }
    }

    public sealed class TimezoneSettings
    {
        public required int HourShift   { get; set; }
        public required int MinuteShift { get; set; }
    }

    public sealed class LoggingSettings
    {
        public required string ConsoleLevel { get; set; }
        public required string FileLevel    { get; set; }
        public required string FileLogPath  { get; set; }
    }

    public sealed class SpeechSettings
    {
        public required float  Tempo      { get; set; }
        public required float  Pitch      { get; set; }
        public required float  Rate       { get; set; }
        public required string Mouth      { get; set; }
        public required string Voice      { get; set; }
        public required float  Stability  { get; set; }
        public required float  Similarity { get; set; }
        public required bool   Tune       { get; set; }
    }

    public sealed class HearingSettings
    {
        public required string Ear { get; set; }
    }

    public sealed class FaceSettings
    {
        public required bool   Enabled     { get; set; }
        public required int    Port        { get; set; }
        public required string DefaultFace { get; set; }
    }

    public sealed class BookSettings
    {
        public required string[] Available           { get; set; }
        public required int      ReadThoughtInterval { get; set; }
    }
}