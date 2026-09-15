using Lane.Core.Models;

namespace Lane.Node.Sdk;

/// <summary>The key a node is identified by.</summary>
public interface INodeKey
{
    NodeIdentity Identity { get; }

    /// <summary>When set, <see cref="Sign"/> signs with the session key this delegation vouches for, not the identity key.</summary>
    NodeDelegation? Delegation { get; }

    byte[] Sign(ReadOnlySpan<byte> data);
}
