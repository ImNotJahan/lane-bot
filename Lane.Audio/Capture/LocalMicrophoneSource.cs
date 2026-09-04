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

    private Process? _capture;
    private Task? _pump;

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

        _capture = process;
        _pump    = Task.Run(() => PumpAsync(process, file, _stop.Token), CancellationToken.None);

        _log.LogInformation("Listening to the local microphone through {Command}", file);
    }

    /// <summary>
    /// Reads raw PCM off the capture program's output for as long as it runs.
    ///
    /// Fixed-size reads rather than whole frames: a pipe hands over whatever has arrived,
    /// and a partial read that was treated as a frame would halve the sample it split.
    /// </summary>
    private async Task PumpAsync(Process process, string command, CancellationToken ct)
    {
        // Drained so a chatty program cannot fill its error pipe and hang, and so its noise
        // goes to the logger rather than over whatever is drawing on the terminal.
        Task<string> errors = process.StandardError.ReadToEndAsync(ct);

        byte[] buffer = new byte[ReadSize + 1];

        // A pipe hands over whatever has arrived, which need not be a whole number of
        // samples. The odd byte is carried into the next read rather than dropped: dropping
        // one does not lose a sample, it shifts the split between every pair of bytes after
        // it, and the whole rest of the stream decodes as noise.
        int carry = 0;

        try
        {
            Stream audio = process.StandardOutput.BaseStream;

            while (!ct.IsCancellationRequested)
            {
                int read = await audio.ReadAsync(buffer.AsMemory(carry, ReadSize), ct).ConfigureAwait(false);

                if (read == 0) break;

                int available = carry + read;
                int whole     = available - available % Format.BytesPerFrame;

                if (whole > 0) _frames.Writer.TryWrite(AudioFrame.Of(buffer[..whole], Format));

                carry = available - whole;

                if (carry > 0) buffer[0] = buffer[whole];
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reading from the local microphone failed");
        }
        finally
        {
            _frames.Writer.TryComplete();
        }

        if (ct.IsCancellationRequested) return;

        // Getting here means the capture program stopped on its own, which is the case worth
        // being loud about: the room goes quiet and nothing else would say why.
        string stderr = (await errors.ConfigureAwait(false)).Trim();

        _log.LogError("The microphone capture program {Command} stopped{Detail}",
            command, stderr.Length > 0 ? $": {stderr}" : "");
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

        if (_capture is { } process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { /* already gone */ }

            process.Dispose();
        }

        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { /* shutting down */ }
        }

        _frames.Writer.TryComplete();
        _stop.Dispose();
    }
}
