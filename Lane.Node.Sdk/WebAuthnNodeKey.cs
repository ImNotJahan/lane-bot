using System.Buffers.Text;
using System.Security.Cryptography;
using Lane.Core.Models;
using Lane.Nodes.Protocol;

namespace Lane.Node.Sdk;

/// <summary>
/// A fresh session key waiting for a security key to vouch for it. Pass <see cref="Challenge"/> to the browser's
/// <c>navigator.credentials.get</c>, then hand the resulting assertion to <see cref="Complete"/>.
/// </summary>
public sealed class WebAuthnSession : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private int _completed;

    public WebAuthnSession()
    {
        SessionPublicKey = _key.ExportSubjectPublicKeyInfo();
        Challenge        = NodeProtocol.DelegationChallenge(SessionPublicKey);
    }

    public byte[] SessionPublicKey { get; }

    public byte[] Challenge { get; }

    /// <param name="credentialPublicKey">The security key's SubjectPublicKeyInfo, as returned by
    /// <c>AuthenticatorAttestationResponse.getPublicKey()</c> when it was registered.</param>
    /// <exception cref="ArgumentException">The assertion is not a user-present signature by
    /// <paramref name="credentialPublicKey"/> over this session's challenge.</exception>
    /// <param name="credentialId">Sent to Lane so the security key can sign in to its portal.</param>
    /// <exception cref="InvalidOperationException">The session was already completed.</exception>
    public WebAuthnNodeKey Complete(
        byte[] credentialPublicKey, byte[] authenticatorData, byte[] clientDataJson, byte[] signature, byte[]? credentialId = null)
    {
        NodeIdentity identity = NodeProtocol.IdentityFor(credentialPublicKey, NodeProtocol.WebAuthnEs256);

        NodeDelegation delegation = new(
            Convert.ToBase64String(SessionPublicKey),
            Convert.ToBase64String(authenticatorData),
            Convert.ToBase64String(clientDataJson),
            Convert.ToBase64String(signature),
            credentialId is null ? null : Base64Url.EncodeToString(credentialId));

        if (!NodeProtocol.VerifyDelegation(identity, delegation))
            throw new ArgumentException("The security key's signature does not match this session.");

        if (Interlocked.Exchange(ref _completed, 1) == 1)
            throw new InvalidOperationException("This session has already been completed.");

        return new WebAuthnNodeKey(_key, identity, delegation);
    }

    /// <summary>Whether <paramref name="subjectPublicKeyInfo"/> is an ECDSA P-256 key, the only kind a node identity can use.</summary>
    public static bool IsSupportedCredentialKey(byte[] subjectPublicKeyInfo)
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int read);

            return read == subjectPublicKeyInfo.Length && key.KeySize == 256;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _completed) == 0) _key.Dispose();
    }
}

/// <summary>
/// A node identity held by a FIDO2 security key. The security key signs once, through a browser, to vouch for a session
/// key; every response is signed with the session key and carries <see cref="Delegation"/> back to the security key.
/// Created by <see cref="WebAuthnSession.Complete"/>.
/// </summary>
public sealed class WebAuthnNodeKey : INodeKey, IDisposable
{
    private readonly ECDsa _session;

    internal WebAuthnNodeKey(ECDsa session, NodeIdentity identity, NodeDelegation delegation)
    {
        _session   = session;
        Identity   = identity;
        Delegation = delegation;
    }

    public NodeIdentity Identity { get; }

    public NodeDelegation Delegation { get; }

    NodeDelegation? INodeKey.Delegation => Delegation;

    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _session.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public void Dispose() => _session.Dispose();
}
