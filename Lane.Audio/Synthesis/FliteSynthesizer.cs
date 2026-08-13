using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lane.Audio.Dsp;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Lane.Audio.Synthesis;

public sealed class FliteOptions
{
    /// <summary>
    /// The flite binary. Left as a bare name it is found on PATH; a full path pins it.
    ///
    /// Flite is a native C program (https://github.com/festvox/flite) and nothing in this
    /// build installs it — `brew install flite`, `apt install flite`, or build it from the
    /// repository.
    /// </summary>
    public string ExecutablePath { get; set; } = "flite";

    /// <summary>
    /// A voice name flite knows (`flite -lv` lists them) or a path to a `.flitevox` file.
    ///
    /// Empty means flite's own default voice, which is the only one guaranteed to be in
    /// every build — naming a voice that was not compiled in fails at the first clause.
    /// </summary>
    public string? Voice { get; set; }

    /// <summary>Directory to load voice files from, for a voice that is not built in.</summary>
    public string? VoiceDirectory { get; set; }

    /// <summary>
    /// Flite's own timing knob: above 1 slower, below 1 faster. Zero leaves it alone.
    ///
    /// Worth preferring over <see cref="SpeechOptions.Tempo"/> where it will do, because
    /// flite stretches durations during synthesis while SoundTouch stretches the waveform
    /// afterwards, and the former has the phonemes to work from.
    /// </summary>
    public float DurationStretch { get; set; }

    /// <summary>Mean F0 in Hz — the pitch flite synthesises at. Zero leaves it alone.</summary>
    public int TargetMeanF0 { get; set; }

    /// <summary>Anything else to put on the command line, for flags this does not model.</summary>
    public string[] ExtraArgs { get; set; } = [];

    /// <summary>
    /// How long one clause may take before the process is killed.
    ///
    /// Flite is far faster than real time, so this is not a latency budget — it is there so
    /// a wedged child process cannot hold a session's playback chain open forever.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Frame size handed to the listener. 20 ms at the target format suits Discord.</summary>
    public TimeSpan FrameDuration { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// The rate this voice is expected to produce, for <see cref="ISpeechSynthesizer.NativeFormat"/>.
    ///
    /// Only a declaration: flite's rate is a property of the voice — 8 kHz for the built-in
    /// diphone voice, 16 kHz for the clustergen ones — so each clip is shaped from the rate
    /// in its own WAV header rather than from this.
    /// </summary>
    public int SampleRate { get; set; } = 16000;
}

/// <summary>
/// Speech from flite, the local one.
///
/// It is a child process rather than a P/Invoke: flite ships as a C library and a CLI, and
/// binding the library would mean shipping a native build per platform for a voice that is
/// a fallback. The process costs a few milliseconds against synthesis that is already many
/// times faster than real time, and a crash inside it stays inside it.
///
/// The point of having it is that it needs no key and no network. ElevenLabs sounds better
/// and always will; this one still speaks when the API is down, when nobody has paid for a
/// key, and in a test that is not allowed to reach the internet — and its first audio is
/// bounded by the machine rather than by a round trip.
///
/// Output goes to a temp file rather than a pipe because flite writes a RIFF header and
/// then seeks back to fill in the sizes, which a pipe cannot do — the header on stdout
/// claims a length of zero and every reader believes it.
/// </summary>
public sealed class FliteSynthesizer : ISpeechSynthesizer
{
    private readonly FliteOptions _options;
    private readonly ILogger<FliteSynthesizer> _log;

    private readonly SemaphoreSlim _voiceLock = new(1, 1);
    private bool _voiceChecked;

    public FliteSynthesizer(FliteOptions options, ILogger<FliteSynthesizer> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);

        _options = options;
        _log     = log;

        NativeFormat = new AudioFormat(options.SampleRate, 1, 16);
    }

    /// <summary>What the configured voice is expected to produce; the header of each clip wins.</summary>
    public AudioFormat NativeFormat { get; }

    public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(
        string text, SpeechOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        AudioFormat target = options.TargetFormat ?? AudioFormat.Pcm48kStereo;

        await EnsureTheVoiceExistsAsync(ct).ConfigureAwait(false);

        byte[] wave = await SpeakAsync(text, ct).ConfigureAwait(false);

        if (wave.Length == 0) yield break;

        (byte[] raw, AudioFormat native) = ReadWave(wave);

        if (raw.Length == 0) yield break;

        byte[] shaped = SpeechShaper.Shape(raw, native, options, target);

        int frameBytes = Math.Max(target.BytesPerFrame, target.BytesFor(_options.FrameDuration));

        foreach (byte[] frame in PcmConverter.IntoFrames(shaped, frameBytes))
        {
            ct.ThrowIfCancellationRequested();

            yield return AudioFrame.Of(frame, target);
        }
    }

