using System.Reflection;
using Lane.Core.Agent;
using Lane.Core.Context;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Presence;
using Lane.Core.Kernel;
using Lane.Core.Memory;
using Lane.Core.Monologue;
using Lane.Core.Pipeline;
using Lane.Core.Pipeline.Stages;
using Lane.Core.Prompts;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the kernel, the session layer, and the default turn pipeline.
    ///
    /// Stage order is registration order, and each stage does its own work then calls the
    /// next — so the chain below reads top to bottom: record what arrived, ask memory what
    /// it knows, produce a reply, commit it, send it. Inserting a moderation or
    /// rate-limiting stage means adding one line here.
    ///
    /// Memory and prompts get no-op implementations by default; <c>AddLaneMemory</c> and
    /// <c>AddLanePrompts</c> replace them.
    /// </summary>
    public static IServiceCollection AddLaneCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEventBus, EventBus>();
        services.TryAddSingleton<IIdentityResolver>(IdentityResolver.Empty);
        services.TryAddSingleton<IIdentityDirectory>(NullIdentityDirectory.Instance);
        services.TryAddSingleton<IPresenceSink, EventBusPresenceSink>();
        services.TryAddSingleton<ITranscriptFormatter, TranscriptFormatter>();

        services.AddOptions<SessionOptions>();
        services.AddOptions<AgentOptions>();
        services.AddOptions<ResponsePolicyOptions>();

        services.TryAddSingleton(sp =>
            new TurnBudget(sp.GetRequiredService<IOptions<SessionOptions>>().Value.MaxConcurrentTurns));

        services.TryAddSingleton<ITurnExecutor, TurnPipeline>();
        services.TryAddSingleton<SessionRegistry>();
        services.TryAddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<SessionRegistry>());
        services.TryAddSingleton<IAgentKernel, AgentKernel>();

        services.TryAddSingleton<IMemoryService, NullMemoryService>();
        services.TryAddSingleton<IStateStore, NullStateStore>();
        services.TryAddSingleton<ITranscriptStore, NullTranscriptStore>();
        services.TryAddSingleton<IPromptLibrary, EmptyPromptLibrary>();

        services.AddOptions<ToolOptions>();
        services.TryAddSingleton<IMonologueScheduler, InertMonologueScheduler>();
        services.TryAddSingleton<IToolSource, DiToolSource>();
        services.TryAddSingleton<IToolRegistry, ToolRegistry>();
        services.TryAddSingleton<AgentLoop>();

        services.AddSingleton<ITurnStage, IngestStage>();
        services.AddSingleton<ITurnStage, ContextAssemblyStage>();

        // After assembly, because deciding whether a message was meant for Lane needs the
        // conversation around it; before the model turn, because the whole point is to not
        // pay for that turn.
        services.AddSingleton<ITurnStage, ResponsePolicyStage>();
        services.AddSingleton<ITurnStage, ModelTurnStage>();
        services.AddSingleton<ITurnStage, PersistStage>();
        services.AddSingleton<ITurnStage, DeliveryStage>();

        return services;
    }

    /// <summary>
    /// Registers every <c>[LaneTool]</c> class in an assembly.
    ///
    /// This is the whole cost of giving Lane a new ability: write the class, and it is
    /// discovered here. Compare v2, where teaching her to read a book meant editing the
    /// orchestrator, the monologue prompt and the settings model together.
    /// </summary>
    public static IServiceCollection AddLaneTools(this IServiceCollection services, Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(ITool).IsAssignableFrom(type)) continue;
            if (type.GetCustomAttribute<LaneToolAttribute>() is null) continue;

            services.AddSingleton(typeof(ITool), type);
        }

        return services;
    }

    /// <summary>
    /// Starts Lane's inner life: one global loop that can volunteer a remark into a named
    /// conversation. Off by default, because a harness that talks unprompted the moment it
    /// starts is a surprising default.
    /// </summary>
    public static IServiceCollection AddLaneMonologue(
        this IServiceCollection services, Action<MonologueOptions>? configure = null)
    {
        if (configure is not null) services.Configure(configure);

        services.RemoveAll<IMonologueScheduler>();

        services.AddSingleton<MonologueService>();
        services.AddSingleton<IMonologueScheduler>(sp => sp.GetRequiredService<MonologueService>());
        services.AddHostedService(sp => sp.GetRequiredService<MonologueService>());

        return services;
    }

    /// <summary>Loads prompt templates from disk.</summary>
    public static IServiceCollection AddLanePrompts(this IServiceCollection services, PromptOptions options)
    {
        services.RemoveAll<IPromptLibrary>();
        services.AddSingleton<IPromptLibrary>(sp =>
            new FilePromptLibrary(options, sp.GetRequiredService<ILogger<FilePromptLibrary>>()));

        return services;
    }
}
