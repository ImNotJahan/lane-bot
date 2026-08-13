using Lane.Audio;
using Lane.Audio.Playback;
using Lane.Host.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// `lane say "…"`, the command that exists so a voice can be judged by ear.
/// </summary>
public sealed class SayCommandTests
{
    [Fact]
    public void Everything_that_is_not_a_flag_is_the_line_to_speak()
    {
        // Quoting is optional, because it will be typed forty times in a row.
        (string text, IReadOnlyDictionary<string, string> flags) =
            SayCommand.Parse(["say", "Tide", "pools", "are", "full", "of", "things."]);

        Assert.Equal("Tide pools are full of things.", text);
        Assert.Empty(flags);
    }

    [Fact]
    public void Flags_are_lifted_out_from_around_the_text()
    {
        (string text, IReadOnlyDictionary<string, string> flags) =
            SayCommand.Parse(["say", "--voice", "slt", "Hello", "there.", "--out", "/tmp/a.wav"]);

        Assert.Equal("Hello there.", text);
        Assert.Equal("slt", flags["voice"]);
        Assert.Equal("/tmp/a.wav", flags["out"]);
    }

    [Fact]
    public void A_negative_number_is_a_value_and_not_a_flag()
    {
        // Taken positionally rather than by looking for a leading dash — every interesting
        // pitch is negative, and reading -2.5 as a flag would make the useful case the
        // broken one.
        (_, IReadOnlyDictionary<string, string> flags) = SayCommand.Parse(["say", "hi", "--pitch", "-2.5"]);

        Assert.Equal("-2.5", flags["pitch"]);
    }

    [Fact]
    public void Verbose_stands_alone_and_does_not_eat_the_next_word()
    {
        (string text, IReadOnlyDictionary<string, string> flags) = SayCommand.Parse(["say", "--verbose", "hello"]);

        Assert.Equal("hello", text);
        Assert.True(flags.ContainsKey("verbose"));
    }

    [Fact]
    public void A_flag_with_nothing_after_it_says_so() =>
        Assert.Contains("--voice needs a value", Assert.Throws<InvalidOperationException>(
            () => SayCommand.Parse(["say", "hello", "--voice"])).Message);

    [Fact]
    public void No_text_at_all_is_no_text() => Assert.Empty(SayCommand.Parse(["say"]).Text);
}

/// <summary>
/// Frames on their way to a file or a speaker.
/// </summary>
public sealed class WaveFileTests
{
    private static async IAsyncEnumerable<AudioFrame> Frames(AudioFormat format, int count, int bytes)
    {
        for (int i = 0; i < count; i++) yield return AudioFrame.Of(new byte[bytes], format);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task What_is_written_can_be_read_back_in_the_format_it_was_written_in()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lane-test-{Guid.NewGuid():N}.wav");

        try
        {
            (AudioFormat format, TimeSpan duration) = await WaveFile.WriteAsync(
                path, Frames(AudioFormat.Pcm48kStereo, count: 50, bytes: 3840), TestContext.Current.CancellationToken);

            Assert.Equal(AudioFormat.Pcm48kStereo, format);
            Assert.Equal(TimeSpan.FromSeconds(1), duration);          // 50 × 20 ms

            using WaveFileReader reader = new(path);

            Assert.Equal(48000, reader.WaveFormat.SampleRate);
            Assert.Equal(2, reader.WaveFormat.Channels);
            Assert.Equal(50 * 3840, reader.Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_frame_in_a_different_format_is_converted_rather_than_written_as_it_is()
    {
        // A header that stops describing the file halfway through is worse than a resample.
        string path = Path.Combine(Path.GetTempPath(), $"lane-test-{Guid.NewGuid():N}.wav");

        async IAsyncEnumerable<AudioFrame> Mixed()
        {
            yield return AudioFrame.Of(new byte[3840], AudioFormat.Pcm48kStereo);
            yield return AudioFrame.Of(new byte[320],  AudioFormat.Pcm16kMono);

            await Task.CompletedTask;
        }

        try
        {
            (AudioFormat format, _) = await WaveFile.WriteAsync(path, Mixed(), TestContext.Current.CancellationToken);

            Assert.Equal(AudioFormat.Pcm48kStereo, format);

            using WaveFileReader reader = new(path);

            // 20 ms of 48 kHz stereo, then 10 ms that arrived as 16 kHz mono and had to
            // become 10 ms of 48 kHz stereo — six times the bytes it came in as.
            Assert.InRange(reader.Length, 3840 + 1920 - 256, 3840 + 1920 + 256);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Nothing_to_write_writes_nothing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lane-test-{Guid.NewGuid():N}.wav");

        (_, TimeSpan duration) = await WaveFile.WriteAsync(
            path, Frames(AudioFormat.Pcm48kStereo, count: 0, bytes: 0), TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.Zero, duration);
        Assert.False(File.Exists(path), "an empty clip should not leave a headerless file behind");
    }
}

public sealed class SystemSpeakerTests
{
    private static SystemSpeaker Speaker(string? player = null) =>
        new(AudioFormat.Pcm48kStereo, NullLogger.Instance, player);

    [Fact]
    public void The_platforms_own_player_is_named_rather_than_guessed_at()
    {
        (string file, IReadOnlyList<string> arguments) = Speaker().Command("/tmp/a.wav");

        if (OperatingSystem.IsMacOS()) Assert.Equal("afplay", file);
        else if (OperatingSystem.IsWindows()) Assert.Equal("powershell", file);

        Assert.Contains("/tmp/a.wav", arguments);
    }

    [Fact]
    public void A_named_player_wins_over_the_platforms()
    {
        (string file, IReadOnlyList<string> arguments) = Speaker("mpv").Command("/tmp/a.wav");

        Assert.Equal("mpv", file);
        Assert.Equal(["/tmp/a.wav"], arguments);
    }
}
