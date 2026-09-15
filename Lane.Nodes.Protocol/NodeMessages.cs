using System.Text.Json.Serialization;
using Lane.Core.Models;

namespace Lane.Nodes.Protocol;

/// <summary>One WebSocket text frame between Lane and a node.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NodeHello),   "hello")]
[JsonDerivedType(typeof(NodeWelcome), "welcome")]
[JsonDerivedType(typeof(NodeRequest), "request")]
[JsonDerivedType(typeof(NodeReply),   "response")]
[JsonDerivedType(typeof(NodeFailure), "failure")]
[JsonDerivedType(typeof(NodeCancel),  "cancel")]
public abstract record NodeMessage;

/// <summary>Node → Lane, first frame on a new connection.</summary>
/// <param name="Model">What the node answers with. Informational only.</param>
/// <param name="Delegation">Set when replies are signed by a session key that <paramref name="Identity"/> vouched for.</param>
public sealed record NodeHello(
    string            Name,
    string            Pool,
    string            Model,
    ModelCapabilities Capabilities,
    int               MaxConcurrency,
    NodeIdentity      Identity,
    NodeDelegation?   Delegation      = null,
    int               ProtocolVersion = NodeProtocol.Version) : NodeMessage;

/// <summary>Lane → node, once the node has joined its pool.</summary>
public sealed record NodeWelcome(string ConnectionId) : NodeMessage;

/// <summary>Lane → node.</summary>
public sealed record NodeRequest(string RequestId, ModelRequest Request) : NodeMessage;

/// <summary>Node → Lane.</summary>
/// <param name="Signature">Base64, over <see cref="NodeProtocol.SigningPayload"/>.</param>
public sealed record NodeReply(string RequestId, ModelResponse Response, string Signature) : NodeMessage;

/// <summary>Node → Lane, when the handler threw.</summary>
public sealed record NodeFailure(string RequestId, string Message) : NodeMessage;

/// <summary>Lane → node, when the caller stopped waiting.</summary>
public sealed record NodeCancel(string RequestId) : NodeMessage;
