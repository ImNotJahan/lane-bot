namespace Lane.Core.Identity;

/// <summary>
/// Identifies one configured surface *instance* — not a surface type. Two Discord bots
/// running side by side are "discord.main" and "discord.alt", and every session,
/// participant and audio source they produce is namespaced under that id.
/// </summary>
public readonly record struct SurfaceId(string Value)
{
    public override string ToString() => Value;

    public static implicit operator string(SurfaceId id) => id.Value;
}
