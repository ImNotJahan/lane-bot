using System.Net.WebSockets;
using Lane.Core.Models;
using Lane.Nodes;
using Lane.Nodes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class NodePoolTests
{
    private static NodeConnection Node(string name, int maxConcurrency = 4, string pool = "p") => new(
        name,
        new NodeHello(name, pool, "m", ModelCapabilities.Tools, maxConcurrency,
            new NodeIdentity(name, NodeProtocol.EcdsaP256Sha256, "")),
        WebSocket.CreateFromStream(Stream.Null, new WebSocketCreationOptions()),
        NullLogger.Instance);

    [Fact]
    public async Task Equally_busy_nodes_take_turns()
    {
        NodePool pool = new();
        NodeConnection a = Node("a"), b = Node("b");
        pool.Add(a);
        pool.Add(b);

        List<string> picked = [];

        for (int i = 0; i < 4; i++)
        {
            using NodeLease lease = await pool.AcquireAsync("p", TimeSpan.FromSeconds(1), CancellationToken.None);
            picked.Add(lease.Node.Id);
        }

        Assert.Equal(["a", "b", "a", "b"], picked);
    }

    [Fact]
    public async Task Least_busy_node_is_preferred()
    {
        NodePool pool = new();
        NodeConnection a = Node("a"), b = Node("b");
        pool.Add(a);
        pool.Add(b);

        using NodeLease first  = await pool.AcquireAsync("p", TimeSpan.FromSeconds(1), CancellationToken.None);
        using NodeLease second = await pool.AcquireAsync("p", TimeSpan.FromSeconds(1), CancellationToken.None);
        NodeLease third        = await pool.AcquireAsync("p", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotEqual(first.Node, second.Node);

        NodeConnection busier = third.Node;
        third.Dispose();

        using NodeLease fourth = await pool.AcquireAsync("p", TimeSpan.FromSeconds(1), CancellationToken.None);
        using NodeLease fifth  = await pool.AcquireAsync("p", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotEqual(fourth.Node, fifth.Node);
        Assert.Contains(busier, new[] { fourth.Node, fifth.Node });
    }

    [Fact]
    public async Task Waits_for_a_free_slot_and_times_out_without_one()
    {
        NodePool pool = new();
        pool.Add(Node("a", maxConcurrency: 1));

        NodeLease held = await pool.AcquireAsync("p", TimeSpan.FromSeconds(1), CancellationToken.None);

        await Assert.ThrowsAsync<NodeUnavailableException>(() =>
            pool.AcquireAsync("p", TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Task<NodeLease> waiting = pool.AcquireAsync("p", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        held.Dispose();

        using NodeLease next = await waiting;
        Assert.Equal("a", next.Node.Id);
    }

    [Fact]
    public async Task A_node_joining_releases_a_waiting_request()
    {
        NodePool pool = new();

        Task<NodeLease> waiting = pool.AcquireAsync("p", TimeSpan.FromSeconds(5), CancellationToken.None);

        pool.Add(Node("late"));

        using NodeLease lease = await waiting;
        Assert.Equal("late", lease.Node.Id);
    }

    [Fact]
    public async Task Removed_nodes_are_not_picked_and_pools_are_separate()
    {
        NodePool pool = new();
        NodeConnection a = Node("a");
        pool.Add(a);
        pool.Add(Node("other", pool: "q"));

        pool.Remove(a);

        await Assert.ThrowsAsync<NodeUnavailableException>(() =>
            pool.AcquireAsync("p", TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.Empty(pool.Snapshot()["p"]);
        Assert.Single(pool.Snapshot()["q"]);
    }

    [Fact]
    public async Task An_empty_pool_falls_back_to_default()
    {
        NodePool pool = new();
        pool.Add(Node("fallback", pool: "default"));

        using NodeLease lease = await pool.AcquireAsync("p", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("fallback", lease.Node.Id);
    }

    [Fact]
    public async Task A_populated_pool_does_not_fall_back_when_its_nodes_are_busy()
    {
        NodePool pool = new();
        pool.Add(Node("own", maxConcurrency: 1));
        pool.Add(Node("fallback", pool: "default"));

        using NodeLease held = await pool.AcquireAsync("p", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal("own", held.Node.Id);

        await Assert.ThrowsAsync<NodeUnavailableException>(() =>
            pool.AcquireAsync("p", TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }
}
