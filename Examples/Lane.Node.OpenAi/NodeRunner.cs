using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lane.Core.Models;
using Lane.Node.Sdk;
using Lane.Providers.OpenAi;

namespace Lane.Node.OpenAi;

/// <summary>Owns the single running <see cref="LaneNode"/>, replacing it whenever the page starts it with new settings.</summary>
public sealed partial class NodeRunner(SettingsStore store, ILoggerFactory loggers) : IAsyncDisposable
{
    private sealed record Plan(
        NodeSettings Settings, string Endpoint, string Key, Dictionary<string, string> Headers,
        ModelCapabilities Capabilities, byte[]? CredentialPublicKey);

    private sealed record PendingTouch(
        WebAuthnSession Session,
        byte[] CredentialPublicKey,
        TouchRequest Request,
        TaskCompletionSource<WebAuthnNodeKey> Result);

    private readonly ILogger       _log       = loggers.CreateLogger<NodeRunner>();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HttpClient    _models    = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>API keys entered on the page, by normalised endpoint, so a key is only ever sent where it was entered for.</summary>
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _run;
    private Task    _task = Task.CompletedTask;
    private int     _answered;
    private int     _failed;

    private volatile PendingTouch? _touch;

    public NodeStatus    Status       { get; private set; }
    public string?       Detail       { get; private set; }
    public NodeIdentity? Identity     { get; private set; }
    public string?       ConnectionId { get; private set; }

    public int Answered => Volatile.Read(ref _answered);
    public int Failed   => Volatile.Read(ref _failed);

