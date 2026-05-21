using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Wizard.Utility
{
    public static partial class BraveSearch
    {
        static readonly HttpClient http = new();

        [GeneratedRegex(@"<(script|style)[^>]*>.*?</(script|style)>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex ScriptStyleRegex();

        [GeneratedRegex(@"<[^>]+>")]
        private static partial Regex HtmlTagRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();

        public static async Task<string> Search(string query, int count = 5)
        {
            string apiKey = DotNetEnv.Env.GetString("BRAVE_API_KEY");

            string apiUrl = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={count}";

            using HttpRequestMessage request = new(HttpMethod.Get, apiUrl);
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("X-Subscription-Token", apiKey);

            HttpResponseMessage response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            JObject data = JObject.Parse(json);

            JArray? results = (JArray?) data["web"]?["results"];
            if(results is null || results.Count == 0) return "(no results)";

            List<string> formatted = [];

            foreach(JToken result in results.Take(count))
            {
                string? title       = (string?) result["title"];
                string? description = (string?) result["description"];
                string? url         = (string?) result["url"];

                if (title is not null && description is not null) formatted.Add($"{title} ({url}): {description}");
            }

            return string.Join("\n\n", formatted);
        }

        public static async Task<string> FetchUrl(string url, int maxLength = 3000)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (compatible; LaneBot/1.0)");

            HttpResponseMessage response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync();

            string? contentType = response.Content.Headers.ContentType?.MediaType;
            if(contentType is not null && !contentType.Contains("html")) return Truncate(content, maxLength);

            content = ScriptStyleRegex().Replace(content, " ");
            content = HtmlTagRegex().Replace(content, " ");
            content = WhitespaceRegex().Replace(content, " ").Trim();

            return Truncate(content, maxLength);
        }

        static string Truncate(string text, int maxLength) =>
            text.Length > maxLength ? text[..maxLength] + "..." : text;
    }
}
