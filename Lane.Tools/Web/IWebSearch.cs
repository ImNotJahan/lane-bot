using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Web;

public sealed record SearchHit(string Title, string Url, string Description);

public interface IWebSearch
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct);

    Task<string> FetchAsync(string url, int maxLength, CancellationToken ct);
}

public sealed class BraveSearchOptions
{
    public string? ApiKey { get; set; }

    public string Endpoint { get; set; } = "https://api.search.brave.com/res/v1/web/search";

    public string UserAgent { get; set; } = "Mozilla/5.0 (compatible; LaneBot/1.0)";
}

/// <summary>
/// Brave web search and plain page fetching, ported from v2's static helper.
///
/// A service rather than a static class so the HTTP client is managed, the key is resolved
/// once at composition, and a test can substitute it without a network.
/// </summary>
public sealed partial class BraveWebSearch(
    HttpClient http,
    BraveSearchOptions options,
    ILogger<BraveWebSearch> log) : IWebSearch
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("No Brave API key is configured; web search is unavailable.");

        string url = $"{options.Endpoint}?q={Uri.EscapeDataString(query)}&count={count}";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Subscription-Token", options.ApiKey);

        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("web", out JsonElement web) ||
            !web.TryGetProperty("results", out JsonElement results) ||
            results.ValueKind != JsonValueKind.Array)
            return [];

        List<SearchHit> hits = [];

        foreach (JsonElement result in results.EnumerateArray().Take(count))
        {
            string? title = result.TryGetProperty("title", out JsonElement t) ? t.GetString() : null;
            string? link  = result.TryGetProperty("url", out JsonElement u) ? u.GetString() : null;
            string? blurb = result.TryGetProperty("description", out JsonElement d) ? d.GetString() : null;

            if (title is null || link is null) continue;

            hits.Add(new SearchHit(title, link, blurb ?? ""));
        }

        log.LogDebug("Brave returned {Count} result(s) for {Query}", hits.Count, query);

        return hits;
    }

    public async Task<string> FetchAsync(string url, int maxLength, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", options.UserAgent);

        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        string? mediaType = response.Content.Headers.ContentType?.MediaType;

        if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return Truncate(content, maxLength);

        content = ScriptStyleRegex().Replace(content, " ");
        content = HtmlTagRegex().Replace(content, " ");
        content = WhitespaceRegex().Replace(content, " ").Trim();

        return Truncate(content, maxLength);
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length > maxLength ? text[..maxLength] + "…" : text;

    public static string Format(IReadOnlyList<SearchHit> hits)
    {
        if (hits.Count == 0) return "(no results)";

        StringBuilder sb = new();

        foreach (SearchHit hit in hits)
            sb.Append(hit.Title).Append(" (").Append(hit.Url).Append("): ").AppendLine(hit.Description).AppendLine();

        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</(script|style)>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