    /// <summary>Runs one clause through flite and hands back the WAV file it wrote.</summary>
    private async Task<byte[]> SpeakAsync(string text, CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), $"lane-flite-{Guid.NewGuid():N}.wav");

        try
        {
            await RunAsync(BuildArguments(_options, text, path), ct).ConfigureAwait(false);

            return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : [];
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { /* it is a temp file */ }
        }
    }

    /// <summary>
    /// Refuses a voice flite does not have, once, before the first clause.
    ///
    /// Flite does not refuse it itself: an unknown <c>-voice</c> is ignored silently — no
    /// message, exit 0, and the default voice speaks instead. So a typo is not a failure,
    /// it is Lane sounding like somebody else, which is the kind of thing that gets blamed
    /// on the configuration being ignored rather than on the name being wrong.
    ///
    /// Checked lazily rather than in the constructor, so that building the container never
    /// shells out, and cached, so it costs one extra process on the first clause ever.
    /// </summary>
    private async Task EnsureTheVoiceExistsAsync(CancellationToken ct)
    {
        if (_voiceChecked || string.IsNullOrWhiteSpace(_options.Voice)) return;

        await _voiceLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_voiceChecked) return;

            // A file or a URL is flite's business to resolve, not ours to second-guess.
            if (File.Exists(_options.Voice) || _options.Voice.Contains("://"))
            {
                _voiceChecked = true;
                return;
            }

            (_, string listing, _) = await RunAsync(["-lv"], ct).ConfigureAwait(false);

            // "Voices available: kal awb_time kal16 awb rms slt"
            string[] voices = listing[(listing.IndexOf(':') + 1)..]
                .Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            if (voices.Length == 0)
            {
                // Not being able to read the list is no reason to refuse to speak.
                _log.LogDebug("flite -lv said nothing useful; the voice name was not checked");
            }
            else if (!voices.Contains(_options.Voice, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"flite has no voice called '{_options.Voice}' — it would have quietly used its default " +
                    $"instead. This build knows: {string.Join(", ", voices)}. A path to a .flitevox file " +
                     "works too.");
            }

            _voiceChecked = true;
        }
        finally { _voiceLock.Release(); }
    }

    /// <summary>One flite process, drained, deadlined and killed if it overstays.</summary>
    private async Task<(int Exit, string Out, string Error)> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadline.CancelAfter(_options.Timeout);

        ProcessStartInfo start = new()
        {
            FileName               = _options.ExecutablePath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = new() { StartInfo = start };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not run '{_options.ExecutablePath}'. Flite is a native binary that this build " +
                 "does not install: `brew install flite`, `apt install flite`, or set " +
                 "Lane:Audio:Flite:ExecutablePath to where it lives.", ex);
        }

        // Both pipes are drained while the process runs. Its diagnostics belong to the
        // logger and not to the terminal the dashboard is drawing in, and an unread pipe
        // that fills is a child that never exits.
        Task<string> errors = process.StandardError.ReadToEndAsync(deadline.Token);
        Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);

            throw new TimeoutException($"flite did not finish within {_options.Timeout}.");
        }
        catch (OperationCanceledException)
        {
            // Barge-in, almost always. Nothing is owed but a dead child process.
            Kill(process);

            throw;
        }

        string stderr = (await errors.ConfigureAwait(false)).Trim();
        string stdout = (await output.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"flite exited {process.ExitCode}: {(stderr.Length > 0 ? stderr : stdout)}");

        if (stderr.Length > 0) _log.LogDebug("flite: {Message}", stderr);

        return (process.ExitCode, stdout, stderr);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* it exited between the check and the kill */ }
    }

    /// <summary>
    /// The command line for one clause.
    ///
    /// Text goes through <c>-t</c> and audio through <c>-o</c> rather than positionally,
    /// because flite reads a bare argument as a *filename* — a clause that happened to name
    /// a file would be read instead of spoken.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(FliteOptions options, string text, string outputPath)
    {
        List<string> arguments = [];

        if (!string.IsNullOrWhiteSpace(options.VoiceDirectory))
        {
            arguments.Add("-voicedir");
            arguments.Add(options.VoiceDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.Voice))
        {
            arguments.Add("-voice");
            arguments.Add(options.Voice);
        }

        // Invariant, always: a machine whose decimal separator is a comma would otherwise
        // hand flite "duration_stretch=1,2", which it reads as 1.
        if (options.DurationStretch > 0)
        {
            arguments.Add("--setf");
            arguments.Add($"duration_stretch={options.DurationStretch.ToString(CultureInfo.InvariantCulture)}");
        }

        if (options.TargetMeanF0 > 0)
        {
            arguments.Add("--setf");
            arguments.Add($"int_f0_target_mean={options.TargetMeanF0.ToString(CultureInfo.InvariantCulture)}");
        }

        arguments.AddRange(options.ExtraArgs);

        arguments.Add("-t");
        arguments.Add(text);
        arguments.Add("-o");
        arguments.Add(outputPath);

        return arguments;
    }

    /// <summary>
    /// Reads flite's WAV back as raw PCM plus the format it is actually in.
    ///
    /// The header is believed rather than assumed: flite's rate belongs to the voice, so a
    /// build whose default voice is the 8 kHz diphone one and a 16 kHz clustergen voice both
    /// arrive here, and guessing wrong would resample by a factor of two — a chipmunk, or a
    /// drawl, depending on which way round.
    /// </summary>
    internal static (byte[] Pcm, AudioFormat Format) ReadWave(byte[] wave)
    {
        using WaveFileReader reader = new(new MemoryStream(wave));

        WaveFormat format = reader.WaveFormat;

        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16)
            throw new NotSupportedException(
                $"flite produced {format.Encoding} at {format.BitsPerSample} bits; only 16-bit PCM is supported.");

        using MemoryStream pcm = new();

        reader.CopyTo(pcm);

        return (pcm.ToArray(), new AudioFormat(format.SampleRate, format.Channels, 16));
    }
}
