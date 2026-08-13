using Lane.Core;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lane.Testing;

/// <summary>
/// A fully wired kernel with a scripted model and recording channels — the fixture the
/// cohesion and concurrency tests drive.
/// </summary>
public sealed class LaneHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly Dictionary<string, RecordingChannel> _channels = [];
    private readonly List<IDisposable> _attachments = [];

    private readonly bool _ownsServices;

    private LaneHarness(ServiceProvider services, ScriptedLanguageModel model, bool ownsServices = true)
    {
        _services      = services;
        Model          = model;
        _ownsServices  = ownsServices;
    }

    /// <summary>
    /// Wraps a container the caller built and still owns. For tests that need services the
    /// standard harness does not register — the monologue, for one.
    /// </summary>
    public static LaneHarness Wrap(ServiceProvider services, ScriptedLanguageModel model) =>
        new(services, model, ownsServices: false);

    public ScriptedLanguageModel Model  { get; }
    public IAgentKernel     Kernel      => _services.GetRequiredService<IAgentKernel>();
    public ISessionRegistry Sessions    => _services.GetRequiredService<ISessionRegistry>();

    /// <summary>For tests that need at services the harness does not surface directly.</summary>
    public IServiceProvider Services => _services;

    public static LaneHarness Create(
        ScriptedLanguageModel? model = null,
        Action<IServiceCollection>? configure = null,
        Action<SessionOptions>? sessionOptions = null)
    {
        ScriptedLanguageModel scripted = model ?? ScriptedLanguageModel.Echoing("ok");

        ServiceCollection services = new();

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddLaneCore();

        // Off unless a test asks for it. The gate costs an extra model call per turn, and
        // most tests here count calls as a proxy for turns taken.
        services.Configure<Lane.Core.Pipeline.Stages.ResponsePolicyOptions>(o => o.Enabled = false);

        // No batching delay by default: tests assert ordering and isolation, not debounce
        // behaviour, and a real window would just make every test slower.
        services.Configure<SessionOptions>(o =>
        {
            o.BatchWindow      = TimeSpan.Zero;
            o.VoiceBatchWindow = TimeSpan.Zero;
            sessionOptions?.Invoke(o);
        });

        services.AddSingleton<ILanguageModel>(scripted);
        services.AddSingleton<ILanguageModelRegistry>(sp => new LanguageModelRegistry(
            sp.GetServices<ILanguageModel>(),
            new Dictionary<string, string>
            {
                ["respond"]   = scripted.Descriptor.InstanceId,
                ["monologue"] = scripted.Descriptor.InstanceId,
                ["routing"]   = scripted.Descriptor.InstanceId,
                ["summarize"] = scripted.Descriptor.InstanceId
            }));

        configure?.Invoke(services);

        return new LaneHarness(services.BuildServiceProvider(), scripted);
    }

    /// <summary>Opens a session and attaches a recorder to it.</summary>
    public RecordingChannel OpenSession(
        string surface,
        string localKey,
        SessionKind kind = SessionKind.Text,
        string? memoryGroup = null,
        ChannelCapabilities capabilities = ChannelCapabilities.Text)
    {
        SessionId id = new(new SurfaceId(surface), kind, localKey);

        Sessions.GetOrCreate(new SessionDescriptor
        {
            Id           = id,
            DisplayName  = localKey,
            MemoryGroup  = memoryGroup ?? $"{surface}/{localKey}",
            Capabilities = capabilities
        });

        RecordingChannel channel = new(id, capabilities);

        _attachments.Add(Sessions.Attach(channel));
        _channels[id.Value] = channel;

        return channel;
    }

    public RecordingChannel Channel(string surface, string localKey, SessionKind kind = SessionKind.Text) =>
        _channels[new SessionId(new SurfaceId(surface), kind, localKey).Value];

    /// <summary>Submits a message as if a surface had heard it.</summary>
    public ValueTask SendAsync(
        RecordingChannel channel,
        string speaker,
        string text,
        string? externalId = null,
        CancellationToken ct = default)
    {
        Participant author = new(new ParticipantId(channel.Surface, speaker), speaker);

        LaneMessage message = LaneMessage.User(channel.Id, author, text, DateTimeOffset.UtcNow, externalId);

        return Kernel.SubmitAsync(new InboundEvent
        {
            Session    = channel.Id,
            Author     = author,
            Message    = message,
            ExternalId = externalId
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IDisposable attachment in _attachments) attachment.Dispose();

        if (_ownsServices) await _services.DisposeAsync().ConfigureAwait(false);
    }
}
