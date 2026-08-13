using System.Text;

namespace Lane.Tools.Mcp;

/// <summary>
/// Turning what a server calls its tools into something safe to put in a prompt.
///
/// Everything here treats the server as untrusted. Not because MCP servers are usually
/// hostile, but because a tool name and description are third-party text that Lane hands
/// verbatim to the model, and the failure mode — the model following an instruction that
/// arrived inside a tool description — is invisible in the logs and looks like Lane
/// misbehaving.
/// </summary>
public static class McpNaming
{
    public const string Prefix = "mcp__";

    /// <summary>Server ids appear in tool names, so they get the same alphabet as the names do.</summary>
    public static bool IsValidServerId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 32 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary><c>mcp__{server}__{tool}</c>, with the tool name reduced to a safe alphabet.</summary>
    public static string Qualify(string serverId, string toolName) =>
        $"{Prefix}{serverId}__{Sanitize(toolName)}";

    /// <summary>
    /// Lowercase letters, digits and underscores only.
    ///
    /// Providers require a restricted alphabet anyway, but the reason to be strict is that a
    /// name is one of the few pieces of server-controlled text the model sees. A name
    /// carrying newlines or punctuation is a name that can be made to look like something
    /// other than a name.
    /// </summary>
    public static string Sanitize(string name)
    {
        StringBuilder clean = new(name.Length);

        bool lastWasSeparator = false;

        foreach (char c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                clean.Append(char.ToLowerInvariant(c));
                lastWasSeparator = false;
                continue;
            }

            // Runs of punctuation collapse rather than becoming runs of underscores.
            if (lastWasSeparator || clean.Length == 0) continue;

            clean.Append('_');
            lastWasSeparator = true;
        }

        while (clean.Length > 0 && clean[^1] == '_') clean.Length--;

        // Long names cost tokens on every single request, since the tool list is sent each time.
        if (clean.Length > 48) clean.Length = 48;

        return clean.Length == 0 ? "tool" : clean.ToString();
    }

    /// <summary>
    /// Strips what a description has no business containing and caps its length.
    ///
    /// Control characters and zero-width marks are removed because their only use in a tool
    /// description is to make the text render as something other than what the model reads.
    /// The cap bounds both the token cost and how much room an unhelpful description has.
    /// </summary>
    public static string CleanDescription(string? description, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(description)) return "(no description provided)";

        StringBuilder clean = new(Math.Min(description.Length, maxLength + 16));

        foreach (char c in description)
        {
            if (c is '\n' or '\r' or '\t')
            {
                // Newlines survive as spaces: a description is one field, and a multi-line
                // one can be made to look like the end of the tool list.
                if (clean.Length > 0 && clean[^1] != ' ') clean.Append(' ');
                continue;
            }

            if (char.IsControl(c)) continue;

            // Zero-width and bidirectional-override characters: invisible on screen, but not
            // to the model.
            if (c is '​' or '‌' or '‍' or '﻿' or '⁠' ||
                c is >= '‪' and <= '‮' ||
                c is >= '⁦' and <= '⁩') continue;

            clean.Append(c);
        }

        string text = clean.ToString().Trim();

        if (text.Length == 0) return "(no description provided)";

        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";
    }

    /// <summary>Glob matching for the allow and deny lists, on the server's own tool names.</summary>
    public static bool Matches(string name, string pattern)
    {
        if (pattern == "*") return true;

        if (pattern.EndsWith('*'))
            return name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);

        return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
