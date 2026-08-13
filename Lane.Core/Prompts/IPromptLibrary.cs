using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Prompts;

/// <summary>
/// Loads prompt templates and fills their named placeholders.
///
/// Named rather than positional: v2 filled nine templates with <c>string.Format</c> and
/// <c>{0}</c>/<c>{1}</c> arguments, where inserting a placeholder in one file silently
/// shifted the meaning of every argument after it.
/// </summary>
public interface IPromptLibrary
{
    /// <summary>The raw template, unfilled.</summary>
    string Get(string name);

    bool Has(string name);

    /// <summary>Fills <c>{{placeholder}}</c> slots. Throws if the template wants a value it was not given.</summary>
    string Render(string name, IReadOnlyDictionary<string, string?> values);

    string Render(string name, params (string Key, string? Value)[] values);
}

public sealed class PromptOptions
{
    /// <summary>Directory of <c>*.md</c> templates, relative to the assembly if not rooted.</summary>
    public string Directory { get; set; } = "Prompts";
}

public sealed partial class FilePromptLibrary : IPromptLibrary
{
    private readonly ConcurrentDictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<FilePromptLibrary> _log;

    public FilePromptLibrary(PromptOptions options, ILogger<FilePromptLibrary> log)
    {
        _log = log;

        string directory = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(AppContext.BaseDirectory, options.Directory);

        if (!System.IO.Directory.Exists(directory))
        {
            _log.LogWarning("Prompt directory {Directory} does not exist; no templates loaded", directory);
            return;
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(directory, "*.md"))
            _templates[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);

        _log.LogInformation("Loaded {Count} prompt template(s) from {Directory}", _templates.Count, directory);
    }

    public bool Has(string name) => _templates.ContainsKey(name);

    public string Get(string name) =>
        _templates.TryGetValue(name, out string? template)
            ? template
            : throw new PromptNotFoundException(name, _templates.Keys);

    public string Render(string name, params (string Key, string? Value)[] values) =>
        Render(name, values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase));

    public string Render(string name, IReadOnlyDictionary<string, string?> values)
    {
        string template = Get(name);

        return PlaceholderPattern().Replace(template, match =>
        {
            string key = match.Groups[1].Value.Trim();

            if (values.TryGetValue(key, out string? value)) return value ?? "";

            // Loud on purpose. A silently empty slot is a prompt that quietly stopped
            // telling Lane something, which is very hard to notice from the outside.
            throw new PromptRenderException(name, key, values.Keys);
        });
    }

    [GeneratedRegex(@"\{\{([^{}]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderPattern();
}

/// <summary>Holds nothing, so callers fall back to their inline defaults.</summary>
public sealed class EmptyPromptLibrary : IPromptLibrary
{
    public bool Has(string name) => false;

    public string Get(string name) => throw new PromptNotFoundException(name, []);

    public string Render(string name, IReadOnlyDictionary<string, string?> values) => Get(name);

    public string Render(string name, params (string Key, string? Value)[] values) => Get(name);
}

public sealed class PromptNotFoundException(string name, IEnumerable<string> known)
    : Exception($"No prompt template named '{name}'. Available: {string.Join(", ", known.Order())}.");

public sealed class PromptRenderException(string template, string placeholder, IEnumerable<string> supplied)
    : Exception(
        $"Prompt '{template}' uses {{{{{placeholder}}}}} but no value was supplied. " +
        $"Supplied: {string.Join(", ", supplied.Order())}.");
