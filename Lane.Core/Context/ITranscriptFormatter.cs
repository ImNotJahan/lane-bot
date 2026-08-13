using System.Text;
using Lane.Core.Messages;

namespace Lane.Core.Context;

public sealed record TranscriptFormatOptions(
    TimeSpan DisplayOffset,
    bool     IncludeRelativeTime = true,
    bool     IncludeSessionLabels = false)
{
    public static TranscriptFormatOptions Default { get; } = new(TimeSpan.Zero);
}

/// <summary>
/// Flattens messages into prompt text. Used only for memory that goes into a *text* block;
/// inline conversation turns keep their structure and never come through here.
/// </summary>
public interface ITranscriptFormatter
{
    string Format(IEnumerable<LaneMessage> messages, TranscriptFormatOptions options);
    string FormatTime(DateTimeOffset time, TranscriptFormatOptions options);
}

public sealed class TranscriptFormatter : ITranscriptFormatter
{
    private readonly TimeProvider _time;

    public TranscriptFormatter(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public string Format(IEnumerable<LaneMessage> messages, TranscriptFormatOptions options)
    {
        StringBuilder sb = new();

        foreach (LaneMessage message in messages)
        {
            string text = message.TextContent;
            if (string.IsNullOrWhiteSpace(text)) continue;

            sb.Append('[').Append(FormatTime(message.Timestamp, options));

            // Session labels are what let the monologue read across conversations
            // without confusing who said what where.
            if (options.IncludeSessionLabels && message.Session is not null)
                sb.Append(' ').Append(message.Session.Value);

            sb.Append("] ");

            sb.Append(message.Kind == MessageKind.Thought
                ? $"{message.Author.DisplayName} thinks: "
                : $"{message.Author.DisplayName}: ");

            sb.AppendLine(text).AppendLine();
        }

        return sb.ToString();
    }

    public string FormatTime(DateTimeOffset time, TranscriptFormatOptions options)
    {
        string stamp = time.ToOffset(options.DisplayOffset).ToString("yyyy/MM/dd HH:mm:ss");

        return options.IncludeRelativeTime ? $"{stamp} ({Relative(_time.GetUtcNow() - time)})" : stamp;
    }

    private static string Relative(TimeSpan since) => since switch
    {
        { TotalMinutes: < 1 }  => "just now",
        { TotalHours:   < 1 }  => $"{since.Minutes} minutes ago",
        { TotalDays:    < 1 }  => $"{since.Hours} hours ago",
        { TotalDays:    < 2 }  => "yesterday",
        { TotalDays:    < 7 }  => $"{since.Days} days ago",
        _                      => $"{since.Days} days ago"
    };
}
