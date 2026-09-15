using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lane.Nodes.Portal;
using Lane.Nodes.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Nodes;

/// <summary>
/// Accepts node WebSockets at <see cref="NodeProtocol.ConnectPath"/>, lists the pools at <c>GET /v1/nodes</c>, and serves
/// <paramref name="portal"/> at the root. Unauthenticated.
/// </summary>
public sealed class NodeListener(
    NodePool        pool,
    NodesOptions    options,
    ILoggerFactory  loggers,
    NodeBookkeeper? bookkeeper = null,
    NodePortal?     portal     = null)
    : IHostedService, IAsyncDisposable
{
    private readonly ILogger<NodeListener>   _log      = loggers.CreateLogger<NodeListener>();
    private readonly CancellationTokenSource _stopping = new();

    private WebApplication? _app;

    /// <summary>Where it actually listens; differs from the configured URL when the port is 0.</summary>
    public IReadOnlyList<string> Addresses => _app is null ? [] : [.. _app.Urls];

    public async Task StartAsync(CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggers);

        builder.WebHost.UseUrls(options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver()));

        _app = builder.Build();

        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        _app.MapGet("/v1/nodes", () => Results.Json(pool.Snapshot(), NodeProtocol.Json));
        _app.Map(NodeProtocol.ConnectPath, AcceptAsync);

        portal?.Map(_app);

        await _app.StartAsync(ct).ConfigureAwait(false);

        _log.LogInformation("Node listener on {Urls}", string.Join(", ", _app.Urls));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_app is not null) await _app.StopAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync().ConfigureAwait(false);

        _stopping.Dispose();
    }

    private async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        NodeHello? hello = await ReceiveHelloAsync(socket).ConfigureAwait(false);

        if (hello is null || hello.ProtocolVersion != NodeProtocol.Version)
        {
            _log.LogWarning("Connection from {Remote} sent no usable hello (protocol {Version}, expected {Expected})",
                context.Connection.RemoteIpAddress, hello?.ProtocolVersion, NodeProtocol.Version);

            await TryCloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "expected a hello").ConfigureAwait(false);
            return;
        }

        NodeConnection node = new(Guid.NewGuid().ToString("n"), hello, socket, loggers.CreateLogger<NodeConnection>());

        try
        {
            await node.WriteAsync(new NodeWelcome(node.Id), _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            return;
        }

        if (bookkeeper is not null) await bookkeeper.JoinedAsync(hello).ConfigureAwait(false);

        pool.Add(node);

        try
        {
            await node.RunAsync(_stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            pool.Remove(node);
            await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "closing").ConfigureAwait(false);
        }
    }

    private async Task<NodeHello?> ReceiveHelloAsync(WebSocket socket)
    {
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        wait.CancelAfter(options.HelloTimeout);

        try
        {
            return await NodeProtocol.ReceiveAsync(socket, wait.Token).ConfigureAwait(false) as NodeHello;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException)
        {
            return null;
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;

        using CancellationTokenSource wait = new(TimeSpan.FromSeconds(2));

        try
        {
            await socket.CloseOutputAsync(status, reason, wait.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}
