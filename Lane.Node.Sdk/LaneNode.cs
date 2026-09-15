using System.Collections.Concurrent;
using System.Net.WebSockets;
using Lane.Core.Models;
using Lane.Nodes.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lane.Node.Sdk;

/// <summary>Connects to Lane as a node and answers its model requests with an <see cref="INodeHandler"/>.</summary>
public sealed class LaneNode
{
    private readonly LaneNodeOptions _options;
    private readonly INodeKey        _key;
    private readonly INodeHandler    _handler;
    private readonly ILogger         _log;

    public LaneNode(LaneNodeOptions options, INodeKey key, INodeHandler handler, ILogger<LaneNode>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LaneUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Pool);

        _options = options;
        _key     = key;
        _handler = handler;
        _log     = (ILogger?)log ?? NullLogger.Instance;
    }

    public LaneNode(
        LaneNodeOptions options,
        INodeKey key,
        Func<ModelRequest, CancellationToken, Task<ModelResponse>> handler,
        ILogger<LaneNode>? log = null)
        : this(options, key, new DelegateNodeHandler(handler), log)
    {
    }

    public NodeIdentity Identity => _key.Identity;

    /// <summary>Raised with the connection id each time Lane accepts this node.</summary>
    public event Action<string>? Connected;

    /// <summary>Raised with the reason each time the connection ends and a reconnect is about to be attempted.</summary>
    public event Action<string>? Disconnected;

    /// <summary>Stays connected to Lane, reconnecting with exponential backoff, until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        TimeSpan delay = _options.ReconnectMin;

        while (!ct.IsCancellationRequested)
        {
            bool welcomed = false;

            try
            {
                await RunSessionAsync(() => welcomed = true, ct).ConfigureAwait(false);

                _log.LogWarning("Lane closed the connection");
                Disconnected?.Invoke("Lane closed the connection");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Connection to Lane failed: {Reason}", ex.Message);
                Disconnected?.Invoke(ex.Message);
            }

            if (welcomed) delay = _options.ReconnectMin;

            _log.LogInformation("Reconnecting in {Delay}", delay);

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ReconnectMax.Ticks));
        }
    }

    private async Task RunSessionAsync(Action onWelcome, CancellationToken ct)
    {
        using ClientWebSocket socket = new();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        await socket.ConnectAsync(ConnectUri(_options.LaneUrl), ct).ConfigureAwait(false);

        await NodeProtocol.SendAsync(socket, new NodeHello(
            _options.Name,
            _options.Pool,
            _options.Model,
            _options.Capabilities,
            _options.MaxConcurrency,
            _key.Identity,
            _key.Delegation), CancellationToken.None).ConfigureAwait(false);

        if (await NodeProtocol.ReceiveAsync(socket, ct).ConfigureAwait(false) is not NodeWelcome welcome)
            throw new InvalidOperationException("Lane did not accept the hello.");

        onWelcome();

        _log.LogInformation("Joined pool '{Pool}' on {Lane} as {KeyId} (connection {Connection})",
            _options.Pool, _options.LaneUrl, _key.Identity.KeyId, welcome.ConnectionId);

        Connected?.Invoke(welcome.ConnectionId);

        using SemaphoreSlim sendLock = new(1, 1);
        using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(ct);

        ConcurrentDictionary<string, CancellationTokenSource> running = new();
        List<Task> answers = [];

        try
        {
            while (await NodeProtocol.ReceiveAsync(socket, ct).ConfigureAwait(false) is { } message)
            {
                switch (message)
                {
                    case NodeRequest request:
                        CancellationTokenSource requestCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                        running[request.RequestId] = requestCts;
                        answers.RemoveAll(t => t.IsCompleted);
                        answers.Add(AnswerAsync(socket, sendLock, request, requestCts, running));
                        break;

                    case NodeCancel cancel when running.TryGetValue(cancel.RequestId, out CancellationTokenSource? target):
                        try { await target.CancelAsync().ConfigureAwait(false); }
                        catch (ObjectDisposedException) { }
                        break;
                }
            }

            if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(answers).ConfigureAwait(false);
        }
    }

    private async Task AnswerAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        NodeRequest request,
        CancellationTokenSource cts,
        ConcurrentDictionary<string, CancellationTokenSource> running)
    {
        NodeMessage answer;

        try
        {
            ModelResponse response = await _handler.HandleAsync(request.Request, cts.Token).ConfigureAwait(false);

            response = response with { Origin = null };

            byte[] signature = _key.Sign(NodeProtocol.SigningPayload(request.RequestId, response));

            answer = new NodeReply(request.RequestId, response, Convert.ToBase64String(signature));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Request {RequestId} failed", request.RequestId);

            answer = new NodeFailure(request.RequestId, ex.Message);
        }
        finally
        {
            running.TryRemove(request.RequestId, out _);
            cts.Dispose();
        }

        await sendLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await NodeProtocol.SendAsync(socket, answer, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            _log.LogWarning("Could not deliver the answer to {RequestId}: {Reason}", request.RequestId, ex.Message);
        }
        finally
        {
            sendLock.Release();
        }
    }

    internal static Uri ConnectUri(string laneUrl)
    {
        UriBuilder builder = new(laneUrl);

        builder.Scheme = builder.Scheme switch
        {
            "http"  => "ws",
            "https" => "wss",
            _       => builder.Scheme
        };

        if (builder.Path is "" or "/") builder.Path = NodeProtocol.ConnectPath;

        return builder.Uri;
    }
}
