namespace Lane.Host.Configuration;

/// <summary>
/// Turns a config-visible reference into a secret.
///
/// Parts never read secrets themselves — v2's TTS class reaching for a global settings
/// singleton in its constructor is precisely why it could only ever have one voice.
/// Resolution happens once, at composition, so two instances of the same part can hold
/// different credentials and the storage mechanism can change without touching them.
/// </summary>
public interface ISecretResolver
{
    /// <summary>Resolves "env:NAME", "file:/path", or a literal value. Null/empty passes through.</summary>
    string? Resolve(string? reference);

    string Require(string? reference, string purpose);
}

public sealed class SecretResolver : ISecretResolver
{
    public string? Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(reference[4..].Trim());

        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            string path = reference[5..].Trim();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        return reference;
    }

    public string Require(string? reference, string purpose)
    {
        string? value = Resolve(reference);

        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"No secret available for {purpose} (reference: '{reference ?? "<unset>"}'). " +
                "Set it in .env or the environment, or point the reference at a file.");

        return value;
    }
}
