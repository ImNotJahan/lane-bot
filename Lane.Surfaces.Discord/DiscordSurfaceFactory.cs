using Lane.Audio;
using Lane.Core.Credits;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Memory;
using Lane.Core.Sessions;
using Lane.Core.Surfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Discord;

/// <summary>Resolves a secret reference such as <c>env:DISCORD_API_KEY</c> at composition.</summary>
public interface IDiscordTokenSource
{
    string Resolve(string reference, string instanceId);
}

public sealed class DiscordSurfaceFactory(IDiscordTokenSource tokens) : ISurfaceFactory
{
    private DiscordConsent? _consent;

    public string TypeName => "discord";

    public ISurface Create(SurfaceId id, IConfiguration options, IServiceProvider services)
    {
        DiscordSurfaceOptions bound = new();
        options.Bind(bound);

        // Resolved here, per instance. Two Discord bots are two tokens, and neither of them
        // reaches for a global to find its own.
        string token = tokens.Resolve(bound.TokenRef, id.Value);

        return new DiscordSurface(
            id,
            token,
            bound,
            services.GetRequiredService<IAgentKernel>(),
            services.GetRequiredService<ISessionRegistry>(),
            services.GetRequiredService<IIdentityResolver>(),
            services.GetRequiredService<IEventBus>(),
            services.GetRequiredService<ILogger<DiscordSurface>>(),
            _consent ??= new DiscordConsent(services.GetService<IKeyValueStore>()),
            services.GetService<AudioRouter>(),
            services.GetService<ISponsoredAccess>());
    }
}
