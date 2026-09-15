using System.Security.Cryptography;
using Lane.Core.Models;
using Lane.Nodes.Protocol;

namespace Lane.Node.Sdk;

/// <summary>
/// A node identity that is a P-256 key pair held in memory or a PKCS#8 PEM file, as an alternative to a security key
/// (<see cref="WebAuthnNodeKey"/>). Whoever has the file is the identity.
/// </summary>
public sealed class FileNodeKey : INodeKey, IDisposable
{
    private readonly ECDsa _key;

    public FileNodeKey(ECDsa key)
    {
        if (key.KeySize != 256) throw new ArgumentException("Node keys must be P-256.", nameof(key));

        _key     = key;
        Identity = NodeProtocol.IdentityFor(key.ExportSubjectPublicKeyInfo());
    }

    public NodeIdentity Identity { get; }

    public NodeDelegation? Delegation => null;

    public static FileNodeKey Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Generates a key pair and writes it to <paramref name="path"/>, readable only by the current user where supported.</summary>
    /// <exception cref="IOException">A file already exists at <paramref name="path"/>.</exception>
    public static FileNodeKey Create(string path)
    {
        FileNodeKey key = Generate();

        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (StreamWriter writer = new(path, options))
                writer.Write(key._key.ExportPkcs8PrivateKeyPem());

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    /// <exception cref="ArgumentException">The file does not hold an unencrypted P-256 private key.</exception>
    public static FileNodeKey Load(string path)
    {
        ECDsa key = ECDsa.Create();

        try
        {
            key.ImportFromPem(File.ReadAllText(path));

            return new FileNodeKey(key);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            throw new ArgumentException($"{path} does not hold an unencrypted P-256 private key.", ex);
        }
    }

    /// <summary>Loads the key at <paramref name="path"/>, generating and saving one if the file does not exist.</summary>
    public static FileNodeKey LoadOrCreate(string path) => File.Exists(path) ? Load(path) : Create(path);

    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    /// <summary>Answers a portal sign-in challenge: an IEEE P1363 signature over <see cref="NodeProtocol.SignInPayload"/>.</summary>
    public byte[] SignSignIn(ReadOnlySpan<byte> challenge) =>
        _key.SignData(NodeProtocol.SignInPayload(challenge), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public byte[] ExportPublicKey() => _key.ExportSubjectPublicKeyInfo();

    public void Dispose() => _key.Dispose();
}
