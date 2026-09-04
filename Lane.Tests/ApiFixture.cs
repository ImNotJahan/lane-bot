using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Surfaces.Api;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lane.Tests;

/// <summary>
/// A live API surface on a loopback port, wired to a scripted model.
///
/// Kestrel is real here rather than stubbed. The things worth checking at this surface —
/// that a key is required, that server-sent events actually arrive framed, that a WebSocket
/// upgrade works — are exactly the things a fake pipeline would answer for the fake.
/// </summary>
public sealed class ApiFixture : IAsyncDisposable
{
    private readonly LaneHarness  _harness;
    private readonly ApiSurface   _surface;
    private readonly List<HttpClient> _clients = [];

    /// <summary>Everything logged while the surface was up. Read when a test needs to know
    /// why a request failed, since the server answers with a status code and nothing else.</summary>
    public static System.Collections.Concurrent.ConcurrentQueue<string> Log { get; } = new();

    private ApiFixture(LaneHarness harness, ApiSurface surface)
    {
        _harness = harness;
        _surface = surface;
    }

    public LaneHarness Harness => _harness;

    public ApiSurface Surface => _surface;

    public string BaseAddress => _surface.Addresses[0];

    public static async Task<ApiFixture> StartAsync(
        ScriptedLanguageModel? model = null,
        Action<ApiSurfaceOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        string surfaceId = "api",
        bool audio = false)
    {
        LaneHarness harness = LaneHarness.Create(
            model ?? ScriptedLanguageModel.Echoing("ok"),
            configure: s =>
            {
                s.AddLaneApi();
                s.AddLogging(b => b.AddProvider(new CollectingLoggerProvider(Log)));

                if (audio) AddFakeAudio(s);

                services?.Invoke(s);
            });

        ApiSurfaceOptions options = new()
        {
            // Port 0 lets the OS choose, so tests never collide over a fixed one.
            Urls    = "http://127.0.0.1:0",
            Clients =
            [
                new ApiClientOptions { Id = "alpha", Name = "Alpha", Key = "alpha-key", CanObserveAllSessions = true },
                new ApiClientOptions { Id = "beta",  Name = "Beta",  Key = "beta-key" }
            ]
        };

        configure?.Invoke(options);

        ApiSurface surface = new(
            new SurfaceId(surfaceId),
            options,
            harness.Kernel,
            harness.Sessions,
            IdentityResolver.Empty,
            harness.Services.GetRequiredService<ITranscriptStore>(),
            harness.Services.GetRequiredService<Lane.Core.Events.IEventBus>(),
            harness.Services.GetRequiredService<Lane.Surfaces.Api.Streaming.TurnStreamHub>(),
            harness.Services.GetRequiredService<ILoggerFactory>(),
            audio ? harness.Services.GetRequiredService<Lane.Audio.AudioRouter>() : null);

        await surface.StartAsync(CancellationToken.None);

        return new ApiFixture(harness, surface);
    }

    /// <summary>An HTTP client already carrying one client app's key.</summary>
    public HttpClient Client(string? key = "alpha-key")
    {
        HttpClient client = new() { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(30) };

        if (key is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        _clients.Add(client);

        return client;
    }

    public Uri WebSocketUri(string path) =>
        new(new Uri(BaseAddress.Replace("http://", "ws://", StringComparison.Ordinal)), path);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static Task<HttpResponseMessage> SendAsync(HttpClient client, string key, object body) =>
        client.PostAsJsonAsync($"/v1/sessions/{key}/messages", body);

    /// <summary>
    /// Ears and a voice with no network behind them.
    ///
    /// Registered exactly where the real ones are, so the wiring under test is the wiring
    /// that ships — including the lazy kernel resolution the voice floor needs to avoid
    /// closing a dependency cycle through the turn pipeline.
    /// </summary>
    private static void AddFakeAudio(IServiceCollection services)
    {
        services.AddSingleton<Lane.Audio.IVoiceFloor>(sp => new Lane.Audio.VoiceFloor(
            sp.GetRequiredService<Lane.Core.Kernel.IAgentKernel>,
            new Lane.Audio.BargeInOptions { MinimumSpeakingTime = TimeSpan.Zero, MinimumWords = 2 },
            sp.GetRequiredService<ILogger<Lane.Audio.VoiceFloor>>()));

        services.AddSingleton<Lane.Audio.ISpeechSynthesizer>(new EncodingBytesSynthesizer());

        services.AddSingleton<Func<Lane.Audio.SpeakerAttribution, Lane.Audio.ISpeechRecognizer>>(
            _ => attribution => new TranscribingBytesRecognizer(attribution));

        services.AddSingleton<Lane.Audio.Voiceprints.ISpeakerAttributor>(sp =>
            new Lane.Audio.Voiceprints.LabelRosterAttributor(
                sp.GetRequiredService<Lane.Core.Identity.IIdentityResolver>(),
                sp.GetRequiredService<ILogger<Lane.Audio.Voiceprints.LabelRosterAttributor>>()));

        services.AddSingleton(sp => new Lane.Audio.AudioRouter(
            sp.GetRequiredService<Lane.Core.Kernel.IAgentKernel>(),
            sp.GetRequiredService<Func<Lane.Audio.SpeakerAttribution, Lane.Audio.ISpeechRecognizer>>(),
            sp.GetRequiredService<Lane.Audio.IVoiceFloor>(),
            sp.GetRequiredService<ILogger<Lane.Audio.AudioRouter>>(),
            sp.GetRequiredService<Lane.Audio.Voiceprints.ISpeakerAttributor>()));

        services.AddSingleton<Lane.Core.Agent.IAgentObserverFactory>(sp => new Lane.Audio.VoiceObserverFactory(
            sp.GetRequiredService<Lane.Audio.ISpeechSynthesizer>(),
            new Lane.Audio.SpeechOptions("test-voice"),
            sp.GetRequiredService<Lane.Audio.IVoiceFloor>(),
            sp.GetRequiredService<ILoggerFactory>()));
    }

    /// <summary>A logger provider that keeps what it is told, for tests to read back.</summary>
    private sealed class CollectingLoggerProvider(
        System.Collections.Concurrent.ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Collecting(categoryName, sink);

        public void Dispose() { }

        private sealed class Collecting(
            string category, System.Collections.Concurrent.ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)} {exception}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (HttpClient client in _clients) client.Dispose();

        await _surface.DisposeAsync();
        await _harness.DisposeAsync();
    }
}
