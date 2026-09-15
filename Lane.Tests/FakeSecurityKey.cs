using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Lane.Node.Sdk;

namespace Lane.Tests;

/// <summary>Plays the authenticator's part of a WebAuthn assertion, the way a browser and security key would.</summary>
internal sealed class FakeSecurityKey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    public WebAuthnNodeKey Vouch(WebAuthnSession session, bool userPresent = true, byte[]? challenge = null)
    {
        (byte[] authenticatorData, byte[] clientDataJson, byte[] signature) = Assert(challenge ?? session.Challenge, userPresent);

        return session.Complete(PublicKey, authenticatorData, clientDataJson, signature, CredentialId);
    }

    public (byte[] AuthenticatorData, byte[] ClientDataJson, byte[] Signature) Assert(byte[] challenge, bool userPresent = true)
    {
        byte[] authenticatorData = [.. SHA256.HashData("localhost"u8), (byte)(userPresent ? 0x01 : 0x00), 0, 0, 0, 1];

        byte[] clientDataJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type      = "webauthn.get",
            challenge = Base64Url.EncodeToString(challenge),
            origin    = "http://localhost:5075"
        });

        byte[] signature = _key.SignData(
            [.. authenticatorData, .. SHA256.HashData(clientDataJson)],
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        return (authenticatorData, clientDataJson, signature);
    }

    public void Dispose() => _key.Dispose();
}
