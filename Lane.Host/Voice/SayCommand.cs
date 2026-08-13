using System.Diagnostics;
using System.Globalization;
using Lane.Audio;
using Lane.Audio.Playback;
using Lane.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lane.Host.Voice;

/// <summary>
/// `lane say "…"` — one line, out of the speakers, and nothing else.
///
/// It builds no host. Hearing what a tempo change did should not need a model key, a
/// Discord token, a database or a dashboard, and the round trip of starting all of that to
/// judge a voice is exactly what stops anyone from trying a second setting.
///
/// What it does share is the composition: the synthesiser comes out of
/// <see cref="Lane.Audio.ServiceCollectionExtensions.AddLaneAudio"/> from the same
/// configuration the running bot reads, so what you hear here is what a channel hears. The
/// overrides are on top of that, never instead of it.
/// </summary>
public static class SayCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Any(a => a is "--help" or "-h")) { Usage(Console.Out); return 0; }

        string text;
        IReadOnlyDictionary<string, string> flags;

        try
        {
            (text, flags) = Parse(args);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (text.Length == 0) { Usage(Console.Error); return 1; }

        try
        {
            return await SpeakAsync(text, flags, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            // The interesting failures here — no flite on PATH, no player, a voice that was
            // not compiled in — all carry their own instructions. A stack trace would bury them.
            Console.Error.WriteLine(ex.Message);

            return 1;
        }
    }

    /// <summary>
    /// Everything that is not a flag is the line to speak.
    ///
    /// Which means quoting is optional — `lane say hello there` works — and that a flag's
    /// value is taken positionally rather than by looking at whether it starts with a dash,
    /// so `--pitch -2.5` is a pitch and not a missing value followed by a mystery flag.
    /// </summary>
    internal static (string Text, IReadOnlyDictionary<string, string> Flags) Parse(string[] args)
    {
        Dictionary<string, string> flags = [];
        List<string> words = [];

        // args[0] is "say".
        for (int i = 1; i < args.Length; i++)
        {
            string argument = args[i];

            if (argument.Length == 0) continue;

            if (argument[0] != '-') { words.Add(argument); continue; }

            string name = argument.TrimStart('-').ToLowerInvariant();

            if (name is "verbose") { flags[name] = "true"; continue; }

            if (i + 1 >= args.Length) throw new InvalidOperationException($"--{name} needs a value.");

            flags[name] = args[++i];
        }

        return (string.Join(' ', words), flags);
    }

    private static async Task<int> SpeakAsync(
        string text, IReadOnlyDictionary<string, string> flags, CancellationToken ct)
    {
        IConfigurationSection section = new ConfigurationBuilder()
            .AddLaneSources(reloadOnChange: false)
            .Build()
            .GetSection("Lane:Audio");

        AudioSetupOptions options = new();

        section.Bind(options);

        // On, whatever the file says: trying the voice before turning voice on is the
        // normal order to do things in.
        options.Enabled = true;

        options.Synthesis.ApiKey = new SecretResolver().Resolve(section["Synthesis:KeyRef"]) ?? options.Synthesis.ApiKey;

        if (flags.TryGetValue("provider", out string? provider)) options.Provider = provider;

        SpeechProvider chosen = options.ResolveProvider();

        // One --voice flag, put wherever that provider keeps its voice: a flite voice name
        // and an ElevenLabs voice id are the same idea and nobody wants two flags for it.
        if (flags.TryGetValue("voice", out string? voice))
        {
            if (chosen is SpeechProvider.Flite) options.Flite.Voice = voice;
            else options.Speech = options.Speech with { VoiceId = voice };
        }

        if (Number(flags, "stretch") is { } stretch) options.Flite.DurationStretch = stretch;
        if (Whole(flags, "f0") is { } f0) options.Flite.TargetMeanF0 = f0;

        AudioFormat target = new(
            Whole(flags, "sample-rate") ?? AudioFormat.Pcm48kStereo.SampleRate,
            Whole(flags, "channels") ?? AudioFormat.Pcm48kStereo.Channels,
            16);

        SpeechOptions speech = options.Speech with { TargetFormat = target };

        if (Number(flags, "tempo") is { } tempo) speech = speech with { Tempo = tempo };
        if (Number(flags, "pitch") is { } pitch) speech = speech with { Pitch = pitch };
        if (Number(flags, "rate")  is { } rate)  speech = speech with { Rate  = rate };

        ServiceCollection services = new();

        services.AddLogging(b => b
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(flags.ContainsKey("verbose") ? LogLevel.Debug : LogLevel.Warning));

        services.AddLaneAudio(options);

        ServiceProvider container = services.BuildServiceProvider();

        await using (container.ConfigureAwait(false))
        {
            ISpeechSynthesizer synthesizer = container.GetRequiredService<ISpeechSynthesizer>();

            Console.Error.WriteLine(
                $"{chosen.ToString().ToLowerInvariant()} · " +
                $"{(chosen is SpeechProvider.Flite ? Named(options.Flite.Voice) : options.Speech.VoiceId)} · " +
                $"{target} · tempo {speech.Tempo:0.##} pitch {speech.Pitch:0.##}");

            Stopwatch clock = Stopwatch.StartNew();

            List<AudioFrame> frames = [];

            await foreach (AudioFrame frame in synthesizer.SynthesizeAsync(text, speech, ct).ConfigureAwait(false))
                frames.Add(frame);

            TimeSpan spent = clock.Elapsed;

            if (frames.Count == 0)
            {
                Console.Error.WriteLine("Nothing was synthesised.");
                return 1;
            }

            TimeSpan spoken = target.DurationOf(frames.Sum(f => f.Pcm.Length));

            Console.Error.WriteLine(
                $"{spoken.TotalSeconds:0.00}s of audio in {spent.TotalMilliseconds:0}ms " +
                $"({spoken.TotalMilliseconds / Math.Max(spent.TotalMilliseconds, 1):0.#}× real time)");

            if (flags.TryGetValue("out", out string? path))
            {
                await WaveFile.WriteAsync(path, Replay(frames), ct).ConfigureAwait(false);

                Console.Error.WriteLine($"wrote {Path.GetFullPath(path)}");

                return 0;
            }

            SystemSpeaker speaker = new(
                target,
                container.GetRequiredService<ILoggerFactory>().CreateLogger("Lane.Audio.Playback"),
                flags.GetValueOrDefault("player"));

            await speaker.PlayAsync(Replay(frames), ct).ConfigureAwait(false);

            return 0;
        }
    }

    /// <summary>Frames are already in hand — a test clip is judged whole, not streamed.</summary>
    private static async IAsyncEnumerable<AudioFrame> Replay(IEnumerable<AudioFrame> frames)
    {
        foreach (AudioFrame frame in frames) yield return frame;

        await Task.CompletedTask;
    }

    private static string Named(string? voice) => string.IsNullOrWhiteSpace(voice) ? "default voice" : voice;

    private static float? Number(IReadOnlyDictionary<string, string> flags, string name) =>
        flags.TryGetValue(name, out string? raw)
            ? float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : throw new InvalidOperationException($"--{name} wants a number, not '{raw}'.")
            : null;

    private static int? Whole(IReadOnlyDictionary<string, string> flags, string name) =>
        flags.TryGetValue(name, out string? raw)
            ? int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : throw new InvalidOperationException($"--{name} wants a whole number, not '{raw}'.")
            : null;

    private static void Usage(TextWriter to) => to.WriteLine(
        """
        Speaks one line through Lane's own voice, then exits.

          lane say <text> [options]

          --provider <name>    elevenlabs | flite, overriding Lane:Audio:Provider
          --voice <name>       flite voice name (flite -lv) or ElevenLabs voice id
          --tempo <percent>    speed change, 0 leaves it alone
          --pitch <semitones>  pitch change, 0 leaves it alone
          --rate <percent>     rate change, 0 leaves it alone
          --stretch <factor>   flite's own duration stretch: above 1 slower, below 1 faster
          --f0 <hz>            flite's own mean pitch
          --sample-rate <hz>   output rate, default 48000
          --channels <1|2>     output channels, default 2
          --out <file.wav>     write it to a file instead of playing it
          --player <command>   play with this instead of the platform's usual one
          --verbose            log at debug

        Examples:
          lane say "Tide pools are full of things worth looking at."
          lane say "Same line, slower." --stretch 1.2 --pitch -1
          lane say "Compare to this." --provider elevenlabs --out /tmp/lane.wav
        """);
}
