using Lane.Core.Energy;
using Lane.Core.Events;
using Lane.Core.Presence;
using Lane.Host.Presence;
using Lane.Nodes.Portal;
using Lane.Surfaces.Api;
using Lane.Surfaces.Discord;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Lane.Host.Web;

/// <summary>Surfaces, face and energy for the node portal. Surfaces are looked up per call, since they are hosted services themselves.</summary>
public sealed class HostLaneStatus : ILaneStatusSource, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IEnergyService   _energy;
    private readonly IDisposable      _subscription;

    private volatile string _face;

    public HostLaneStatus(IServiceProvider services, IEventBus bus, IEnergyService energy, IOptions<FaceOptions> face)
    {
        _services     = services;
        _energy       = energy;
        _face         = face.Value.DefaultEmoticon;
        _subscription = bus.Subscribe<PresenceChanged>(change => _face = change.Emoticon);
    }

    public LaneStatus Current
    {
        get
        {
            SurfaceHost[] hosts = [.. _services.GetServices<IHostedService>().OfType<SurfaceHost>()];

            SurfaceStatus[] surfaces = [.. hosts.Select(host => new SurfaceStatus(host.Id.Value, host.Running))];

            int apiClients = hosts.Select(host => host.Surface).OfType<ApiSurface>().Sum(api => api.ClientCount);

            DiscordSurface[] discord = [.. hosts.Select(host => host.Surface).OfType<DiscordSurface>()];

            int? discordChannels = discord.Any(surface => surface.IncludedChannelCount is null)
                ? null
                : discord.Sum(surface => surface.IncludedChannelCount!.Value);

            EnergyState state = _energy.Current;

            EnergyStatus? energy = _energy is BoundlessEnergy
                ? null
                : new EnergyStatus(state.Fraction, state.Tier.ToString(), state.Asleep, state.Remaining, state.Budget);

            return new LaneStatus(surfaces, _face, energy, apiClients, discordChannels);
        }
    }

    public void Dispose() => _subscription.Dispose();
}
