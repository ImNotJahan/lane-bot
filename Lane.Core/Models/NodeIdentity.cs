namespace Lane.Core.Models;

/// <param name="KeyId">Lowercase hex of the first 16 bytes of SHA-256 over the public key.</param>
/// <param name="Algorithm"><c>ecdsa-p256-sha256</c> when the key signs responses itself; <c>webauthn-es256</c> when it is a
/// security key that vouches for a session key through a <see cref="NodeDelegation"/>.</param>
/// <param name="PublicKey">Base64 SubjectPublicKeyInfo.</param>
public sealed record NodeIdentity(string KeyId, string Algorithm, string PublicKey);

/// <summary>A WebAuthn assertion by a node's identity key over the session key that signs its responses.</summary>
/// <param name="SessionPublicKey">Base64 SubjectPublicKeyInfo of the session key.</param>
/// <param name="AuthenticatorData">Base64.</param>
/// <param name="ClientDataJson">Base64.</param>
/// <param name="Signature">Base64.</param>
/// <param name="CredentialId">Base64url WebAuthn credential id of the identity key. Not covered by any signature.</param>
public sealed record NodeDelegation(
    string  SessionPublicKey,
    string  AuthenticatorData,
    string  ClientDataJson,
    string  Signature,
    string? CredentialId = null);

/// <summary>The node that produced a response, and its signature over it.</summary>
/// <param name="Signature">Base64, over <c>NodeProtocol.SigningPayload</c> for the request. Made by the session key when
/// <paramref name="Delegation"/> is set, otherwise by the identity key.</param>
public sealed record NodeAttestation(NodeIdentity Identity, string Signature, NodeDelegation? Delegation = null);
