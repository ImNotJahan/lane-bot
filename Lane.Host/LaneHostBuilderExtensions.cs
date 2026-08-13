using Lane.Core;
using Lane.Audio;
using Lane.Core.Agent;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Monologue;
using Lane.Core.Pipeline.Stages;
using Lane.Core.Prompts;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Core.Surfaces;
using Lane.Host.Configuration;
using Lane.Host.Logging;
using Lane.Host.Presence;
using Lane.Host.Ui;
using Lane.Memory;
using Lane.Memory.Sqlite;
using Lane.Tools;
using Lane.Tools.Mcp;
using Lane.Providers.Anthropic;
using Lane.Providers.OpenAi;
using Lane.Surfaces.Api;
using Lane.Surfaces.Discord;
using Lane.Surfaces.Terminal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Host;

public static class LaneHostBuilderExtensions
{
    /// <summary>
    /// Wires the kernel, then instantiates every configured model and surface instance.
    ///
    /// Note what is absent: no static settings singleton, no "which body am I" switch. The
    /// host reads instance arrays and constructs each one through a keyed factory, so
    /// adding a second Discord bot is a config entry rather than a code change.
    /// </summary>
    public static IHostApplicationBuilder AddLane(
        this IHostApplicationBuilder builder, bool useDashboard, BufferedLogSink logs)
    {
        IConfigurationSection section = builder.Configuration.GetSection(LaneOptions.SectionName);

        builder.Services.AddLaneCore();
        builder.Services.AddSingleton<ISecretResolver, SecretResolver>();

        builder.Services.Configure<SessionOptions>(section.GetSection("Sessions"));
        builder.Services.Configure<AgentOptions>(section.GetSection("Agent"));
        builder.Services.Configure<ResponsePolicyOptions>(section.GetSection("ResponsePolicy"));

        PromptOptions prompts = new();
        section.GetSection("Prompts").Bind(prompts);
        builder.Services.AddLanePrompts(prompts);

        RegisterIdentities(builder.Services, section.GetSection("Identities"));
        RegisterMonologue(builder.Services, section.GetSection("Monologue"));
        RegisterFace(builder.Services, section.GetSection("Face"));
        RegisterAudio(builder.Services, section.GetSection("Audio"));
        RegisterMemory(builder.Services, section.GetSection("Memory"));
        RegisterTools(builder.Services, section.GetSection("Tools"));
        RegisterSurfaceFactories(builder.Services);
        RegisterModels(builder.Services, section.GetSection("Models"));
        RegisterSurfaces(builder.Services, section.GetSection("Surfaces"));

        if (useDashboard) RegisterDashboard(builder.Services, section, logs);

        return builder;
    }

    private static void RegisterDashboard(
        IServiceCollection services, IConfigurationSection section, BufferedLogSink logs)
    {
        services.AddSingleton(logs);

        // The chat pane stands in for the console, so it needs to introduce the person at
        // the keyboard the same way the terminal surface would have.
        string userName = section.GetSection("Surfaces").GetChildren()
            .FirstOrDefault(s => string.Equals(s["Type"], "terminal", StringComparison.OrdinalIgnoreCase))
            ?["Options:UserName"] ?? "you";

        services.AddSingleton(new ChatView(userName));
        services.AddSingleton<ITerminalIo>(sp => new ChatTerminalIo(sp.GetRequiredService<ChatView>()));

        services.AddSingleton<IHostedService>(sp => new TuiHost(
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<ISessionRegistry>(),
            sp.GetRequiredService<ILanguageModelRegistry>(),
            logs,
            sp.GetRequiredService<IMonologueScheduler>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<ILogger<TuiHost>>(),
            sp.GetRequiredService<ChatView>()));
    }

    /// <summary>
    /// Fails fast when a role that will be asked to call tools is bound to a model that
    /// cannot call them. With no prompt-JSON fallback, silence here would mean discovering
    /// it mid-conversation.
    /// </summary>
    public static IHost ValidateLane(this IHost host)
    {
        ILanguageModelRegistry registry = host.Services.GetRequiredService<ILanguageModelRegistry>();

        if (registry is LanguageModelRegistry concrete)
            concrete.ValidateCapabilities([ModelRole.Respond, ModelRole.Monologue]);

        return host;
    }

