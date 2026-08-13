using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Surfaces.Api.Streaming;

namespace Lane.Surfaces.Api;

/// <summary>
/// Where Lane's replies go for an API conversation.
///
/// Unlike a Discord channel or a terminal, there is nowhere fixed to write to: the client
/// that asked may have hung up, and several clients may be watching one shared conversation.
/// So the channel publishes into the hub and whoever is listening receives it — including
/// nobody, which is a perfectly ordinary outcome for a conversation held over HTTP.
///
/// One of these lives per session, not per request. It outlives any single connection, which
/// is what lets a client post a message, drop the connection, and read the reply from
/// history afterwards.
/// </summary>
public sealed class ApiSessionChannel(SessionId id, TurnStreamHub hub, ChannelCapabilities capabilities)
    : SessionChannelBase(id, id.Surface, capabilities), ITextOutput
{
    public Task SendAsync(OutboundText text, CancellationToken ct)
    {
        hub.Publish(Id, new TurnStreamEvent.Message(text.Text, text.ReplyToExternalId));

        return Task.CompletedTask;
    }
}
