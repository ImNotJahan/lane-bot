using Lane.Audio;
using Lane.Core.Agent;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Memory;
using Lane.Core.Sessions;
using Lane.Core.Surfaces;
using Lane.Surfaces.Api.Streaming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Api;

/// <summary>Resolves a client key reference. Implemented by the host, so this project never
/// learns where secrets live.</summary>
public interface IApiKeySource
{
    string Resolve(string reference, string clientId);
}

public sealed class ApiSurfaceFactory(IApiKeySource keys) : ISurfaceFactory
{
    public string TypeName => "api";

    public ISurface Create(SurfaceId id, IConfiguration options, IServiceProvider services)
    {
        ApiSurfaceOptions bound = new();
        options.Bind(bound);

        // Resolved here, at composition, exactly as every other credential is: a part that
        // fetches its own can only ever hold one set of them.
        foreach (ApiClientOptions client in bound.Clients)
        {
            if (string.IsNullOrWhiteSpace(client.KeyRef)) continue;

            client.Key = keys.Resolve(client.KeyRef, client.Id);
        }

        return new ApiSurface(
            id,
            bound,
            services.GetRequiredService<IAgentKernel>(),
            services.GetRequiredService<ISessionRegistry>(),
            services.GetService<IIdentityResolver>() ?? IdentityResolver.Empty,
            services.GetService<ITranscriptStore>() ?? new NullTranscriptStore(),
            services.GetRequiredService<IEventBus>(),
            services.GetRequiredService<TurnStreamHub>(),
            services.GetRequiredService<ILoggerFactory>(),
            services.GetService<AudioRouter>());
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the half of the API that has to exist before any request arrives.
    ///
    /// The stream hub and its observer live in the kernel's container rather than the
    /// surface's, because the turn pipeline is built at startup and a request that wants to
    /// watch a turn arrives long afterwards. Registering them costs nothing when no client
    /// ever connects: the observer factory returns null for every turn nobody is watching,
    /// and the turn runs exactly as it did before this surface existed.
    /// </summary>
    public static IServiceCollection AddLaneApi(this IServiceCollection services)
    {
        services.AddSingleton(sp => new TurnStreamHub(sp.GetService<IEventBus>()));
        services.AddSingleton<IAgentObserverFactory>(sp =>
            new ApiStreamObserverFactory(sp.GetRequiredService<TurnStreamHub>()));

        return services;
    }
}