    /// <summary>
    /// Links one person's accounts across surfaces, so User-scoped memory follows them from
    /// Discord to the terminal to the API rather than fragmenting per surface.
    /// </summary>
    private static void RegisterIdentities(IServiceCollection services, IConfigurationSection section)
    {
        Dictionary<string, IReadOnlyList<string>> identities = [];

        foreach (IConfigurationSection person in section.GetChildren())
        {
            string[] accounts = [.. person.GetChildren().Select(c => c.Value).OfType<string>()];

            if (accounts.Length > 0) identities[person.Key] = accounts;
        }

        if (identities.Count == 0) return;

        services.AddSingleton<IIdentityResolver>(sp =>
            new IdentityResolver(identities, sp.GetService<ILogger<IdentityResolver>>()));
    }

    private static void RegisterMonologue(IServiceCollection services, IConfigurationSection section)
    {
        MonologueOptions options = new();
        section.Bind(options);

        if (!options.Enabled) return;

        services.AddLaneMonologue(o =>
        {
            o.Enabled         = options.Enabled;
            o.Interval        = options.Interval;
            o.AfterMessage    = options.AfterMessage;
            o.MinInterval     = options.MinInterval;
            o.MaxInterval     = options.MaxInterval;
            o.StartupDelay    = options.StartupDelay;
            o.Prompt          = options.Prompt;
            o.MaxOutputTokens = options.MaxOutputTokens;
        });
    }

    private static void RegisterFace(IServiceCollection services, IConfigurationSection section)
    {
        services.Configure<FaceOptions>(section);
        services.AddHostedService<FaceServer>();
    }

    private static void RegisterAudio(IServiceCollection services, IConfigurationSection section)
    {
        AudioSetupOptions options = new();
        section.Bind(options);

        if (!options.Enabled) return;

        // Resolved at composition, as with every other credential — a part that fetches its
        // own can only ever have one set.
        SecretResolver secrets = new();

        options.Recognition.Key    = secrets.Resolve(section["Recognition:KeyRef"]) ?? options.Recognition.Key;
        options.Recognition.Region = secrets.Resolve(section["Recognition:RegionRef"]) ?? options.Recognition.Region;
        options.Synthesis.ApiKey   = secrets.Resolve(section["Synthesis:KeyRef"]) ?? options.Synthesis.ApiKey;

        if (string.IsNullOrWhiteSpace(options.Recognition.Key) ||
            string.IsNullOrWhiteSpace(options.Synthesis.ApiKey))
        {
            throw new InvalidOperationException(
                "Lane:Audio is enabled but a speech key is missing. Set Recognition:KeyRef, " +
                "Recognition:RegionRef and Synthesis:KeyRef, or turn Audio off.");
        }

        services.AddLaneAudio(options);
    }

    private static void RegisterMemory(IServiceCollection services, IConfigurationSection section)
    {
        SqliteOptions sqlite = new();
        section.GetSection("Sqlite").Bind(sqlite);

        MemoryOptions memory = new();
        section.Bind(memory);

        // Handlers bind by hand because Scope and Slot are enums the binder would happily
        // leave at their defaults on a typo — and a handler that silently becomes Session
        // scope instead of Global is the kind of bug you notice months later.
        memory.Handlers = [.. section.GetSection("Handlers").GetChildren().Select(BindHandler)];

        if (memory.Handlers.Count == 0)
            throw new InvalidOperationException(
                "Lane:Memory:Handlers is empty. Lane would not remember anything between turns.");

        services.AddLaneMemory(sqlite, o =>
        {
            o.Handlers      = memory.Handlers;
            o.IdleEviction  = memory.IdleEviction;
            o.FlushInterval = memory.FlushInterval;
            o.MaxInstances  = memory.MaxInstances;
        });
    }

