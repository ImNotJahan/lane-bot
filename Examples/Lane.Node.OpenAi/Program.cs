using System.Buffers.Text;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Lane.Node.OpenAi;
using Lane.Node.Sdk;
using Lane.Nodes.Protocol;

// A Lane node that answers every request with one model from any OpenAI-compatible API, with templates for OpenRouter and
// Google Gemini, configured from a local web page. Its identity is either a security key, used through the browser's
// WebAuthn prompt (which only works when the page is served from localhost), or a key pair kept in a PEM file.
//
//   dotnet run --project Examples/Lane.Node.OpenAi [-- --urls http://localhost:5075] [--no-browser]

bool openBrowser = !args.Contains("--no-browser");

WebApplicationBuilder builder = WebApplication.CreateBuilder([.. args.Where(a => a != "--no-browser")]);

if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5075");

LogBuffer logs = new();

builder.Logging.AddProvider(logs);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

builder.Services.AddSingleton(logs);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<NodeRunner>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

WebApplication app = builder.Build();

app.MapGet("/", () => Results.Stream(
    typeof(NodeRunner).Assembly.GetManifestResourceStream("index.html")!, "text/html; charset=utf-8"));

app.MapGet("/api/state", (NodeRunner runner, SettingsStore store, LogBuffer buffer) =>
{
    NodeSettings settings = store.Load();

    return new NodeState(
        runner.Status,
        runner.Detail,
        runner.Identity,
        runner.ConnectionId,
        runner.Answered,
        runner.Failed,
        runner.Touch,
        runner.KeyEndpoints,
        RegisteredKeyId(settings),
        KeyFileId(settings.KeyFilePath),
        settings,
        ProviderTemplates.All,
        buffer.Lines);
});

app.MapPost("/api/models", async (NodeSettings settings, NodeRunner runner, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await runner.ListModelsAsync(settings, ct));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/start", async (NodeSettings settings, NodeRunner runner) =>
{
    try
    {
        await runner.StartAsync(settings);
        return Results.NoContent();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/stop", async (NodeRunner runner) =>
{
    await runner.StopAsync();
    return Results.NoContent();
});

app.MapPost("/api/security-key/register", (SecurityKeyRegistration body, SettingsStore store) =>
{
    const int es256 = -7;

    byte[] publicKey;

    try
    {
        publicKey = Base64Url.DecodeFromChars(body.PublicKey);
    }
    catch (FormatException)
    {
        return Results.BadRequest(new { error = "The security key's public key is malformed." });
    }

    if (body.Algorithm != es256 || !WebAuthnSession.IsSupportedCredentialKey(publicKey))
        return Results.BadRequest(new { error = "The security key must use ES256 (ECDSA P-256)." });

    store.Save(store.Load() with
    {
        Identity                = IdentitySource.SecurityKey,
        SecurityKeyCredentialId = body.CredentialId,
        SecurityKeyPublicKey    = Convert.ToBase64String(publicKey)
    });

    return Results.NoContent();
});

app.MapPost("/api/key-file/generate", (KeyFileRequest body, SettingsStore store) =>
{
    if (string.IsNullOrWhiteSpace(body.Path))
        return Results.BadRequest(new { error = "Choose where to save the key file." });

    string path = Path.GetFullPath(body.Path.Trim());

    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using FileNodeKey key = FileNodeKey.Create(path);

        store.Save(store.Load() with { Identity = IdentitySource.KeyFile, KeyFilePath = path });

        return Results.Ok(new { keyId = key.Identity.KeyId, path });
    }
    catch (IOException) when (File.Exists(path))
    {
        return Results.Conflict(new { error = "A file already exists there. Use it as it is, or choose another path." });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
        return Results.BadRequest(new { error = $"Could not save the key file: {ex.Message}" });
    }
});

app.MapPost("/api/key-file/sign-in", (PortalChallenge body, SettingsStore store) =>
{
    const int maxChallengeBytes = 64;

    NodeSettings settings = store.Load();

    if (settings.Identity != IdentitySource.KeyFile)
        return Results.BadRequest(new { error = "This node does not use a key pair." });

    try
    {
        byte[] challenge = Base64Url.DecodeFromChars(body.Challenge);

        if (challenge.Length is 0 or > maxChallengeBytes)
            return Results.BadRequest(new { error = "That is not a portal challenge." });

        using FileNodeKey key = FileNodeKey.Load(settings.KeyFilePath);

        return Results.Ok(new PortalSignature(
            Base64Url.EncodeToString(key.ExportPublicKey()), Base64Url.EncodeToString(key.SignSignIn(challenge))));
    }
    catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/security-key/assert", (SecurityKeyAssertion body, NodeRunner runner) =>
{
    try
    {
        runner.SubmitAssertion(
            Base64Url.DecodeFromChars(body.AuthenticatorData),
            Base64Url.DecodeFromChars(body.ClientDataJson),
            Base64Url.DecodeFromChars(body.Signature));

        return Results.NoContent();
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

using HttpClient laneHttp = new();

app.MapGet("/lane", () => Results.Redirect("/lane/"));

app.Map("/lane/{**path}", async (HttpContext context, string? path, SettingsStore store) =>
{
    if (!Uri.TryCreate(store.Load().LaneUrl, UriKind.Absolute, out Uri? lane))
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsync("Set a valid Lane URL on the node page first.");
        return;
    }

    UriBuilder target = new(lane)
    {
        Scheme = lane.Scheme switch { "ws" => "http", "wss" => "https", _ => lane.Scheme },
        Path   = "/" + path,
        Query  = context.Request.QueryString.Value ?? ""
    };

    using HttpRequestMessage request = new(new HttpMethod(context.Request.Method), target.Uri);

    if (context.Request.ContentLength > 0)
    {
        request.Content = new StreamContent(context.Request.Body);

        if (context.Request.ContentType is { } contentType)
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
    }

    if (context.Request.Headers.Authorization.Count > 0)
        request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToString());

    try
    {
        using HttpResponseMessage response =
            await laneHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

        context.Response.StatusCode = (int)response.StatusCode;

        if (response.Content.Headers.ContentType is { } type) context.Response.ContentType = type.ToString();

        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (HttpRequestException ex)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsync($"Could not reach Lane at {target.Uri.GetLeftPart(UriPartial.Authority)}: {ex.Message}");
    }
});

if (openBrowser)
    app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(app.Urls.First()));

await app.RunAsync();

static string? RegisteredKeyId(NodeSettings settings)
{
    if (settings.SecurityKeyPublicKey is not { } publicKey) return null;

    try
    {
        return NodeProtocol.IdentityFor(Convert.FromBase64String(publicKey), NodeProtocol.WebAuthnEs256).KeyId;
    }
    catch (FormatException)
    {
        return null;
    }
}

static string? KeyFileId(string path)
{
    if (!File.Exists(path)) return null;

    try
    {
        using FileNodeKey key = FileNodeKey.Load(path);
        return key.Identity.KeyId;
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
    {
        return null;
    }
}

static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        Console.WriteLine($"Open {url} in a browser.");
    }
}
