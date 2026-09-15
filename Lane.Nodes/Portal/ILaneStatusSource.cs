namespace Lane.Nodes.Portal;

public sealed record SurfaceStatus(string Id, bool Running);

public sealed record EnergyStatus(double Fraction, string Tier, bool Asleep, long Remaining, long Budget);

/// <param name="Energy">Null when Lane does not track energy.</param>
/// <param name="ApiClients">Configured clients across every running API surface.</param>
/// <param name="DiscordChannels">Included channels across every running Discord surface; null when one of them includes
/// every channel it can see.</param>
public sealed record LaneStatus(
    IReadOnlyList<SurfaceStatus> Surfaces,
    string                       Face,
    EnergyStatus?                Energy,
    int                          ApiClients      = 0,
    int?                         DiscordChannels = 0);

/// <summary>What the portal's statistics page shows about Lane herself.</summary>
public interface ILaneStatusSource
{
    LaneStatus Current { get; }
}