    private static void RegisterTools(IServiceCollection services, IConfigurationSection section)
    {
        services.Configure<ToolOptions>(section);

        ToolsSetupOptions setup = new();
        section.GetSection("Books").Bind(setup.Books);
        section.GetSection("Search").Bind(setup.Search);

        // Resolved here, at composition, rather than read from inside the tool. A part that
        // fetches its own credentials can only ever have one set of them — which is exactly
        // why v2's speech synthesiser could never hold two voices.
        setup.Search.ApiKey = new SecretResolver().Resolve(section["Search:KeyRef"]);

        services.AddLaneBuiltinTools(setup);

        RegisterMcp(services, section.GetSection("Mcp"));
    }

    /// <summary>
    /// Connects the MCP servers named in configuration, and only those.
    ///
    /// There is deliberately no discovery. Connecting to a server means running code it
    /// controls and putting text it wrote into Lane's prompt, which should be something
    /// somebody chose rather than something that happened.
    /// </summary>
    private static void RegisterMcp(IServiceCollection services, IConfigurationSection section)
    {
        if (!section.Exists()) return;

        McpOptions options = new();
        section.Bind(options);

        // Servers are bound by hand for the same reason memory handlers are: Transport is an
        // enum the binder would leave at its default on a typo, and a server silently
        // falling back to stdio when it was meant to be http fails in a confusing way.
        options.Servers = [.. section.GetSection("Servers").GetChildren().Select(BindMcpServer)];

        if (options.Servers.Count(s => s.Enabled) == 0) return;

        SecretResolver secrets = new();

        // Environment values and headers are resolved once, here, so a server can be handed
        // a token without that token living in the config file.
        foreach (McpServerOptions server in options.Servers)
        {
            foreach (string key in server.Env.Keys.ToList())
                server.Env[key] = secrets.Resolve(server.Env[key]) ?? server.Env[key];

            foreach (string key in server.Headers.Keys.ToList())
                server.Headers[key] = secrets.Resolve(server.Headers[key]) ?? server.Headers[key];
        }

        services.AddLaneMcp(options);
    }

    private static McpServerOptions BindMcpServer(IConfigurationSection section)
    {
        McpServerOptions server = new();
        section.Bind(server);

        server.Id = section["Id"] ?? throw new InvalidOperationException(
            $"An MCP server at {section.Path} is missing its Id.");

        string? transport = section["Transport"];

        if (!string.IsNullOrWhiteSpace(transport))
        {
            if (!Enum.TryParse(transport, ignoreCase: true, out McpTransport parsed))
                throw new InvalidOperationException(
                    $"MCP server '{server.Id}' has Transport='{transport}'. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames<McpTransport>())}.");

            server.Transport = parsed;
        }

        return server;
    }

    private static MemoryHandlerOptions BindHandler(IConfigurationSection section)
    {
        string id = section["Id"] ?? throw new InvalidOperationException(
            $"A memory handler at {section.Path} is missing its Id.");

        string type = section["Type"] ?? throw new InvalidOperationException(
            $"Memory handler '{id}' is missing its Type.");

        return new MemoryHandlerOptions
        {
            Id                 = id,
            Type               = type,
            Scope              = ParseEnum<MemoryScope>(section["Scope"], id, nameof(MemoryHandlerOptions.Scope)),
            Slot               = ParseEnum<MemorySlot>(section["Slot"], id, nameof(MemoryHandlerOptions.Slot)),
            Order              = section.GetValue("Order", 0),
            SectionTitle       = section["SectionTitle"],
            Pinned             = section.GetValue("Pinned", false),
            DirectSessionsOnly = section.GetValue("DirectSessionsOnly", false),
            MaxMessages        = section.GetValue("MaxMessages", 20),
            MaxEntries         = section.GetValue("MaxEntries", 20),
            PromptName         = section["PromptName"] ?? "",
            Kinds              = section.GetSection("Kinds").Get<MessageKind[]>()
        };
    }

