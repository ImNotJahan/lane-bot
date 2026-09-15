using Lane.Core.Models;

namespace Lane.Node.OpenAi;

public enum NodeStatus { Stopped, WaitingForTouch, Connecting, Connected, Failed }

public enum IdentitySource { SecurityKey, KeyFile }

public sealed record HeaderSetting(string Name, string Value);

/// <summary>Everything the page's form edits. <see cref="ApiKey"/> is never written to disk or sent back.</summary>
public sealed record NodeSettings
{
    public string              LaneUrl             { get; init; } = "ws://127.0.0.1:5070";
    public string              Provider            { get; init; } = ProviderTemplates.OpenRouter.Id;
    public string              Endpoint            { get; init; } = ProviderTemplates.OpenRouter.Endpoint;
    public string              ChatCompletionsPath { get; init; } = ProviderTemplates.OpenRouter.ChatCompletionsPath;
    public string              ModelsPath          { get; init; } = ProviderTemplates.OpenRouter.ModelsPath;
    public List<HeaderSetting> Headers             { get; init; } = [.. ProviderTemplates.OpenRouter.Headers];
    public string              Model               { get; init; } = "";
    public string?             ApiKey              { get; init; }
    public string              Pool                { get; init; } = "default";
    public string              Name                { get; init; } = Environment.MachineName;
    public int                 Concurrency         { get; init; } = 4;
    public List<string>        Capabilities        { get; init; } = [.. ProviderTemplates.OpenRouter.Capabilities];
    public IdentitySource      Identity            { get; init; } = IdentitySource.SecurityKey;
    public string              KeyFilePath         { get; init; } = Path.Combine(SettingsStore.Folder, "node-key.pem");

    /// <summary>Base64url WebAuthn credential id of the registered security key.</summary>
    public string? SecurityKeyCredentialId { get; init; }

    /// <summary>Base64 SubjectPublicKeyInfo of the registered security key.</summary>
    public string? SecurityKeyPublicKey { get; init; }
}

/// <summary>What the page passes to <c>navigator.credentials.get</c>. Both values are base64url.</summary>
public sealed record TouchRequest(string Challenge, string CredentialId);

/// <param name="KeyEndpoints">Endpoints the node already has an API key for, from earlier starts or the environment.</param>
public sealed record NodeState(
    NodeStatus                      Status,
    string?                         Detail,
    NodeIdentity?                   Identity,
    string?                         ConnectionId,
    int                             Answered,
    int                             Failed,
    TouchRequest?                   Touch,
    IReadOnlyList<string>           KeyEndpoints,
    string?                         RegisteredKeyId,
    string?                         KeyFileId,
    NodeSettings                    Settings,
    IReadOnlyList<ProviderTemplate> Templates,
    IReadOnlyList<string>           Logs);

/// <param name="CredentialId">Base64url.</param>
/// <param name="PublicKey">Base64url SubjectPublicKeyInfo.</param>
/// <param name="Algorithm">COSE algorithm identifier; -7 is ES256.</param>
public sealed record SecurityKeyRegistration(string CredentialId, string PublicKey, int Algorithm);

public sealed record KeyFileRequest(string Path);

/// <param name="Challenge">Base64url portal sign-in challenge.</param>
public sealed record PortalChallenge(string Challenge);

/// <param name="PublicKey">Base64url SubjectPublicKeyInfo.</param>
/// <param name="Signature">Base64url IEEE P1363.</param>
public sealed record PortalSignature(string PublicKey, string Signature);

/// <summary>All fields base64url, straight from <c>AuthenticatorAssertionResponse</c>.</summary>
public sealed record SecurityKeyAssertion(string AuthenticatorData, string ClientDataJson, string Signature);
