using System.ComponentModel;
using Lane.Core.Tools;

namespace Lane.Tools.Web;

/// <summary>
/// Replaces v2's <c>data["search"]</c> field and the block of orchestrator code that
/// dispatched it. The loop no longer knows that searching exists.
/// </summary>
[LaneTool]
public sealed class WebSearchTool(IWebSearch search) : Tool<WebSearchTool.Args>
{
    public sealed record Args(
        [property: Description("What to search for.")] string Query,
        [property: Description("How many results to return, 1-10. Default 5.")] int Count = 5);

    protected override string Name => "web_search";

    protected override string Description =>
        "Search the web for current information. Use when you need facts you do not already know, " +
        "or anything that may have changed recently.";

    protected override TimeSpan Timeout => TimeSpan.FromSeconds(20);

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Query)) return ToolResult.Error("A search query is required.");

        IReadOnlyList<SearchHit> hits = await search
            .SearchAsync(args.Query, Math.Clamp(args.Count, 1, 10), ct)
            .ConfigureAwait(false);

        string formatted = BraveWebSearch.Format(hits);

        // Remembered globally: what Lane learned is hers, not the conversation's.
        return ToolResult.Ok(formatted)
                         .RememberAs($"[searched the web for \"{args.Query}\"]\n{formatted}", MemoryScopeHint.Global);
    }
}

/// <summary>Replaces v2's <c>data["fetch"]</c> field.</summary>
[LaneTool]
public sealed class FetchUrlTool(IWebSearch search) : Tool<FetchUrlTool.Args>
{
    public sealed record Args(
        [property: Description("The full URL to read.")] string Url,
        [property: Description("Maximum characters to return. Default 3000.")] int MaxLength = 3000);

    protected override string Name => "fetch_url";

    protected override string Description =>
        "Read the text of a web page. Use after web_search when a result looks worth reading properly.";

    protected override TimeSpan Timeout => TimeSpan.FromSeconds(20);

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!Uri.TryCreate(args.Url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ToolResult.Error($"'{args.Url}' is not an http or https URL.");

        string content = await search
            .FetchAsync(uri.ToString(), Math.Clamp(args.MaxLength, 200, 20_000), ct)
            .ConfigureAwait(false);

        return ToolResult.Ok(content)
                         .RememberAs($"[read {uri}]\n{content}", MemoryScopeHint.Global);
    }
}
