using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Lane.Surfaces.Api.Streaming;

/// <summary>
/// Server-sent events, written one at a time.
///
/// The lock is not optional: a stream has two independent writers — the turn's own events
/// and the keep-alive timer — and two interleaved half-frames are unparseable to the client
/// rather than merely out of order.
/// </summary>
public sealed class SseWriter(HttpResponse response) : IAsyncDisposable
{
    private static readonly byte[] KeepAlive = ": keep-alive\n\n"u8.ToArray();

    private readonly SemaphoreSlim _gate = new(1, 1);

    public static void PrepareHeaders(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        // Proxies that buffer will hold a whole reply back and deliver it in one piece,
        // which defeats the point of streaming it.
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public async Task SendAsync<T>(string name, T payload, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload, ApiJson.Options);

        StringBuilder frame = new();

        frame.Append("event: ").Append(name).Append('\n');

        // A payload containing a newline would otherwise end the frame early and leave the
        // rest of it looking like a new event.
        foreach (string line in json.Split('\n')) frame.Append("data: ").Append(line).Append('\n');

        frame.Append('\n');

        await WriteAsync(Encoding.UTF8.GetBytes(frame.ToString()), ct).ConfigureAwait(false);
    }

    public Task KeepAliveAsync(CancellationToken ct) => WriteAsync(KeepAlive, ct);

    private async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
            await response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();

        return ValueTask.CompletedTask;
    }
}
