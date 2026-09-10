using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Capture;

public sealed class MicrophoneOptions
{
    /// <summary>Off by default: opening a microphone is not something to start doing unasked.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Which input, in whatever form the capture command names them — <c>:0</c> for the
    /// default on macOS, <c>default</c> on Linux, a device name on Windows.
    /// </summary>
    public string Device { get; set; } = "";

    /// <summary>The capture program. Empty picks one for the platform.</summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Arguments, when the defaults do not suit the machine. Whatever is named here must
    /// write signed 16-bit little-endian PCM at 16 kHz mono to standard output, because
    /// nothing downstream inspects it — it is read as exactly that.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; set; } = [];

    /// <summary>Which conversation the room is talking to.</summary>
    public string Session { get; set; } = "room";

    /// <summary>
    /// Also mirror what is said into the dashboard, when one is running.
    ///
    /// Off, the room is a spoken conversation and nothing else — which is the point, but
    /// also means the only record of it while it happens is the transcript in the database.
    /// On, the same exchange scrolls past in the conversation pane, which is the difference
    /// between watching her mishear somebody and finding out about it tomorrow.
    /// </summary>
    public bool ShowInTui { get; set; }

    /// <summary>
    /// How loud the room has to be before she listens to it at all.
    ///
    /// Off, a microphone left open in a house hears the next room, the television and the
    /// phone call upstairs, and answers all of them. See <see cref="NoiseGate"/>.
    /// </summary>
    public NoiseGateOptions NoiseGate { get; set; } = new();

    /// <summary>
    /// Ignore the room while Lane is speaking into it.
    ///
    /// A microphone and a speaker in one room hear each other. Off, she interrupts herself
    /// on her own first clause and the rest of her sentence comes back as something the room
    /// said — see <see cref="EchoGate"/>. On, nobody can interrupt her through this
    /// microphone either, which is the price of not having acoustic echo cancellation.
    /// </summary>
    public bool SuppressEcho { get; set; } = true;

    /// <summary>
    /// How long after she stops talking to keep ignoring the room.
    ///
    /// Covers the sound still on its way out of the speakers when playback ends, and the
    /// audio already captured but not yet read. Too short and the last syllable of her own
    /// sentence gets through; too long and the first word of the reply to it does not.
    /// </summary>
    public TimeSpan EchoTail { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long to wait before starting the capture program again after it stops, doubling
    /// on each consecutive failure up to <see cref="MaxRestartDelay"/>.
    ///
    /// A program that exits the instant it starts — a device that is named wrong, or gone —
    /// would otherwise be respawned in a tight loop for the rest of the process's life.
    /// </summary>
    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRestartDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the capture program may hand over nothing at all before it is assumed wedged
    /// and restarted. Zero waits forever.
    ///
    /// This is not a check for a quiet room. A capture program sends silence as zero samples
    /// at the sample rate, so a stream that stops arriving entirely has not gone quiet — the
    /// device underneath it has gone away without telling the process reading it, which is
    /// what a sleeping machine or a re-enumerated USB microphone does.
    /// </summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>What the speakers are handed. Playback quality, not recognition quality.</summary>
    public int OutputSampleRate { get; set; } = 48000;

    public int OutputChannels { get; set; } = 2;

    /// <summary>The playback program. Empty picks one for the platform, as ever.</summary>
    public string Player { get; set; } = "";
}

/// <summary>
/// The machine's own microphone, read from a capture program rather than an audio device.
///
/// The same choice, and the same reasoning, as the local speaker beside it: NAudio has
/// capture devices on Windows only, and the alternative is a native audio dependency per
/// platform. A child process is portable, can be swapped for one that suits an odd sound
/// setup, and cannot take Lane down when it fails — losing the microphone should cost the
/// microphone.
///
/// It is registered as a shared source, because the whole reason to point a microphone at a
/// room rather than wear one is that there is more than one person in the room.
/// </summary>
public sealed class LocalMicrophoneSource : IAudioSource
{
    /// <summary>A tenth of a second: small enough to stay responsive, large enough not to churn.</summary>
    private const int ReadSize = 3200;

    private readonly Channel<AudioFrame> _frames = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,

