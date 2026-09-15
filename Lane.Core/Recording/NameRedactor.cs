using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lane.Core.Identity;

namespace Lane.Core.Recording;

/// <summary>
/// Replaces people's display names and global ids with numbered placeholders (<c>[NAME_1]</c>),
/// giving every name one person goes by the same placeholder. Matches whole words, ignoring case.
/// Lane's own name is left alone.
/// </summary>
public sealed class NameRedactor
{
    private readonly Dictionary<string, string> _placeholders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Regex? _pattern;

    public NameRedactor(IEnumerable<Participant> people)
    {
        Dictionary<string, string> byPerson = new(StringComparer.OrdinalIgnoreCase);
        int count = 0;

        foreach (Participant person in people)
        {
            if (person.IsLane) continue;

            string[] aliases = [.. new[] { person.DisplayName, person.GlobalUserId }
                .Select(alias => alias?.Trim())
                .OfType<string>()
                .Where(IsRedactable)];

            if (aliases.Length == 0) continue;

            if (!byPerson.TryGetValue(person.StableKey, out string? token))
            {
                token = aliases.Select(alias => _placeholders.GetValueOrDefault(alias)).FirstOrDefault(t => t is not null)
                        ?? $"[NAME_{++count}]";

                byPerson[person.StableKey] = token;
            }

            foreach (string alias in aliases) _placeholders.TryAdd(alias, token);
        }

        if (_placeholders.Count == 0) return;

        string alternatives = string.Join('|', _placeholders.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));

        _pattern = new Regex(
            $@"(?<![\p{{L}}\p{{N}}_])(?:{alternatives})(?![\p{{L}}\p{{N}}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    public string Redact(string text) =>
        _pattern is null || text.Length == 0
            ? text
            : _pattern.Replace(text, match => _placeholders.GetValueOrDefault(match.Value, "[NAME]"));

    /// <summary>Returns a copy with every string value redacted. Property names are kept.</summary>
    public JsonNode? Redact(JsonNode? node) => node switch
    {
        JsonObject obj                                     => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Redact(p.Value)))),
        JsonArray array                                    => new JsonArray(array.Select(Redact).ToArray()),
        JsonValue value when value.TryGetValue(out string? text) => JsonValue.Create(Redact(text)),
        _                                                  => node?.DeepClone()
    };

    private static bool IsRedactable(string alias) =>
        alias.Length >= 2 && !alias.Equals("Lane", StringComparison.OrdinalIgnoreCase);
}
