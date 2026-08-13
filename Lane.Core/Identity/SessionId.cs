using System.Diagnostics.CodeAnalysis;

namespace Lane.Core.Identity;

public enum SessionKind
{
    /// <summary>A text conversation — a Discord channel, a terminal, an API thread.</summary>
    Text,

    /// <summary>A live voice conversation.</summary>
    Voice,

    /// <summary>A programmatic caller that is not a human conversation.</summary>
    Api,

    /// <summary>Lane talking to herself. Has no surface channel attached.</summary>
    Internal
}

/// <summary>
/// The address of one conversation. Composed rather than opaque so that logs, config,
/// database rows and the monologue's <c>speak_to_session</c> tool can all name a
/// conversation in the same human-readable way.
/// </summary>
public sealed record SessionId(SurfaceId Surface, SessionKind Kind, string LocalKey)
{
    private const char Separator = '/';

    /// <summary>Canonical, stable, and safe for filesystem/DB keys: "discord.main/Text/145898...".</summary>
    public string Value => $"{Surface.Value}{Separator}{Kind}{Separator}{LocalKey}";

    public override string ToString() => Value;

    public static bool TryParse(string? value, [NotNullWhen(true)] out SessionId? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // LocalKey may itself contain separators (a Discord guild/channel pair, say),
        // so split only the first two segments and keep the remainder intact.
        int first = value.IndexOf(Separator);
        if (first <= 0) return false;

        int second = value.IndexOf(Separator, first + 1);
        if (second <= first + 1 || second == value.Length - 1) return false;

        if (!Enum.TryParse(value.AsSpan(first + 1, second - first - 1), ignoreCase: false, out SessionKind kind))
            return false;

        id = new SessionId(new SurfaceId(value[..first]), kind, value[(second + 1)..]);
        return true;
    }

    public static SessionId Parse(string value) =>
        TryParse(value, out SessionId? id) ? id : throw new FormatException($"Malformed session id '{value}'.");
}
