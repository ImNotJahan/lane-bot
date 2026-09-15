using System.Buffers;
using System.Buffers.Text;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lane.Core.Models;

namespace Lane.Nodes.Protocol;

public static class NodeProtocol
{
    public const int Version = 2;

    public const string ConnectPath = "/v1/nodes/connect";

    /// <summary>The identity key signs responses directly: ECDSA over P-256 with SHA-256, DER-encoded.</summary>
    public const string EcdsaP256Sha256 = "ecdsa-p256-sha256";

    /// <summary>The identity key is a WebAuthn ES256 credential that vouches for a session key.</summary>
    public const string WebAuthnEs256 = "webauthn-es256";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter() }
    };

    /// <summary>The bytes a node signs: the request id, a newline, then the response JSON without its origin.</summary>
    public static byte[] SigningPayload(string requestId, ModelResponse response) =>
        Encoding.UTF8.GetBytes(requestId + "\n" + JsonSerializer.Serialize(response with { Origin = null }, Json));

    /// <summary>The bytes a key pair identity signs to sign in to the portal: a fixed prefix, then the challenge.</summary>
    public static byte[] SignInPayload(ReadOnlySpan<byte> challenge) => [.. "lane-portal-sign-in\n"u8, .. challenge];

    /// <summary>Checks an <see cref="EcdsaP256Sha256"/> identity's IEEE P1363 (r‖s) signature over <see cref="SignInPayload"/>.</summary>
    public static bool VerifySignIn(NodeIdentity identity, byte[] challenge, byte[] signature) =>
        identity.Algorithm == EcdsaP256Sha256 &&
        VerifyEcdsa(identity.PublicKey, SignInPayload(challenge), signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>The WebAuthn challenge a security key signs to vouch for <paramref name="sessionPublicKey"/>.</summary>
    public static byte[] DelegationChallenge(byte[] sessionPublicKey) => SHA256.HashData(sessionPublicKey);

    public static NodeIdentity IdentityFor(byte[] subjectPublicKeyInfo, string algorithm = EcdsaP256Sha256) =>
        new(Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo).AsSpan(0, 16)),
            algorithm,
            Convert.ToBase64String(subjectPublicKeyInfo));

    public static bool Verify(NodeAttestation attestation, string requestId, ModelResponse response)
    {
        byte[] payload = SigningPayload(requestId, response);

        if (attestation.Delegation is not { } delegation)
            return attestation.Identity.Algorithm == EcdsaP256Sha256 &&
                   VerifyEcdsa(attestation.Identity.PublicKey, payload, attestation.Signature);

        return VerifyDelegation(attestation.Identity, delegation) &&
               VerifyEcdsa(delegation.SessionPublicKey, payload, attestation.Signature);
    }

    /// <summary>
    /// Checks that <paramref name="delegation"/> is a user-present WebAuthn assertion by <paramref name="identity"/> whose
    /// challenge is <see cref="DelegationChallenge"/> of the session key. The relying party and origin are not checked.
    /// </summary>
    public static bool VerifyDelegation(NodeIdentity identity, NodeDelegation delegation)
    {
        if (identity.Algorithm != WebAuthnEs256) return false;

        try
        {
            return VerifyAssertion(
                identity.PublicKey,
                Convert.FromBase64String(delegation.AuthenticatorData),
                Convert.FromBase64String(delegation.ClientDataJson),
                Convert.FromBase64String(delegation.Signature),
                DelegationChallenge(Convert.FromBase64String(delegation.SessionPublicKey)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks that a WebAuthn <c>webauthn.get</c> assertion is user-present, answers <paramref name="challenge"/>, and is
    /// signed by <paramref name="subjectPublicKeyInfo"/> (base64). The relying party and origin are not checked.
    /// </summary>
    public static bool VerifyAssertion(
        string subjectPublicKeyInfo, byte[] authenticatorData, byte[] clientDataJson, byte[] signature, byte[] challenge)
    {
        const int userPresent = 0x01;

        if (authenticatorData.Length < 37 || (authenticatorData[32] & userPresent) == 0) return false;

        try
        {
            using JsonDocument clientData = JsonDocument.Parse(clientDataJson);
            JsonElement root = clientData.RootElement;

            if (root.GetProperty("type").GetString() != "webauthn.get") return false;

            if (root.GetProperty("challenge").GetString() != Base64Url.EncodeToString(challenge)) return false;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }

        return VerifyEcdsa(subjectPublicKeyInfo, [.. authenticatorData, .. SHA256.HashData(clientDataJson)], signature);
    }

    public static async Task SendAsync(WebSocket socket, NodeMessage message, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);

        await socket.SendAsync(new ReadOnlyMemory<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Returns null once the peer has closed the socket.</summary>
    public static async Task<NodeMessage?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        ArrayBufferWriter<byte> buffer = new();

        while (true)
        {
            ValueWebSocketReceiveResult result = await socket
                .ReceiveAsync(buffer.GetMemory(16 * 1024), ct)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close) return null;

            buffer.Advance(result.Count);

            if (result.EndOfMessage) break;
        }

        return JsonSerializer.Deserialize<NodeMessage>(buffer.WrittenSpan, Json);
    }

    private static bool VerifyEcdsa(string subjectPublicKeyInfo, byte[] data, string signature)
    {
        try
        {
            return VerifyEcdsa(subjectPublicKeyInfo, data, Convert.FromBase64String(signature));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool VerifyEcdsa(
        string subjectPublicKeyInfo, byte[] data, byte[] signature, DSASignatureFormat format = DSASignatureFormat.Rfc3279DerSequence)
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(subjectPublicKeyInfo), out _);

            return key.KeySize == 256 && key.VerifyData(data, signature, HashAlgorithmName.SHA256, format);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
