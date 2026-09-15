using Lane.Core.Models;
using Lane.Nodes.Protocol;

namespace Lane.Nodes;

public readonly record struct NodeValidationResult(bool Accepted, string? Reason = null)
{
    public static NodeValidationResult Accept { get; } = new(true);

    public static NodeValidationResult Reject(string reason) => new(false, reason);
}

/// <summary>Decides whether a node's reply is used. A rejected reply fails the model call.</summary>
public interface INodeResponseValidator
{
    ValueTask<NodeValidationResult> ValidateAsync(
        NodeHello node, ModelRequest request, NodeReply reply, CancellationToken ct);
}

public sealed class AcceptAllNodeValidator : INodeResponseValidator
{
    public ValueTask<NodeValidationResult> ValidateAsync(
        NodeHello node, ModelRequest request, NodeReply reply, CancellationToken ct) =>
        ValueTask.FromResult(NodeValidationResult.Accept);
}
