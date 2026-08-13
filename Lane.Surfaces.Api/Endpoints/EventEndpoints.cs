using System.Threading.Channels;
using Lane.Core.Events;
using Lane.Core.Presence;
using Lane.Surfaces.Api.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Lane.Surfaces.Api.Endpoints;

/// <summary>
/// Everything happening across the whole harness, as it happens.
///
/// This is the same bus the dashboard and the face server read, and it is why any of them
/// can exist: in v2 the emoticon travelled on a C# event with room for exactly one listener,
/// so a client app watching Lane's expression meant the face server could not. Here a client
/// is just another subscriber, and neither knows the other exists.
/// </summary>
public static class EventEndpoints
{
    public static void Map(IEndpointRouteBuilder app) => app.MapGet("/v1/events", StreamAsync);

    private static async Task StreamAsync(
        HttpContext context, IEventBus bus, string? types, CancellationToken ct)
    {
        ApiClient client = ApiAuth.Require(context);

        HashSet<string>? wanted = string.IsNullOrWhiteSpace(types)
            ? null
            : new HashSet<string>(
                types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

        SseWriter.PrepareHeaders(context.Response);

        await using SseWriter writer = new(context.Response);

        Channel<(string Name, object Payload)> queue = Channel.CreateBounded<(string, object)>(
            new BoundedChannelOptions(512)
            {
                SingleReader = true,

                // A slow reader loses the oldest events rather than applying backpressure to
                // the bus, which would put a client's socket in front of Lane taking a turn.
                FullMode = BoundedChannelFullMode.DropOldest
            });

        void Emit(string name, object payload)
        {
            if (wanted is not null && !wanted.Contains(name)) return;

            queue.Writer.TryWrite((name, payload));
        }

        List<IDisposable> subscriptions =
        [
            bus.Subscribe<TurnStarted>(e => Emit("turn.started",
                new { session = e.Session.Value, kind = e.Kind.ToString(), incoming = e.IncomingCount })),

            bus.Subscribe<TurnCompleted>(e => Emit("turn.completed", new
            {
                session = e.Session.Value, kind = e.Kind.ToString(),
                suppressed = e.Suppressed, toolCalls = e.ToolCalls, durationMs = e.Duration.TotalMilliseconds
            })),

            bus.Subscribe<TurnFailed>(e => Emit("turn.failed", new { session = e.Session.Value, error = e.Error })),

            bus.Subscribe<SessionEvent>(e => Emit("session",
                new { session = e.Session.Value, kind = e.Kind.ToString(), detail = e.Detail })),

            bus.Subscribe<ToolInvokedEvent>(e => Emit("tool.invoked", new { tool = e.Tool, session = e.Session })),

            bus.Subscribe<ToolCompletedEvent>(e => Emit("tool.completed",
                new { tool = e.Tool, isError = e.IsError, durationMs = e.Duration.TotalMilliseconds })),

            bus.Subscribe<PresenceChanged>(e => Emit("presence", new { emoticon = e.Emoticon })),

            bus.Subscribe<SurfaceStateChanged>(e => Emit("surface",
                new { surface = e.Surface.Value, connected = e.Connected, detail = e.Detail }))
        ];

        // Token usage is a cost signal, not conversation content, and it names model
        // instances. Only a client already trusted to see every conversation gets it.
        if (client.CanObserveAllSessions)
            subscriptions.Add(bus.Subscribe<TokenUsageEvent>(e => Emit("tokens", new
            {
                model = e.ModelInstanceId, role = e.Role,
                input = e.Usage.Input, output = e.Usage.Output,
                cacheRead = e.Usage.CacheRead, cacheWrite = e.Usage.CacheWrite
            })));

        try
        {
            await writer.SendAsync("ready", new { surface = "api" }, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                (string Name, object Payload) next;

                try
                {
                    next = await queue.Reader.ReadAsync(ct).AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await writer.KeepAliveAsync(ct).ConfigureAwait(false);
                    continue;
                }

                await writer.SendAsync(next.Name, next.Payload, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away, which is the only way this stream ever ends.
        }
        finally
        {
            foreach (IDisposable subscription in subscriptions) subscription.Dispose();
        }
    }
}