    private static T ParseEnum<T>(string? value, string handlerId, string field) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Memory handler '{handlerId}' is missing {field}.");

        if (!Enum.TryParse(value, ignoreCase: true, out T parsed))
            throw new InvalidOperationException(
                $"Memory handler '{handlerId}' has {field}='{value}'. " +
                $"Valid values: {string.Join(", ", Enum.GetNames<T>())}.");

        return parsed;
    }

    private static void RegisterSurfaceFactories(IServiceCollection services)
    {
        // Keyed by config discriminator, so config can name the same type twice with
        // different ids and options.
        services.AddKeyedSingleton<ISurfaceFactory, TerminalSurfaceFactory>("terminal");

        services.AddSingleton<IDiscordTokenSource, SecretTokenSource>();
        services.AddKeyedSingleton<ISurfaceFactory>("discord",
            (sp, _) => new DiscordSurfaceFactory(sp.GetRequiredService<IDiscordTokenSource>()));

        // The streaming half of the API has to be registered whether or not an API surface
        // is configured: the turn pipeline is built at startup, and the observer that feeds
        // a client's stream is part of it. It offers nothing to turns nobody is watching.
        services.AddLaneApi();

        services.AddSingleton<IApiKeySource, SecretApiKeySource>();
        services.AddKeyedSingleton<ISurfaceFactory>("api",
            (sp, _) => new ApiSurfaceFactory(sp.GetRequiredService<IApiKeySource>()));
    }

    /// <summary>Bridges the Discord factory to the host's secret resolution.</summary>
    private sealed class SecretTokenSource(ISecretResolver secrets) : IDiscordTokenSource
    {
        public string Resolve(string reference, string instanceId) =>
            secrets.Require(reference, $"Discord surface '{instanceId}'");
    }

    /// <summary>The same, for API client keys.</summary>
    private sealed class SecretApiKeySource(ISecretResolver secrets) : IApiKeySource
    {
        public string Resolve(string reference, string clientId) =>
            secrets.Require(reference, $"API client '{clientId}'");
    }

    private static void RegisterModels(IServiceCollection services, IConfigurationSection section)
    {
        ModelsOptions options = new();
        section.Bind(options);

        if (options.Instances.Count == 0)
            throw new InvalidOperationException(
                "Lane:Models:Instances is empty. At least one model instance must be configured.");

        foreach (ModelInstanceOptions instance in options.Instances)
        {
            ModelInstanceOptions captured = instance;

            services.AddSingleton<ILanguageModel>(sp => CreateModel(captured, sp));
        }

        Dictionary<(string, string), string> overrides = options.Overrides
            .ToDictionary(o => (o.Surface, o.Role), o => o.Instance);

        services.AddSingleton<ILanguageModelRegistry>(sp => new LanguageModelRegistry(
            sp.GetServices<ILanguageModel>(),
            options.Roles,
            overrides,
            sp.GetService<ILogger<LanguageModelRegistry>>()));
    }

    private static readonly Dictionary<string, string> KnownEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openrouter"] = "https://openrouter.ai/api/v1",
        ["deepseek"]   = "https://api.deepseek.com/v1"
    };

    private static ILanguageModel CreateModel(ModelInstanceOptions options, IServiceProvider sp)
    {
        ISecretResolver secrets = sp.GetRequiredService<ISecretResolver>();
        IEventBus       bus     = sp.GetRequiredService<IEventBus>();

        string key = secrets.Require(options.KeyRef, $"model instance '{options.Id}'");

        ILanguageModel model = options.Provider.ToLowerInvariant() switch
        {
            "anthropic" => new AnthropicModel(
                new AnthropicModelOptions { InstanceId = options.Id, Model = options.Model, ApiKey = key },
                sp.GetRequiredService<ILogger<AnthropicModel>>()),

            // One adapter serves every OpenAI-compatible endpoint; only the URL differs.
            "openrouter" or "deepseek" or "openai-compatible" => new OpenAiCompatibleModel(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient($"model:{options.Id}"),
                new OpenAiCompatibleOptions
                {
                    InstanceId   = options.Id,
                    Model        = options.Model,
                    ApiKey       = key,
                    Endpoint     = ResolveEndpoint(options),
                    Capabilities = ParseCapabilities(options)
                },
                sp.GetRequiredService<ILogger<OpenAiCompatibleModel>>()),

            _ => throw new InvalidOperationException(
                $"Model instance '{options.Id}' names unknown provider '{options.Provider}'. " +
                "Known providers: anthropic, openrouter, deepseek, openai-compatible.")
        };

        // Wrapped so every provider reports usage without knowing the bus exists. No role
        // is attached here: one instance can serve several roles, and the dashboard reads
        // the bindings from the registry anyway.
        return new TelemetryLanguageModel(model, bus);
    }

    private static string ResolveEndpoint(ModelInstanceOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Endpoint)) return options.Endpoint;

        if (KnownEndpoints.TryGetValue(options.Provider, out string? known)) return known;

        throw new InvalidOperationException(
            $"Model instance '{options.Id}' uses provider '{options.Provider}' and needs an explicit Endpoint.");
    }

    /// <summary>
    /// Capabilities are declared per instance because OpenRouter fronts hundreds of models
    /// whose support differs completely. Defaults assume tool calling; a model without it
    /// must say so, and the registry then refuses to bind it to a tool-using role.
    /// </summary>
    private static ModelCapabilities ParseCapabilities(ModelInstanceOptions options)
    {
        if (options.Capabilities is not { Count: > 0 }) return ModelCapabilities.Tools |
                                                               ModelCapabilities.Streaming |
                                                               ModelCapabilities.StopSequences;

        ModelCapabilities parsed = ModelCapabilities.None;

        foreach (string name in options.Capabilities)
        {
            if (!Enum.TryParse(name, ignoreCase: true, out ModelCapabilities one))
                throw new InvalidOperationException(
                    $"Model instance '{options.Id}' declares unknown capability '{name}'. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames<ModelCapabilities>())}.");

            parsed |= one;
        }

        return parsed;
    }


    private static void RegisterSurfaces(IServiceCollection services, IConfigurationSection section)
    {
        List<(SurfaceInstanceOptions Instance, IConfigurationSection Options)> configured = [];

        foreach (IConfigurationSection child in section.GetChildren())
        {
            SurfaceInstanceOptions instance = new();
            child.Bind(instance);

            // Options is passed through as raw configuration; only the factory knows its shape.
            configured.Add((instance, child.GetSection("Options")));
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach ((SurfaceInstanceOptions instance, IConfigurationSection surfaceOptions) in configured)
        {
            if (!instance.Enabled) continue;

            if (string.IsNullOrWhiteSpace(instance.Id))
                throw new InvalidOperationException($"A '{instance.Type}' surface is missing its Id.");

            if (!seen.Add(instance.Id))
                throw new InvalidOperationException(
                    $"Two surfaces share the id '{instance.Id}'. Ids namespace sessions and must be unique.");

            SurfaceInstanceOptions  captured        = instance;
            IConfigurationSection   capturedOptions = surfaceOptions;

            // The surface is built lazily inside its host, so a bad token or endpoint costs
            // that one surface rather than aborting startup for every other one.
            services.AddSingleton<IHostedService>(sp => new SurfaceHost(
                () =>
                {
                    ISurfaceFactory factory =
                        sp.GetKeyedService<ISurfaceFactory>(captured.Type)
                        ?? throw new InvalidOperationException(
                            $"No surface factory registered for type '{captured.Type}' (instance '{captured.Id}').");

                    return factory.Create(new SurfaceId(captured.Id), capturedOptions, sp);
                },
                new SurfaceId(captured.Id),
                sp.GetRequiredService<ILogger<SurfaceHost>>()));
        }

        if (seen.Count == 0)
            throw new InvalidOperationException("No surfaces are enabled; Lane would have no way to be reached.");
    }
}
