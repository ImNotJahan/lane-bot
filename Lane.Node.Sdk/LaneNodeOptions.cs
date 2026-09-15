using Lane.Core.Models;

namespace Lane.Node.Sdk;

public sealed class LaneNodeOptions
{
    /// <summary>Lane's node listener, e.g. <c>ws://lane.local:5070</c>. http and https are accepted too.</summary>
    public required string LaneUrl { get; init; }

    /// <summary>What the node answers with. Reported to Lane for display only.</summary>
    public required string Model { get; init; }

    public string Pool { get; init; } = "default";

    public string Name { get; init; } = Environment.MachineName;

    public ModelCapabilities Capabilities { get; init; } =
        ModelCapabilities.Tools | ModelCapabilities.Streaming | ModelCapabilities.StopSequences;

    public int MaxConcurrency { get; init; } = 4;

    public TimeSpan ReconnectMin { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMax { get; init; } = TimeSpan.FromSeconds(30);
}
