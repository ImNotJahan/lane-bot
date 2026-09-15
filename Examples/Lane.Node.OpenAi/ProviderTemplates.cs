using System.Text.RegularExpressions;

namespace Lane.Node.OpenAi;

/// <summary>Values the page fills in when a template is chosen. Every field stays editable afterwards.</summary>
/// <param name="KeyVariable">Environment variable an empty API key falls back to, for this template's endpoint only.</param>
public sealed record ProviderTemplate(
    string                Id,
    string                Label,
    string                Endpoint,
    string                ChatCompletionsPath,
    string                ModelsPath,
    string                ModelPlaceholder,
    string                KeyPlaceholder,
    string?               KeyVariable,
    bool                  KeyRequired,
    List<HeaderSetting>   Headers,
    List<string>          Capabilities);

public static partial class ProviderTemplates
{
    public const string CustomId = "custom";

    /// <summary>A Gemini model setting that sends each request to a random text-output model.</summary>
    public const string RandomModel = "random";

    public static ProviderTemplate OpenRouter { get; } = new(
        "openrouter", "OpenRouter",
        "https://openrouter.ai/api/v1", "chat/completions", "models",
        "google/gemini-3.7-flash", "sk-or-…", "OPENROUTER_API_KEY", KeyRequired: true,
        [new HeaderSetting("X-Title", "Lane")],
        ["Tools", "Streaming", "StopSequences"]);

    public static ProviderTemplate Gemini { get; } = new(
        "gemini", "Google Gemini",
        "https://generativelanguage.googleapis.com/v1beta/openai", "chat/completions", "models",
        "gemini-2.5-flash", "AIza…", "GEMINI_API_KEY", KeyRequired: true,
        [],
        ["Tools", "Streaming", "Images", "StructuredOutput", "StopSequences"]);

    public static ProviderTemplate Custom { get; } = new(
        CustomId, "Custom (any OpenAI-compatible API)",
        "http://localhost:11434/v1", "chat/completions", "models",
        "model name", "Optional for local servers", null, KeyRequired: false,
        [],
        ["Tools", "Streaming", "StopSequences"]);

    public static IReadOnlyList<ProviderTemplate> All { get; } = [OpenRouter, Gemini, Custom];

    public static ProviderTemplate Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Custom;

    /// <summary>The template whose endpoint this is, ignoring a trailing slash and case; null for any other endpoint.</summary>
    public static ProviderTemplate? ForEndpoint(string endpoint) =>
        All.FirstOrDefault(t => t.KeyVariable is not null && SameEndpoint(t.Endpoint, endpoint));

    public static bool IsGemini(NodeSettings settings) =>
        string.Equals(settings.Provider, Gemini.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a Gemini model id names a model that answers in text, judged by its name.</summary>
    public static bool IsGeminiTextModel(string id) => !NonTextGeminiModel().IsMatch(id);

    [GeneratedRegex(@"(^|-)(embedding|image|imagen|veo|tts|audio|live|lyria|aqa)(-|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NonTextGeminiModel();

    public static bool SameEndpoint(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