    public IReadOnlyList<string> KeyEndpoints
    {
        get
        {
            lock (_keys)
                return [.. _keys.Keys, .. ProviderTemplates.All
                    .Where(t => t.KeyVariable is not null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(t.KeyVariable)))
                    .Select(t => Normalise(t.Endpoint))];
        }
    }

    /// <summary>Set while the node is waiting for the page to have the security key sign its session.</summary>
    public TouchRequest? Touch => _touch?.Request;

    /// <exception cref="ArgumentException">The settings are incomplete or malformed.</exception>
    public async Task StartAsync(NodeSettings settings)
    {
        Plan plan = Validate(settings, KeyFor(settings));

        await _lifecycle.WaitAsync();

        try
        {
            await StopCoreAsync();

            Remember(plan);
            store.Save(settings);

            Interlocked.Exchange(ref _answered, 0);
            Interlocked.Exchange(ref _failed, 0);

            Status   = settings.Identity == IdentitySource.SecurityKey ? NodeStatus.WaitingForTouch : NodeStatus.Connecting;
            Detail   = null;
            Identity = null;

            CancellationTokenSource run = new();
            _run  = run;
            _task = Task.Run(() => RunAsync(plan, run.Token));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();

        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _models.Dispose();
    }

    /// <summary>
    /// Model ids from the provider's models endpoint, without any <c>models/</c> prefix, sorted. For Gemini, only text-output
    /// models, led by <see cref="ProviderTemplates.RandomModel"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The settings are malformed, or the provider could not be asked or refused.</exception>
    public async Task<IReadOnlyList<string>> ListModelsAsync(NodeSettings settings, CancellationToken ct)
    {
        string endpoint = ValidateEndpoint(settings);
        Dictionary<string, string> headers = ValidateHeaders(settings.Headers);
        string key = KeyFor(settings);
        ProviderTemplate template = ProviderTemplates.Find(settings.Provider);

        if (template.KeyRequired && key.Length == 0)
            throw new ArgumentException($"Enter your {template.Label} API key to list models.");

        IReadOnlyList<string> models = await FetchModelsAsync(settings, endpoint, headers, key, ct);

        if (key.Length > 0) Remember(settings, endpoint, key);

        return ProviderTemplates.IsGemini(settings) ? [ProviderTemplates.RandomModel, .. models] : models;
    }

    /// <exception cref="ArgumentException">The provider could not be asked or refused.</exception>
    private async Task<IReadOnlyList<string>> FetchModelsAsync(
        NodeSettings settings, string endpoint, Dictionary<string, string> headers, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelsPath)) throw new ArgumentException("A models path is required to list models.");

        using HttpRequestMessage request = new(HttpMethod.Get, Resolve(endpoint, settings.ModelsPath));

        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        foreach ((string name, string value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        try
        {
            using HttpResponseMessage response = await _models.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ArgumentException($"The models endpoint answered {(int)response.StatusCode}: {Shorten(body)}");

            using JsonDocument document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("The models endpoint did not return an OpenAI-style list.");

            bool gemini = ProviderTemplates.IsGemini(settings);

            return [.. data.EnumerateArray()
                .Select(model => model.TryGetProperty("id", out JsonElement id) ? id.GetString() : null)
                .OfType<string>()
                .Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id)
                .Where(id => !gemini || ProviderTemplates.IsGeminiTextModel(id))
                .Distinct()
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ArgumentException($"Could not list models: {ex.Message}");
        }
    }

    /// <summary>The key typed on the page, else one remembered or from the environment for this exact endpoint.</summary>
    private string KeyFor(NodeSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey)) return settings.ApiKey.Trim();

        string endpoint = Normalise(settings.Endpoint ?? "");

        lock (_keys)
            if (_keys.TryGetValue(endpoint, out string? remembered)) return remembered;

        return ProviderTemplates.ForEndpoint(endpoint)?.KeyVariable is { } variable
            ? Environment.GetEnvironmentVariable(variable)?.Trim() ?? ""
            : "";
    }

    private void Remember(Plan plan) => Remember(plan.Settings, plan.Endpoint, plan.Key);

    private void Remember(NodeSettings settings, string endpoint, string key)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) return;

        lock (_keys) _keys[Normalise(endpoint)] = key;
    }

    private static bool IsRandom(NodeSettings s) =>
        string.Equals(s.Model.Trim(), ProviderTemplates.RandomModel, StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string endpoint) => endpoint.Trim().TrimEnd('/');

    private static Uri Resolve(string endpoint, string path) => new(new Uri(endpoint + "/"), path.Trim().TrimStart('/'));

    private static string Shorten(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "…";

    /// <exception cref="InvalidOperationException">Nothing is waiting for the security key.</exception>
    /// <exception cref="ArgumentException">The assertion does not verify against the registered security key.</exception>
    public void SubmitAssertion(byte[] authenticatorData, byte[] clientDataJson, byte[] signature)
    {
        PendingTouch touch = _touch ?? throw new InvalidOperationException("The node is not waiting for a security key.");

        WebAuthnNodeKey key = touch.Session.Complete(
            touch.CredentialPublicKey, authenticatorData, clientDataJson, signature,
            Base64Url.DecodeFromChars(touch.Request.CredentialId));

        if (!touch.Result.TrySetResult(key)) key.Dispose();
    }

    private async Task StopCoreAsync()
    {
        if (_run is null) return;

        await _run.CancelAsync();
        await _task;

        _run.Dispose();
        _run = null;

        Status       = NodeStatus.Stopped;
        Detail       = null;
        ConnectionId = null;
    }

    private async Task RunAsync(Plan plan, CancellationToken ct)
    {
        NodeSettings s = plan.Settings;
        INodeKey? key = null;

        try
        {
            if (s.Identity == IdentitySource.KeyFile)
            {
                key = FileNodeKey.Load(s.KeyFilePath);
            }
            else
            {
                key = await AwaitTouchAsync(plan, ct);
            }

            Identity = key.Identity;
            Status   = NodeStatus.Connecting;
            Detail   = null;

            _log.LogInformation("Node identity {KeyId}", key.Identity.KeyId);

            using HttpClient http = new();

            IReadOnlyList<string> models = IsRandom(s)
                ? await FetchModelsAsync(s, plan.Endpoint, plan.Headers, plan.Key, ct)
                : [s.Model.Trim()];

            if (models.Count == 0) throw new InvalidOperationException("The provider listed no text-output models to choose from.");

            OpenAiCompatibleModel[] providers = [.. models.Select(model => new OpenAiCompatibleModel(
                http,
                new OpenAiCompatibleOptions
                {
                    InstanceId          = "node",
                    Model               = model,
                    ApiKey              = plan.Key,
                    Endpoint            = plan.Endpoint,
                    ChatCompletionsPath = s.ChatCompletionsPath.Trim(),
                    Headers             = plan.Headers,
                    AppName             = null,
                    Capabilities        = plan.Capabilities
                },
                loggers.CreateLogger<OpenAiCompatibleModel>()))];

            if (IsRandom(s))
                _log.LogInformation("Answering with a random one of {Count} models at {Endpoint}", providers.Length, plan.Endpoint);
            else
                _log.LogInformation("Answering with {Model} at {Endpoint}", models[0], plan.Endpoint);

            LaneNode node = new(
                new LaneNodeOptions
                {
                    LaneUrl        = s.LaneUrl,
                    Model          = s.Model,
                    Pool           = s.Pool,
                    Name           = s.Name,
                    MaxConcurrency = s.Concurrency,
                    Capabilities   = plan.Capabilities
                },
                key,
                async (request, token) =>
                {
                    try
                    {
                        ModelResponse response = IsRandom(s)
                            ? await CompleteWithRandomModelAsync(providers, request, token)
                            : await providers[0].CompleteAsync(request, token);
                        Interlocked.Increment(ref _answered);
                        return response;
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _failed);
                        throw;
                    }
                },
                loggers.CreateLogger<LaneNode>());

            node.Connected += id =>
            {
                Status       = NodeStatus.Connected;
                ConnectionId = id;
                Detail       = null;
            };

            node.Disconnected += reason =>
            {
                Status       = NodeStatus.Connecting;
                ConnectionId = null;
                Detail       = reason;
            };

            await node.RunAsync(ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError("Node stopped: {Reason}", ex.Message);

            Status       = NodeStatus.Failed;
            Detail       = ex.Message;
            ConnectionId = null;
        }
        finally
        {
            (key as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Answers with a random model, retrying up to three times on a 404, 429 or 503 with a model not yet tried for this request
    /// while any remain.
    /// </summary>
    private async Task<ModelResponse> CompleteWithRandomModelAsync(
        OpenAiCompatibleModel[] providers, ModelRequest request, CancellationToken ct)
    {
        const int maxRetries = 3;

        List<OpenAiCompatibleModel> untried = [.. providers];

        for (int attempt = 0; ; attempt++)
        {
            if (untried.Count == 0) untried.AddRange(providers);

            int index = Random.Shared.Next(untried.Count);
            OpenAiCompatibleModel provider = untried[index];
            untried.RemoveAt(index);

            try
            {
                return await provider.CompleteAsync(request, ct);
            }
            catch (HttpRequestException ex) when (
                attempt < maxRetries && ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                _log.LogWarning("{Model} answered {Status}; retrying with another model", provider.Descriptor, (int)ex.StatusCode!);
            }
        }
    }

    private async Task<WebAuthnNodeKey> AwaitTouchAsync(Plan plan, CancellationToken ct)
    {
        using WebAuthnSession session = new();

        PendingTouch touch = new(
            session,
            plan.CredentialPublicKey!,
            new TouchRequest(Base64Url.EncodeToString(session.Challenge), plan.Settings.SecurityKeyCredentialId!),
            new TaskCompletionSource<WebAuthnNodeKey>(TaskCreationOptions.RunContinuationsAsynchronously));

        Status = NodeStatus.WaitingForTouch;
        Detail = "Waiting for the security key to be touched.";

        _touch = touch;
        _log.LogInformation("Waiting for the security key");

        try
        {
            return await touch.Result.Task.WaitAsync(ct);
        }
        finally
        {
            _touch = null;
        }
    }

    private static Plan Validate(NodeSettings s, string key)
    {
        if (!Uri.TryCreate(s.LaneUrl, UriKind.Absolute, out Uri? lane) || lane.Scheme is not ("ws" or "wss" or "http" or "https"))
            throw new ArgumentException("The Lane URL must be an absolute ws://, wss://, http:// or https:// address.");

        string endpoint = ValidateEndpoint(s);
        Dictionary<string, string> headers = ValidateHeaders(s.Headers);
        ProviderTemplate template = ProviderTemplates.Find(s.Provider);

        if (string.IsNullOrWhiteSpace(s.ChatCompletionsPath) || s.ChatCompletionsPath.Contains("://"))
            throw new ArgumentException("The chat completions path must be relative to the endpoint, like chat/completions.");

        if (string.IsNullOrWhiteSpace(s.Model)) throw new ArgumentException("A model is required.");

        if (IsRandom(s) && !ProviderTemplates.IsGemini(s))
            throw new ArgumentException($"The '{ProviderTemplates.RandomModel}' model is only available with {ProviderTemplates.Gemini.Label}.");

        if (template.KeyRequired && string.IsNullOrWhiteSpace(key))
            throw new ArgumentException($"An API key is required for {template.Label}.");

        if (string.IsNullOrWhiteSpace(s.Pool))  throw new ArgumentException("A pool is required.");
        if (string.IsNullOrWhiteSpace(s.Name))  throw new ArgumentException("A node name is required.");
        if (s.Concurrency < 1)                  throw new ArgumentException("Max concurrent requests must be at least 1.");

        ModelCapabilities capabilities = ModelCapabilities.None;

        foreach (string name in s.Capabilities)
        {
            if (!Enum.TryParse(name, ignoreCase: true, out ModelCapabilities one))
                throw new ArgumentException($"Unknown capability '{name}'.");

            capabilities |= one;
        }

        byte[]? credentialPublicKey = null;

        if (s.Identity == IdentitySource.SecurityKey)
        {
            if (string.IsNullOrWhiteSpace(s.SecurityKeyCredentialId) || string.IsNullOrWhiteSpace(s.SecurityKeyPublicKey))
                throw new ArgumentException("Register a security key before starting.");

            try
            {
                credentialPublicKey = Convert.FromBase64String(s.SecurityKeyPublicKey);
            }
            catch (FormatException)
            {
                throw new ArgumentException("The registered security key is malformed; register it again.");
            }
        }

        if (s.Identity == IdentitySource.KeyFile && (string.IsNullOrWhiteSpace(s.KeyFilePath) || !File.Exists(s.KeyFilePath)))
            throw new ArgumentException("There is no key pair at that path. Generate one, or point to an existing key file.");

        return new Plan(s, endpoint, key, headers, capabilities, credentialPublicKey);
    }

    private static string ValidateEndpoint(NodeSettings s)
    {
        if (!Uri.TryCreate(s.Endpoint?.Trim(), UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("The endpoint must be an absolute http:// or https:// address, like https://api.openai.com/v1.");

        return Normalise(s.Endpoint!);
    }

    private static Dictionary<string, string> ValidateHeaders(IEnumerable<HeaderSetting>? headers)
    {
        Dictionary<string, string> valid = new(StringComparer.OrdinalIgnoreCase);

        foreach (HeaderSetting header in headers ?? [])
        {
            string name = header.Name?.Trim() ?? "";

            if (name.Length == 0) continue;

            if (!HeaderName().IsMatch(name)) throw new ArgumentException($"'{name}' is not a valid header name.");

            if (header.Value is null || header.Value.Any(c => c is '\r' or '\n'))
                throw new ArgumentException($"The value of header '{name}' cannot span lines.");

            valid[name] = header.Value.Trim();
        }

        return valid;
    }

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderName();
}
