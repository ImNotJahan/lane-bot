using Lane.Core.Events;
using Lane.Core.Identity;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Presence;

/// <summary>How Lane is currently showing herself — the emoticon on her face, for now.</summary>
public sealed record PresenceChanged(string Emoticon, SessionId? Session) : ILaneEvent;

/// <summary>
/// Somewhere presence changes go.
///
/// A seam rather than a direct wire to the face server: in v2 the emoticon travelled on a
/// C# event from the orchestrator straight into one HTTP server, which meant exactly one
/// listener could ever exist. The event bus in M3 implements this, and the face server,
/// the dashboard and the API's event stream all subscribe independently.
/// </summary>
public interface IPresenceSink
{
    void Publish(PresenceChanged change);
}

public sealed class LoggingPresenceSink(ILogger<LoggingPresenceSink> log) : IPresenceSink
{
    public void Publish(PresenceChanged change) => log.LogInformation("Presence: {Emoticon}", change.Emoticon);
}

/// <summary>Puts presence on the event bus, where any number of listeners can see it.</summary>
public sealed class EventBusPresenceSink(IEventBus bus) : IPresenceSink
{
    public void Publish(PresenceChanged change) => bus.Publish(change);
}
