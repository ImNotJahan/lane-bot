using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Playback;

/// <summary>
/// Lane out of the local speakers.
///
/// A voice output like any other, so the same audio that reaches a Discord channel can be
/// heard on the machine she is running on — which is the only way to hear what a change to
/// tempo or pitch actually did.
///
/// It hands a file to the platform's own player rather than opening an audio device: NAudio
/// only has output devices on Windows, and the alternative is a native audio dependency per
/// platform to play a test clip. The player is a child process for the same reason flite is
/// — it can be missing, it can be swapped, and it cannot take the process down with it.
/// </summary>
public sealed class SystemSpeaker(AudioFormat format, ILogger logger, string? player = null) : IVoiceOutput
{
    private Process? _playing;

    public AudioFormat Format { get; } = format;

    public async Task PlayAsync(IAsyncEnumerable<AudioFrame> audio, CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), $"lane-speak-{Guid.NewGuid():N}.wav");

        try
        {
            (_, TimeSpan duration) = await WaveFile.WriteAsync(path, audio, ct).ConfigureAwait(false);

            if (duration == TimeSpan.Zero) return;

            await RunAsync(path, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { /* it is a temp file */ }
        }
    }

    public Task StopAsync()
    {
        Process? playing = Interlocked.Exchange(ref _playing, null);

        if (playing is not null) Kill(playing);

        return Task.CompletedTask;
    }

    private async Task RunAsync(string path, CancellationToken ct)
    {
        (string file, IReadOnlyList<string> arguments) = Command(path);

        ProcessStartInfo start = new()
        {
            FileName               = file,
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
                $"Could not run the audio player '{file}'. Install it, or name another one.", ex);
        }

        _playing = process;

        // Drained so a player that chats cannot fill a pipe and hang, and so its noise goes
        // to the logger rather than over whatever is drawing on the terminal.
        Task<string> errors = process.StandardError.ReadToEndAsync(ct);
        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            throw;
        }
        finally
        {
            Interlocked.CompareExchange(ref _playing, null, process);
        }

        string stderr = (await errors.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
            logger.LogWarning(
                "{Player} exited {Code}: {Message}", file, process.ExitCode,
                stderr.Length > 0 ? stderr : (await output.ConfigureAwait(false)).Trim());

        else if (stderr.Length > 0) logger.LogDebug("{Player}: {Message}", file, stderr);
    }

    /// <summary>
    /// Whatever this machine plays audio with.
    ///
    /// Named explicitly per platform rather than guessed, because the failure of guessing
    /// wrong is silence, which is indistinguishable from synthesis having produced nothing.
    /// </summary>
    internal (string File, IReadOnlyList<string> Arguments) Command(string path)
    {
        if (!string.IsNullOrWhiteSpace(player)) return (player, [path]);

        if (OperatingSystem.IsMacOS()) return ("afplay", [path]);

        if (OperatingSystem.IsWindows())
            return ("powershell", [
                "-NoProfile", "-Command",
                $"(New-Object Media.SoundPlayer '{path.Replace("'", "''")}').PlaySync()"]);

        foreach (string candidate in (string[])["paplay", "aplay", "play"])
            if (OnPath(candidate)) return (candidate, [path]);

        if (OnPath("ffplay")) return ("ffplay", ["-nodisp", "-autoexit", "-loglevel", "quiet", path]);

        throw new InvalidOperationException(
            "No audio player was found. Install alsa-utils (aplay) or pulseaudio-utils (paplay), " +
            "or name one to use.");
    }

    private static bool OnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(directory => File.Exists(Path.Combine(directory, executable)));

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { /* already gone */ }
    }
}
