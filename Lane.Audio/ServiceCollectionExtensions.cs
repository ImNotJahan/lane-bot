using Lane.Audio.Recognition;
using Lane.Audio.Synthesis;
using Lane.Core.Agent;
using Lane.Core.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Lane.Audio;

public sealed class AudioSetupOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Where Lane's voice comes from: <c>elevenlabs</c> or <c>flite</c>.
    ///
    /// A string rather than an enum bound straight from configuration, because the binder
    /// leaves an enum at its default on a typo — and silently falling back to the paid
    /// provider is the wrong direction to fail in.
    /// </summary>
    public string Provider { get; set; } = "elevenlabs";

    public AzureSpeechOptions Recognition { get; set; } = new();
    public ElevenLabsOptions  Synthesis   { get; set; } = new();
    public FliteOptions       Flite       { get; set; } = new();
    public BargeInOptions     BargeIn     { get; set; } = new();

    /// <summary>Voice, tempo and pitch. The same shape v2's speech settings had.</summary>
    public SpeechOptions Speech { get; set; } = new("MEJe6hPrI48Kt2lFuVe3");

    /// <summary>Names the bad value and the good ones — the message is the whole point.</summary>
    public SpeechProvider ResolveProvider() => Provider?.Trim().ToLowerInvariant() switch
    {
        null or "" or "elevenlabs" => SpeechProvider.ElevenLabs,
        "flite"                    => SpeechProvider.Flite,

        _ => throw new InvalidOperationException(
            $"Lane:Audio:Provider is '{Provider}'. It must be 'elevenlabs' or 'flite'."),
    };
}

public enum SpeechProvider
{
    ElevenLabs,
    Flite,
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Gives Lane ears and a voice.
    ///
    /// Everything registered here is optional: without it, sessions have no voice output,
    /// the observer factory offers nothing, and every surface works exactly as it did
    /// before audio existed.
    /// </summary>
    public static IServiceCollection AddLaneAudio(this IServiceCollection services, AudioSetupOptions options)
    {
        if (!options.Enabled) return services;

        // Read now rather than inside the factory: an unknown provider is a configuration
        // mistake, and finding out at the first clause means finding out mid-conversation.
        SpeechProvider provider = options.ResolveProvider();

        services.AddSingleton(options);
        services.AddSingleton(options.Recognition);
        services.AddSingleton(options.Synthesis);
        services.AddSingleton(options.Flite);
        services.AddSingleton(options.BargeIn);

        services.TryAddSingleton<IVoiceFloor>(sp => new VoiceFloor(
            sp.GetRequiredService<IAgentKernel>,
            options.BargeIn,
            sp.GetRequiredService<ILogger<VoiceFloor>>()));

        services.TryAddSingleton<ISpeechSynthesizer>(sp => provider switch
        {
            SpeechProvider.Flite => new FliteSynthesizer(
                options.Flite, sp.GetRequiredService<ILogger<FliteSynthesizer>>()),

            _ => new ElevenLabsSynthesizer(
                options.Synthesis, sp.GetRequiredService<ILogger<ElevenLabsSynthesizer>>()),
        });

        // A recogniser per source, not one shared: each holds a connection and a session
        // with the recognition service for as long as its speaker is talking.
        services.AddSingleton<Func<ISpeechRecognizer>>(sp => () => new AzureSpeechRecognizer(
            options.Recognition, sp.GetRequiredService<ILogger<AzureSpeechRecognizer>>()));

        services.AddSingleton(sp => new AudioRouter(
            sp.GetRequiredService<IAgentKernel>(),
            sp.GetRequiredService<Func<ISpeechRecognizer>>(),
            sp.GetRequiredService<IVoiceFloor>(),
            sp.GetRequiredService<ILogger<AudioRouter>>()));

        services.AddSingleton<IAgentObserverFactory>(sp => new VoiceObserverFactory(
            sp.GetRequiredService<ISpeechSynthesizer>(),
            options.Speech,
            sp.GetRequiredService<IVoiceFloor>(),
            sp.GetRequiredService<ILoggerFactory>()));

        return services;
    }
}