            // Audio is only useful live. Falling behind should lose the oldest of it rather
            // than build a backlog that puts recognition further behind the conversation.
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly MicrophoneOptions _options;
    private readonly ILogger _log;

    private readonly CancellationTokenSource _stop = new();

    private volatile Process? _capture;
    private Task? _supervisor;

    public LocalMicrophoneSource(AudioSourceId id, MicrophoneOptions options, ILogger log)
    {
        Id       = id;
        _options = options;
        _log     = log;
    }

    public AudioSourceId Id { get; }

    public AudioFormat Format => AudioFormat.Pcm16kMono;

    /// <summary>Null on purpose: who is on this microphone is the point, and it is not known here.</summary>
    public string? SpeakerHint => null;

    public void Start()
    {
        // The first launch happens here rather than in the supervisor so that a capture
        // program which is simply not installed is a startup error somebody sees, instead of
        // a retry loop running quietly behind a room that never works.
        Process process = Launch();

        _supervisor = Task.Run(() => SuperviseAsync(process, _stop.Token), CancellationToken.None);

        _log.LogInformation("Listening to the local microphone through {Command}", Executable());
    }

    private Process Launch()
    {
        (string file, IReadOnlyList<string> arguments) = Command();

        ProcessStartInfo start = new()
        {
            FileName               = file,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        Process process = new() { StartInfo = start };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not run the capture program '{file}'. Install it, or name another one in " +
                "Lane:Audio:Microphone:Command.", ex);
        }

        return process;
    }

