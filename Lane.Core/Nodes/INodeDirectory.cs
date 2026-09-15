using Lane.Core.Models;

namespace Lane.Core.Nodes;

/// <param name="LastNodeName">The name the identity's most recent node connected with.</param>
/// <param name="Responses">Requests its nodes have answered.</param>
public sealed record NodeIdentityRecord(
    string         KeyId,
    string         Algorithm,
    string         PublicKey,
    string?        Nickname,
    string?        LastNodeName,
    long           Responses,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen)
{
    public string DisplayName => Nickname ?? LastNodeName ?? KeyId[..Math.Min(8, KeyId.Length)];
}

public sealed class NicknameTakenException(string nickname)
    : InvalidOperationException($"The nickname '{nickname}' is already taken.")
{
    public string Nickname { get; } = nickname;
}

/// <summary>Every node identity Lane has seen, with its nickname and how many requests it has answered.</summary>
public interface INodeDirectory
{
    /// <summary>Adds the identity if it is new and remembers the node name and WebAuthn credential id it connected with.</summary>
    /// <param name="credentialId">Base64url.</param>
    ValueTask SeenAsync(NodeIdentity identity, string? nodeName, string? credentialId, CancellationToken ct);

    ValueTask RecordResponseAsync(NodeIdentity identity, CancellationToken ct);

    ValueTask<NodeIdentityRecord?> FindAsync(string keyId, CancellationToken ct);

    /// <summary>Most responses first.</summary>
    ValueTask<IReadOnlyList<NodeIdentityRecord>> ListAsync(CancellationToken ct);

    /// <param name="credentialId">Base64url.</param>
    ValueTask<IReadOnlyList<NodeIdentityRecord>> FindByCredentialAsync(string credentialId, CancellationToken ct);

    /// <param name="credentialId">Base64url.</param>
    ValueTask LinkCredentialAsync(string keyId, string credentialId, CancellationToken ct);

    /// <summary>Base64url ids of every WebAuthn credential a node has connected with.</summary>
    ValueTask<IReadOnlyList<string>> CredentialIdsAsync(CancellationToken ct);

    /// <summary>Nicknames are unique ignoring case; null clears it. False when the identity is unknown.</summary>
    /// <exception cref="NicknameTakenException"/>
    ValueTask<bool> SetNicknameAsync(string keyId, string? nickname, CancellationToken ct);
}
