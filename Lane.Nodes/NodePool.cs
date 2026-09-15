using Lane.Core.Events;
using Lane.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lane.Nodes;

public sealed record NodeSummary(
    string            ConnectionId,
    string            Name,
    string            Model,
    string            KeyId,
    ModelCapabilities Capabilities,
    int               InFlight,
    int               MaxConcurrency,
    DateTimeOffset    ConnectedAt);

/// <summary>Every connected node, grouped by the pool each one named in its hello.</summary>
public sealed class NodePool(IEventBus? bus = null, ILogger<NodePool>? log = null)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<NodeConnection>> _pools    = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int>                  _cursors  = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelCapabilities>    _expected = new(StringComparer.OrdinalIgnoreCase);

    private TaskCompletionSource _changed = NewSignal();

    /// <summary>Records the capabilities a model instance promises for <paramref name="pool"/>; nodes that
    /// join without them are logged but still admitted.</summary>
    public void Expect(string pool, ModelCapabilities capabilities)
    {
        lock (_gate) _expected[pool] = _expected.GetValueOrDefault(pool) | capabilities;
    }

    public void Add(NodeConnection node)
    {
        string pool = node.Hello.Pool;
        ModelCapabilities expected;

        lock (_gate)
        {
            if (!_pools.TryGetValue(pool, out List<NodeConnection>? members))
                _pools[pool] = members = [];

            members.Add(node);
            expected = _expected.GetValueOrDefault(pool);

            Signal();
        }

        log?.LogInformation("Node {Node} joined pool '{Pool}' serving {Model} (max {Max} concurrent)",
            node, pool, node.Hello.Model, node.MaxConcurrency);

        ModelCapabilities missing = expected & ~node.Hello.Capabilities;

        if (missing != ModelCapabilities.None)
            log?.LogWarning("Node {Node} in pool '{Pool}' does not declare {Missing}, which the pool's model promises",
                node, pool, missing);

        bus?.Publish(new NodeJoined(pool, node.Id, node.Hello.Name, node.Hello.Identity.KeyId));
    }

    public void Remove(NodeConnection node)
    {
        bool removed;

        lock (_gate)
        {
            removed = _pools.TryGetValue(node.Hello.Pool, out List<NodeConnection>? members) && members.Remove(node);

            if (removed) Signal();
        }

        if (!removed) return;

        log?.LogInformation("Node {Node} left pool '{Pool}'", node, node.Hello.Pool);

        bus?.Publish(new NodeLeft(node.Hello.Pool, node.Id, node.Hello.Name, node.Hello.Identity.KeyId));
    }

    public IReadOnlyDictionary<string, IReadOnlyList<NodeSummary>> Snapshot()
    {
        lock (_gate)
        {
            return _pools.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<NodeSummary>)[.. kv.Value.Select(n => new NodeSummary(
                    n.Id, n.Hello.Name, n.Hello.Model, n.Hello.Identity.KeyId, n.Hello.Capabilities,
                    n.InFlight, n.MaxConcurrency, n.ConnectedAt))],
                StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Reserves a slot on the least-busy node in <paramref name="pool"/>, rotating between equally busy
    /// nodes. Waits up to <paramref name="timeout"/> for one to become free.
    /// </summary>
    /// <exception cref="NodeUnavailableException">No slot came free in time.</exception>
    public async Task<NodeLease> AcquireAsync(string pool, TimeSpan timeout, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        while (true)
        {
            Task changed;

            lock (_gate)
            {
                if (TryReserve(pool) is { } node) return new NodeLease(this, node);

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new NodeUnavailableException(pool, timeout);
            }
        }
    }

    internal void Release(NodeConnection node)
    {
        lock (_gate)
        {
            node.InFlight--;
            Signal();
        }
    }

    private NodeConnection? TryReserve(string pool)
    {
        if (!_pools.TryGetValue(pool, out List<NodeConnection>? members) || members.Count == 0) return null;

        int cursor = _cursors.GetValueOrDefault(pool);

        NodeConnection? best = null;
        int bestIndex = -1;

        for (int offset = 0; offset < members.Count; offset++)
        {
            int index = (cursor + offset) % members.Count;
            NodeConnection candidate = members[index];

            if (candidate.InFlight >= candidate.MaxConcurrency) continue;

            if (best is null || candidate.InFlight < best.InFlight)
            {
                best      = candidate;
                bestIndex = index;
            }
        }

        if (best is null) return null;

        best.InFlight++;
        _cursors[pool] = bestIndex + 1;

        return best;
    }

    private void Signal()
    {
        TaskCompletionSource previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class NodeLease : IDisposable
{
    private NodePool? _pool;

    internal NodeLease(NodePool pool, NodeConnection node)
    {
        _pool = pool;
        Node  = node;
    }

    public NodeConnection Node { get; }

    public void Dispose() => Interlocked.Exchange(ref _pool, null)?.Release(Node);
}
