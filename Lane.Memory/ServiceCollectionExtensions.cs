using Lane.Core.Context;
using Lane.Core.Credits;
using Lane.Core.Forum;
using Lane.Core.Nodes;
using Lane.Core.Memory;
using Lane.Memory.Handlers;
using Lane.Memory.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Memory;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the no-op memory services from <c>AddLaneCore</c> with SQLite-backed ones.
    ///
    /// Handler types register keyed by their config discriminator, so a new kind of memory
    /// is a factory registration and a config entry — nothing in the kernel changes.
    /// </summary>
    public static IServiceCollection AddLaneMemory(
        this IServiceCollection services,
        SqliteOptions sqlite,
        Action<MemoryOptions>? configure = null)
    {
        services.AddSingleton(sp => new LaneDatabase(sqlite, sp.GetRequiredService<ILogger<LaneDatabase>>()));

        services.RemoveAll<IStateStore>();
        services.RemoveAll<ITranscriptStore>();
        services.RemoveAll<IMemoryService>();

        services.AddSingleton<IStateStore, SqliteStateStore>();
        services.AddSingleton<ITranscriptStore, SqliteTranscriptStore>();
        services.AddSingleton<IKeyValueStore, SqliteKeyValueStore>();
        services.AddSingleton<INodeDirectory, SqliteNodeDirectory>();
        services.AddSingleton<ICreditLedger, SqliteCreditLedger>();
        services.AddSingleton<ISponsorships>(sp => new SqliteSponsorships(sp.GetRequiredService<LaneDatabase>()));
        services.AddSingleton<IForum>(sp => new SqliteForum(sp.GetRequiredService<LaneDatabase>()));

        services.AddSingleton<IMemoryHandlerFactory, SlidingWindowFactory>();
        services.AddSingleton<IMemoryHandlerFactory, SummaryFactory>();
        services.AddSingleton<IMemoryHandlerFactory, ProfileFactory>();

        if (configure is not null) services.Configure(configure);

        services.AddSingleton(sp => new MemoryHandlerPool(
            sp.GetServices<IMemoryHandlerFactory>(),
            sp.GetRequiredService<IStateStore>(),
            sp,
            sp.GetRequiredService<IOptions<MemoryOptions>>().Value,
            sp.GetRequiredService<ILogger<MemoryHandlerPool>>()));

        services.AddSingleton<IMemoryService>(sp => new MemoryService(
            sp.GetRequiredService<MemoryHandlerPool>(),
            sp.GetRequiredService<ITranscriptFormatter>(),
            sp.GetRequiredService<ILogger<MemoryService>>()));

        services.AddHostedService<MemoryFlushService>();

        return services;
    }
}
