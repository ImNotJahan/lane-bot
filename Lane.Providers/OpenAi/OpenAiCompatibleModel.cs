using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lane.Core.Messages;
using Lane.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lane.Providers.OpenAi;

public sealed class OpenAiCompatibleOptions
{
    public required string InstanceId { get; init; }
    public required string Model      { get; init; }
    public required string Endpoint   { get; init; }
    /// <summary>Sent as a bearer token. Empty sends no Authorization header, for local servers.</summary>
    public required string ApiKey     { get; init; }

    /// <summary>Relative to <see cref="Endpoint"/>; may carry a query string.</summary>
    public string ChatCompletionsPath { get; init; } = "chat/completions";

    /// <summary>Added to every request.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// What this particular model can do.
    ///
    /// Declared per instance rather than per provider because OpenRouter fronts hundreds of
    /// models whose capabilities differ completely — some advertise tools and then emit
    /// malformed arguments, others have no tool support at all. Getting this wrong is
    /// caught at startup by the registry's validation rather than mid-conversation.
    /// </summary>
    public ModelCapabilities Capabilities { get; init; } =
        ModelCapabilities.Tools | ModelCapabilities.Streaming | ModelCapabilities.StopSequences;

    /// <summary>Sent by OpenRouter for attribution; harmless elsewhere.</summary>
    public string? AppName { get; init; } = "Lane";
}

/// <summary>
/// One adapter for every OpenAI-compatible endpoint: OpenRouter, DeepSeek, a local server.
///
/// Speaks the protocol directly instead of through an SDK. That is a deliberate carry-over
/// from v2, which had to reach past its client library to stop it throwing on
/// <c>finish_reason</c> values it did not recognise — a real problem when one endpoint
/// proxies many providers.
/// </summary>
public sealed class OpenAiCompatibleModel : ILanguageModel
{
    private readonly HttpClient _http;
    private readonly OpenAiCompatibleOptions _options;
    private readonly ILogger<OpenAiCompatibleModel> _log;

    public OpenAiCompatibleModel(
        HttpClient http, OpenAiCompatibleOptions options, ILogger<OpenAiCompatibleModel> log)
    {
        _http    = http;
        _options = options;
        _log     = log;

        _http.BaseAddress ??= new Uri(options.Endpoint.TrimEnd('/') + "/");

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        if (!string.IsNullOrWhiteSpace(options.AppName))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", options.AppName);

        foreach ((string name, string value) in options.Headers)
            _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);

        Descriptor = new ModelDescriptor(
            options.InstanceId, ProviderNameFor(options.Endpoint), options.Model, options.Capabilities);
    }

    public ModelDescriptor Descriptor { get; }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        JsonObject body = BuildBody(request);

        long started = Stopwatch.GetTimestamp();

        using HttpResponseMessage response = await _http
            .PostAsJsonAsync(_options.ChatCompletionsPath.TrimStart('/'), body, ct)
            .ConfigureAwait(false);

        string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{Descriptor} returned {(int)response.StatusCode}: {Truncate(raw)}", null, response.StatusCode);

        TimeSpan latency = Stopwatch.GetElapsedTime(started);

        using JsonDocument document = JsonDocument.Parse(raw);
        JsonElement root = document.RootElement;

        // Some gateways report upstream failures with a 200 and an error body.
        if (root.TryGetProperty("error", out JsonElement error))
            throw new HttpRequestException($"{Descriptor} returned an error: {error.GetRawText()}");

        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            _log.LogError("{Model} returned no choices: {Body}", Descriptor, Truncate(raw));
            return new ModelResponse([], StopReason.Other, UsageFrom(root, latency));
        }

        JsonElement choice = choices[0];

        List<ContentPart> content = choice.TryGetProperty("message", out JsonElement message)
            ? OpenAiMessageMapper.FromMessage(message)
            : [];

        string? finish = choice.TryGetProperty("finish_reason", out JsonElement f) ? f.GetString() : null;

        StopReason stop = OpenAiMessageMapper.FromFinishReason(finish);

        // Some providers omit finish_reason entirely when they return tool calls.
        if (stop != StopReason.ToolUse && content.OfType<ToolUsePart>().Any()) stop = StopReason.ToolUse;

        if (stop == StopReason.Other)
            _log.LogDebug("{Model} returned an unfamiliar finish_reason '{Reason}'", Descriptor, finish);

        TokenUsage usage = UsageFrom(root, latency);

        _log.LogDebug("{Model} in={In} out={Out} cached={Cached} in {Latency}ms",
            Descriptor.InstanceId, usage.Input, usage.Output, usage.CacheRead, (int)latency.TotalMilliseconds);

        return new ModelResponse(content, stop, usage);
    }

    /// <summary>
    /// Emits the finished response as one delta. Incremental streaming arrives with the
    /// audio work, where first-audio latency is what it actually buys.
    /// </summary>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ModelResponse response = await CompleteAsync(request, ct).ConfigureAwait(false);

        if (response.Text.Length > 0) yield return new ModelStreamEvent.TextDelta(response.Text);

        foreach (ToolUsePart call in response.ToolCalls)
            yield return new ModelStreamEvent.ToolUseStarted(call.ToolCallId, call.ToolName);

        yield return new ModelStreamEvent.Completed(response);
    }

    private JsonObject BuildBody(ModelRequest request)
    {
        JsonObject body = new()
        {
            ["model"]       = _options.Model,
            ["messages"]    = OpenAiMessageMapper.ToMessages(request.System, request.Messages),
            ["max_tokens"]  = request.MaxOutputTokens,
            ["temperature"] = request.Temperature
        };

        if (request.StopSequences is { Count: > 0 })
            body["stop"] = new JsonArray([.. request.StopSequences.Select(s => JsonValue.Create(s))]);

        if (request.Tools.Count > 0)
        {
            body["tools"] = OpenAiMessageMapper.ToTools(request.Tools);

            JsonNode? choice = OpenAiMessageMapper.ToToolChoice(request.ToolChoice);
            if (choice is not null) body["tool_choice"] = choice;
        }

        if (request.ResponseFormat is { } format)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"]   = format.Name,
                    ["strict"] = format.Strict,
                    ["schema"] = JsonNode.Parse(format.Schema.GetRawText())
                }
            };
        }

        return body;
    }

    private TokenUsage UsageFrom(JsonElement root, TimeSpan latency)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage))
            return new TokenUsage(0, 0, 0, 0, Descriptor.InstanceId, latency);

        int input  = Int(usage, "prompt_tokens");
        int output = Int(usage, "completion_tokens");

        int cached = usage.TryGetProperty("prompt_tokens_details", out JsonElement details)
            ? Int(details, "cached_tokens")
            : 0;

        return new TokenUsage(input, output, cached, 0, Descriptor.InstanceId, latency);
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : 0;

    private static string ProviderNameFor(string endpoint) =>
        endpoint.Contains("openrouter", StringComparison.OrdinalIgnoreCase) ? "openrouter"
        : endpoint.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ? "deepseek"
        : "openai-compatible";

    private static string Truncate(string text) => text.Length > 500 ? text[..500] + "…" : text;
}