    /// <summary>
    /// Keeps a capture program running for as long as anything is listening to this source.
    ///
    /// A microphone is not a thing that ends, but the program reading it is a child process,
    /// and that ends for reasons that have nothing to do with the conversation: the machine
    /// sleeps, the interface is unplugged, the sound stack is reconfigured underneath it. The
    /// source deliberately outlives all of it — the frame channel stays open across restarts,
    /// so the recogniser, both gates and the router never learn that the microphone went away
    /// and never have to be rebuilt to bring it back.
    ///
    /// Without this the first such exit was permanent, and it presented as a room that had
    /// simply stopped being answered.
    /// </summary>
    private async Task SuperviseAsync(Process? started, CancellationToken ct)
    {
        int failures = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Process? process = started;

                started = null;

                try
                {
                    process ??= Launch();

                    _capture = process;

                    // A run that produced audio was a working microphone, whatever ended it,
                    // so the backoff starts again from the beginning rather than punishing it
                    // for however many times it failed hours ago.
                    if (await RunAsync(process, ct).ConfigureAwait(false)) failures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // First at Error so a device named wrong is noticed, the rest at Warning
                    // so a microphone that is not there does not fill the log all day.
                    _log.Log(failures == 0 ? LogLevel.Error : LogLevel.Warning, ex,
                        "The local microphone could not be opened (attempt {Attempt})", failures + 1);
                }
                finally
                {
                    _capture = null;

                    if (process is not null) { Stop(process); process.Dispose(); }
                }

                failures++;

                if (ct.IsCancellationRequested) break;

                TimeSpan wait = Backoff(failures);

                _log.LogInformation("Reopening the local microphone in {Delay:0.#}s", wait.TotalSeconds);

                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            // Only here: completing the channel is what tells everything downstream the
            // microphone is gone for good, and until the source is disposed it is not.
            _frames.Writer.TryComplete();
        }
    }

    private TimeSpan Backoff(int failures)
    {
        if (failures <= 0) return _options.RestartDelay;

        double seconds = _options.RestartDelay.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 10));

        seconds = Math.Min(seconds, _options.MaxRestartDelay.TotalSeconds);

        // Jitter, so several sources that lost the same sound stack at the same moment do not
        // come back for it in lockstep.
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    /// <summary>
    /// Reads raw PCM off one capture program's output for as long as it runs, and says
    /// whether it produced any audio at all before it stopped.
    ///
    /// Fixed-size reads rather than whole frames: a pipe hands over whatever has arrived,
    /// and a partial read that was treated as a frame would halve the sample it split.
    /// </summary>
    private async Task<bool> RunAsync(Process process, CancellationToken ct)
    {
        // Drained so a chatty program cannot fill its error pipe and hang, and so its noise
        // goes to the logger rather than over whatever is drawing on the terminal.
        Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Per run, not per source: a read abandoned at the stall timeout may still be on its
        // way into this buffer while the next run is already filling one.
        byte[] buffer = new byte[ReadSize + 1];

        // A pipe hands over whatever has arrived, which need not be a whole number of
        // samples. The odd byte is carried into the next read rather than dropped: dropping
        // one does not lose a sample, it shifts the split between every pair of bytes after
        // it, and the whole rest of the stream decodes as noise.
        int carry = 0;

        bool heard   = false;
        bool stalled = false;

        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            Stream audio = process.StandardOutput.BaseStream;

            while (true)
            {
                // Re-armed before every read, so it measures the gap between reads rather
                // than the age of the connection.
                if (_options.StallTimeout > TimeSpan.Zero) stall.CancelAfter(_options.StallTimeout);

                int read = await audio
                    .ReadAsync(buffer.AsMemory(carry, ReadSize), stall.Token)
                    .ConfigureAwait(false);

                if (read == 0) break;

                heard = true;

                int available = carry + read;
                int whole     = available - available % Format.BytesPerFrame;

                if (whole > 0) _frames.Writer.TryWrite(AudioFrame.Of(buffer[..whole], Format));

                carry = available - whole;

                if (carry > 0) buffer[0] = buffer[whole];
            }
        }
        catch (OperationCanceledException)
        {
            stalled = !ct.IsCancellationRequested;
        }
        finally
        {
            // Killed rather than left to exit: on the stall path the program is still running
            // and still holding the device, and the drain below only ends once it is gone.
            Stop(process);
        }

        string said = await SaidAsync(errors).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return heard;

        if (stalled)
            _log.LogWarning(
                "The local microphone handed over nothing for {Timeout:0.#}s, which a working one never does{Detail}",
                _options.StallTimeout.TotalSeconds, Detail(said));
        else
            _log.LogWarning("The microphone capture program {Command} stopped{Detail}",
                Executable(), Detail(said));

        return heard;
    }

    private static string Detail(string said) => said.Length > 0 ? $": {said}" : "";

    /// <summary>Whatever the capture program said on its way out, or nothing if it would not say.</summary>
    private static async Task<string> SaidAsync(Task<string> errors)
    {
        try { return (await errors.ConfigureAwait(false)).Trim(); }
        catch (Exception) { return ""; }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone, or already disposed */ }
    }

    /// <summary>
    /// How this machine captures audio.
    ///
    /// Named per platform rather than guessed, because guessing wrong produces silence,
    /// which is indistinguishable from a room where nobody is talking.
    /// </summary>
    internal (string File, IReadOnlyList<string> Arguments) Command()
    {
        if (_options.Arguments.Count > 0)
            return (Executable(), _options.Arguments);

        string device = _options.Device;

        (string format, string fallback) = true switch
        {
            _ when OperatingSystem.IsMacOS()   => ("avfoundation", ":0"),
            _ when OperatingSystem.IsWindows() => ("dshow", "audio=Microphone"),
            _                                  => ("alsa", "default"),
        };

        if (string.IsNullOrWhiteSpace(device)) device = fallback;

        return (Executable(), [
            "-hide_banner", "-loglevel", "error",
            "-f", format,
            "-i", device,
            "-ac", "1",
            "-ar", Format.SampleRate.ToString(),
            "-f", "s16le",
            "-"]);
    }

    private string Executable() =>
        string.IsNullOrWhiteSpace(_options.Command) ? "ffmpeg" : _options.Command;

    public IAsyncEnumerable<AudioFrame> ReadAsync(CancellationToken ct) => _frames.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        // Killed as well as cancelled: a read pending on a pipe does not always come back
        // for the token, and closing the far end of it is what makes it return.
        if (_capture is { } process) Stop(process);

        if (_supervisor is not null)
        {
            try { await _supervisor.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { /* shutting down */ }
        }

        _frames.Writer.TryComplete();
        _stop.Dispose();
    }
}
