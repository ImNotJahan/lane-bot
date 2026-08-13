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

    public AzureSpeechOptions Recognition { get; set; } = new();
    public ElevenLabsOptions  Synthesis   { get; set; } = new();
    public BargeInOptions     BargeIn     { get; set; } = new();

    /// <summary>Voice, tempo and pitch. The same shape v2's speech settings had.</summary>
    public SpeechOptions Speech { get; set; } = new("MEJe6hPrI48Kt2lFuVe3");
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

        services.AddSingleton(options);
        services.AddSingleton(options.Recognition);
        services.AddSingleton(options.Synthesis);
        services.AddSingleton(options.BargeIn);

        services.TryAddSingleton<IVoiceFloor>(sp => new VoiceFloor(
            sp.GetRequiredService<IAgentKernel>,
            options.BargeIn,
            sp.GetRequiredService<ILogger<VoiceFloor>>()));

        services.TryAddSingleton<ISpeechSynthesizer>(sp => new ElevenLabsSynthesizer(
            options.Synthesis, sp.GetRequiredService<ILogger<ElevenLabsSynthesizer>>()));

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
