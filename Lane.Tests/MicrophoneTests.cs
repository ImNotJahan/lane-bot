using Lane.Audio;
using Lane.Audio.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The machine's own microphone, read from a capture program rather than an audio device —
/// the same choice the local speaker makes, and for the same reason: NAudio has capture
/// devices on Windows only.
/// </summary>
public sealed class MicrophoneTests
{
    private static LocalMicrophoneSource Source(MicrophoneOptions options) =>
        new(new AudioSourceId("microphone/room"), options, NullLogger.Instance);

    [Fact]
    public void The_capture_command_asks_for_exactly_what_recognition_wants()
    {
        // Nothing downstream inspects this stream — it is read as raw 16 kHz mono PCM16 —
        // so the command has to be the thing that guarantees it.
        (string file, IReadOnlyList<string> arguments) = Source(new MicrophoneOptions()).Command();

        Assert.Equal("ffmpeg", file);

        string line = string.Join(" ", arguments);

        Assert.Contains("-ac 1", line);
        Assert.Contains("-ar 16000", line);
        Assert.Contains("-f s16le", line);
        Assert.EndsWith("-", line);
    }

    [Fact]
    public void The_platform_picks_the_input_layer_and_a_default_device()
    {
        // Guessing wrong produces silence, which is indistinguishable from a room where
        // nobody is talking — so each platform is named rather than probed.
        (_, IReadOnlyList<string> arguments) = Source(new MicrophoneOptions()).Command();

        string line = string.Join(" ", arguments);

        if (OperatingSystem.IsMacOS()) Assert.Contains("-f avfoundation -i :0", line);
        else if (OperatingSystem.IsWindows()) Assert.Contains("-f dshow", line);
        else Assert.Contains("-f alsa -i default", line);
    }

    [Fact]
    public void A_named_device_is_used_instead_of_the_default()
    {
        (_, IReadOnlyList<string> arguments) = Source(new MicrophoneOptions { Device = ":2" }).Command();

        Assert.Contains(":2", arguments);
    }

    [Fact]
    public void An_odd_sound_setup_can_replace_the_command_outright()
    {
        // A missing or differently-named capture program should be a configuration change
        // rather than a code change.
        MicrophoneOptions options = new()
        {
            Command   = "parec",
            Arguments = ["--format=s16le", "--rate=16000", "--channels=1"]
        };

        (string file, IReadOnlyList<string> arguments) = Source(options).Command();

        Assert.Equal("parec", file);
        Assert.Equal(options.Arguments, arguments);
    }

    [Fact]
    public async Task A_capture_program_that_does_not_exist_names_itself()
    {
        // The failure a person actually hits, and the message has to say which program to
        // install rather than surfacing a Win32 error code.
        await using LocalMicrophoneSource source = Source(new MicrophoneOptions
        {
            Command   = "lane-not-a-real-capture-program",
            Arguments = ["--nothing"]
        });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(source.Start);

        Assert.Contains("lane-not-a-real-capture-program", ex.Message);
        Assert.Contains("Lane:Audio:Microphone:Command", ex.Message);
    }

    /// <summary>
    /// Runs a shell command as the capture program and returns the first <paramref
    /// name="wanted"/> bytes heard.
    ///
    /// Read to a count rather than to the end of the stream, because the source deliberately
    /// has no end: a capture program that exits is restarted rather than being the last word.
    /// </summary>
    private static async Task<byte[]> CaptureAsync(string script, int wanted, MicrophoneOptions? options = null)
    {
        options ??= new MicrophoneOptions();

        options.Command   = "sh";
        options.Arguments = ["-c", script];

        await using LocalMicrophoneSource source = Source(options);

        source.Start();

        List<byte> heard = [];

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        try
        {
            await foreach (AudioFrame frame in source.ReadAsync(timeout.Token))
            {
                Assert.Equal(AudioFormat.Pcm16kMono, frame.Format);

                // Never half a sample: everything downstream reads these in pairs.
                Assert.Equal(0, frame.Pcm.Length % 2);

                heard.AddRange(frame.Pcm.ToArray());

                if (heard.Count >= wanted) break;
            }
        }
        catch (OperationCanceledException) { /* leaves whatever arrived, and the assert says so */ }

        return [.. heard];
    }

    [Fact]
    public async Task Audio_arrives_as_whole_samples_at_the_rate_recognition_wants()
    {
        Assert.Equal("abcdefghij"u8.ToArray(), await CaptureAsync("printf 'abcdefghij'", 10));
    }

    [Fact]
    public async Task A_read_that_splits_a_sample_does_not_shift_everything_after_it()
    {
        // The failure this guards against is not losing a sample, it is losing the *phase*:
        // drop one odd byte and every pair after it is split down the middle, so the whole
        // rest of the stream decodes as noise. Two odd-length writes with a pause between
        // them make the pipe hand over a half sample, which is what a real microphone does
        // constantly.
        byte[] heard = await CaptureAsync("printf 'abcde'; sleep 0.2; printf 'fghij'", 10);

        Assert.Equal("abcdefghij"u8.ToArray(), heard);
    }

    [Fact]
    public async Task A_capture_program_that_stops_is_started_again()
    {
        // The failure that prompted this: a capture program exits hours in — the machine
        // slept, the interface was unplugged — and the room simply stops being answered, with
        // one line in a log nobody is reading to say why.
        byte[] heard = await CaptureAsync("printf 'abcdefghij'", 20, new MicrophoneOptions
        {
            RestartDelay = TimeSpan.FromMilliseconds(10)
        });

        Assert.Equal("abcdefghijabcdefghij"u8.ToArray(), heard);
    }

    [Fact]
    public async Task A_capture_program_that_goes_silent_without_exiting_is_restarted()
    {
        // The nastier half of the same failure: the program is alive and the pipe is open, so
        // nothing looks wrong, but the device behind it has gone away and no sample will ever
        // arrive again. A working microphone sends silence as zero samples, so a stream that
        // stops arriving at all is never just a quiet room.
        byte[] heard = await CaptureAsync("printf 'abcdefghij'; sleep 30", 20, new MicrophoneOptions
        {
            RestartDelay = TimeSpan.FromMilliseconds(10),
            StallTimeout = TimeSpan.FromMilliseconds(300)
        });

        Assert.Equal("abcdefghijabcdefghij"u8.ToArray(), heard);
    }

    [Fact]
    public async Task Restarting_backs_off_so_a_device_that_is_gone_is_not_hammered()
    {
        // A program that exits the instant it starts would otherwise be respawned in a tight
        // loop for the rest of the process's life.
        MicrophoneOptions options = new()
        {
            RestartDelay    = TimeSpan.FromMilliseconds(200),
            MaxRestartDelay = TimeSpan.FromSeconds(1)
        };

        long start = Environment.TickCount64;

        // Three runs means two waits, and the second is twice the first.
        byte[] heard = await CaptureAsync("printf 'ab'", 6, options);

        Assert.Equal("ababab"u8.ToArray(), heard);

        // 200ms + 400ms, less the jitter floor of 0.8.
        Assert.True(Environment.TickCount64 - start >= 480,
            $"Restarted after only {Environment.TickCount64 - start}ms");
    }
}
