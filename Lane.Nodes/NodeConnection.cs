using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Lane.Core.Models;
using Lane.Nodes.Protocol;
using Microsoft.Extensions.Logging;

namespace Lane.Nodes;

public sealed class NodeConnection
{
    private readonly WebSocket     _socket;
    private readonly ILogger       _log;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NodeReply>> _pending = new();

    private volatile bool _closed;

    internal NodeConnection(string id, NodeHello hello, WebSocket socket, ILogger log)
    {
        Id      = id;
        Hello   = hello;
        _socket = socket;
        _log    = log;
    }

    public string Id { get; }

    public NodeHello Hello { get; }

    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public int MaxConcurrency => Math.Max(1, Hello.MaxConcurrency);

    /// <summary>Guarded by the owning <see cref="NodePool"/>'s lock.</summary>
    internal int InFlight { get; set; }

    public async Task<NodeReply> SendAsync(ModelRequest request, CancellationToken ct)
    {
        string requestId = Guid.NewGuid().ToString("n");

        TaskCompletionSource<NodeReply> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = reply;

        try
        {
            if (_closed) throw new NodeDisconnectedException(ToString());

            await WriteAsync(new NodeRequest(requestId, request), ct).ConfigureAwait(false);

            return await reply.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryWriteAsync(new NodeCancel(requestId)).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new NodeDisconnectedException(ToString());
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>Cancelling only abandons the wait for the send lock; a send in progress always completes,
    /// since cancelling a WebSocket send aborts the socket.</summary>
    internal async Task WriteAsync(NodeMessage message, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await NodeProtocol.SendAsync(_socket, message, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task TryWriteAsync(NodeMessage message)
    {
        try
        {
            await WriteAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Reads replies until the socket closes, then fails every request still waiting.</summary>
    internal async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await NodeProtocol.ReceiveAsync(_socket, ct).ConfigureAwait(false) is { } message)
            {
                switch (message)
                {
                    case NodeReply reply when _pending.TryRemove(reply.RequestId, out TaskCompletionSource<NodeReply>? waiting):
                        waiting.TrySetResult(reply);
                        break;

                    case NodeFailure failure when _pending.TryRemove(failure.RequestId, out TaskCompletionSource<NodeReply>? waiting):
                        waiting.TrySetException(new NodeRequestFailedException(ToString(), failure.Message));
                        break;

                    // Answers to requests that were cancelled.
                    case NodeReply or NodeFailure:
                        break;

                    default:
                        _log.LogWarning("Node {Node} sent an unexpected {Type}", this, message.GetType().Name);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Node {Node} sent a malformed message; dropping it", this);
        }
        finally
        {
            _closed = true;

            foreach (string id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out TaskCompletionSource<NodeReply>? waiting))
                    waiting.TrySetException(new NodeDisconnectedException(ToString()));
            }
        }
    }

    public override string ToString() => $"{Hello.Name} ({Hello.Identity.KeyId})";
}
