using System.Collections.Concurrent;
using Lane.Core.Identity;

namespace Lane.Core.Voice;

/// <summary>One voice channel Lane could sit in, as much of it as a surface can say.</summary>
public sealed record VoiceChannelSummary
{
    public required SurfaceId Surface { get; init; }

    /// <summary>Opaque to everything above the surface — whatever that surface joins by.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The guild or server the channel belongs to.</summary>
    public string? Space { get; init; }

    /// <summary>How many people are in it, Lane excluded.</summary>
    public int Occupants { get; init; }

    public bool Joined { get; init; }
}

public sealed record VoiceJoinResult(bool Joined, string Detail);

/// <summary>
/// A surface Lane can enter a voice channel on.
///
/// Declared in Core, and in names rather than audio, so the tools that drive it reference
/// neither Discord nor <c>Lane.Audio</c> — the same reason a session resolves its mouth by
/// capability instead of by type.
/// </summary>
public interface IVoiceChannelHost
{
    SurfaceId Surface { get; }

    ValueTask<IReadOnlyList<VoiceChannelSummary>> ListChannelsAsync(CancellationToken ct);

    ValueTask<VoiceJoinResult> JoinAsync(string channelId, CancellationToken ct);
}

/// <summary>
/// Where surfaces that carry voice announce themselves, so a tool can find them without
/// knowing which surfaces exist. A surface withdraws when it stops, which is what keeps her
/// from being offered a channel on a bot that is no longer connected.
/// </summary>
public sealed class VoiceChannelHosts
{
    private readonly ConcurrentDictionary<SurfaceId, IVoiceChannelHost> _hosts = new();

    public IReadOnlyList<IVoiceChannelHost> All => [.. _hosts.Values];

    public IDisposable Register(IVoiceChannelHost host)
    {
        _hosts[host.Surface] = host;

        return new Registration(() => _hosts.TryRemove(new KeyValuePair<SurfaceId, IVoiceChannelHost>(host.Surface, host)));
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }
}
