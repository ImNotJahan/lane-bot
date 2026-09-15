using Lane.Core.Events;

namespace Lane.Nodes;

public sealed class NodeUnavailableException(string pool, TimeSpan waited)
    : Exception($"No node in pool '{pool}' had a free slot within {waited}.")
{
    public string Pool { get; } = pool;
}

public sealed class NodeDisconnectedException(string node)
    : Exception($"Node {node} disconnected before answering.");

public sealed class NodeRequestFailedException(string node, string message)
    : Exception($"Node {node} failed the request: {message}");

public sealed class NodeResponseRejectedException(string node, string reason)
    : Exception($"The response from node {node} was rejected: {reason}");

public sealed record NodeJoined(string Pool, string ConnectionId, string Name, string KeyId) : ILaneEvent;

public sealed record NodeLeft(string Pool, string ConnectionId, string Name, string KeyId) : ILaneEvent;
