using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Lane.Core.Events;
using Lane.Core.Presence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Host.Presence;

public sealed class FaceOptions
{
    public bool Enabled { get; set; }

    public int Port { get; set; } = 5050;

    public string DefaultEmoticon { get; set; } = "( ._.)";
}

/// <summary>
/// Lane's face, served over HTTP and pushed to browsers as it changes.
///
/// Ported from v2, with one structural difference: it subscribes to the event bus instead
/// of being handed a C# event by the orchestrator. In v2 exactly one listener could exist,
/// so the face and the dashboard could not both know her expression. Here anything that
/// cares subscribes, and this server is just one of them.
/// </summary>
public sealed class FaceServer(
    IEventBus bus,
    IOptions<FaceOptions> options,
    ILogger<FaceServer> log) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Stream> _clients = new();
    private readonly FaceOptions _options = options.Value;

    private string _emoticon = options.Value.DefaultEmoticon;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        using HttpListener listener = new();
        listener.Prefixes.Add($"http://localhost:{_options.Port}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            // A port already in use should cost the face, not the process.
            log.LogError(ex, "Face server could not listen on port {Port}", _options.Port);
            return;
        }

        log.LogInformation("Face at http://localhost:{Port}/", _options.Port);

        using IDisposable subscription = bus.Subscribe<PresenceChanged>(change =>
        {
            _emoticon = change.Emoticon;
            _ = BroadcastAsync(change.Emoticon);
        });

        using CancellationTokenRegistration stop = stoppingToken.Register(listener.Close);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Face server dropped a connection");
                continue;
            }

            _ = HandleAsync(context, stoppingToken);
        }

        foreach (Stream client in _clients.Values) client.Dispose();

        _clients.Clear();
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            if (context.Request.Url?.AbsolutePath == "/events")
            {
                await StreamEventsAsync(context, ct).ConfigureAwait(false);
                return;
            }

            byte[] page = Encoding.UTF8.GetBytes(FacePage.Html(_emoticon));

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = page.Length;

            await context.Response.OutputStream.WriteAsync(page, ct).ConfigureAwait(false);

            context.Response.Close();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Face server failed to serve a request");
        }
    }

    private async Task StreamEventsAsync(HttpListenerContext context, CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.Add("Cache-Control", "no-cache");
        context.Response.SendChunked = true;

        Guid id = Guid.NewGuid();
        Stream stream = context.Response.OutputStream;

        _clients[id] = stream;

        try
        {
            await WriteAsync(stream, _emoticon, ct).ConfigureAwait(false);

            // A comment every so often, or an idle proxy will close the connection.
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

                await stream.WriteAsync(": ping\n\n"u8.ToArray(), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The browser went away. Nothing to report.
        }
        finally
        {
            _clients.TryRemove(id, out _);

            try { stream.Dispose(); } catch { /* already gone */ }
        }
    }

    private async Task BroadcastAsync(string emoticon)
    {
        foreach ((Guid id, Stream stream) in _clients)
        {
            try
            {
                await WriteAsync(stream, emoticon, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _clients.TryRemove(id, out _);
            }
        }
    }

    private static async Task WriteAsync(Stream stream, string emoticon, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes($"data: {emoticon}\n\n");

        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
