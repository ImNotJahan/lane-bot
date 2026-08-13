using System.Globalization;
using Lane.Audio;
using Lane.Audio.Dsp;
using Lane.Audio.Synthesis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The local voice.
///
/// Flite is a child process, so most of what can go wrong is on the command line or in the
/// bytes that come back — both of which are checked here without running it. The one test
/// that does run it skips when the binary is not installed, because a test suite that is
/// required to work offline cannot also require a native package to be present.
/// </summary>
public sealed class FliteSynthesisTests
{
    private static FliteSynthesizer Synthesizer(FliteOptions options) =>
        new(options, NullLogger<FliteSynthesizer>.Instance);

    // ---- the command line --------------------------------------------------

    [Fact]
    public void Text_and_output_are_passed_as_flags_rather_than_positionally()
    {
        // A bare argument is read by flite as a filename, so a clause that happened to name
        // a file would be read out of the disk instead of spoken.
        IReadOnlyList<string> arguments = FliteSynthesizer.BuildArguments(new FliteOptions(), "hello.txt", "/tmp/a.wav");

        Assert.Equal(["-t", "hello.txt", "-o", "/tmp/a.wav"], arguments);
    }

    [Fact]
    public void Nothing_configured_means_flites_own_default_voice()
    {
        // Naming a voice that was not compiled into this build fails at the first clause;
        // the default one is the only voice every build is guaranteed to have.
        Assert.DoesNotContain("-voice", FliteSynthesizer.BuildArguments(new FliteOptions(), "hi", "/tmp/a.wav"));
    }

    [Fact]
    public void A_voice_and_a_voice_directory_both_reach_the_command_line()
    {
        IReadOnlyList<string> arguments = FliteSynthesizer.BuildArguments(
            new FliteOptions { Voice = "slt", VoiceDirectory = "/opt/voices" }, "hi", "/tmp/a.wav");

        Assert.Equal(["-voicedir", "/opt/voices", "-voice", "slt", "-t", "hi", "-o", "/tmp/a.wav"], arguments);
    }

    [Fact]
    public void Flites_own_timing_and_pitch_are_set_through_it_rather_than_through_soundtouch()
    {
        IReadOnlyList<string> arguments = FliteSynthesizer.BuildArguments(
            new FliteOptions { DurationStretch = 1.25f, TargetMeanF0 = 145 }, "hi", "/tmp/a.wav");

        Assert.Contains("duration_stretch=1.25", arguments);
        Assert.Contains("int_f0_target_mean=145", arguments);
    }

    [Fact]
    public void A_decimal_setting_does_not_change_with_the_machines_locale()
    {
        // On a machine whose separator is a comma, "duration_stretch=1,25" is read by flite
        // as 1 — a voice that is subtly wrong on some machines and right on others.
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            Assert.Contains(
                "duration_stretch=1.25",
                FliteSynthesizer.BuildArguments(new FliteOptions { DurationStretch = 1.25f }, "hi", "/tmp/a.wav"));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Extra_arguments_are_kept_ahead_of_the_text()
    {
        IReadOnlyList<string> arguments = FliteSynthesizer.BuildArguments(
            new FliteOptions { ExtraArgs = ["-ssml"] }, "hi", "/tmp/a.wav");

        Assert.Equal(["-ssml", "-t", "hi", "-o", "/tmp/a.wav"], arguments);
    }

    // ---- what comes back ---------------------------------------------------

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    public void The_wave_header_decides_the_native_rate_rather_than_configuration(int rate)
    {
        // Flite's rate belongs to the voice: 8 kHz for the built-in diphone one, 16 kHz for
        // the clustergen ones. Assuming one of them resamples the other by a factor of two.
        (byte[] pcm, AudioFormat format) = FliteSynthesizer.ReadWave(Wave(rate, channels: 1, samples: 4000));

        Assert.Equal(new AudioFormat(rate, 1, 16), format);
        Assert.Equal(4000 * 2, pcm.Length);
    }

    [Fact]
    public void Audio_that_is_not_sixteen_bit_pcm_is_refused_by_name()
    {
        MemoryStream file = new();

        using (WaveFileWriter writer = new(file, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1)))
            writer.WriteSamples(new float[1000], 0, 1000);

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => FliteSynthesizer.ReadWave(file.ToArray()));

        Assert.Contains("32 bits", ex.Message);
    }

    // ---- the synthesiser ---------------------------------------------------

    [Fact]
    public async Task Empty_text_costs_nothing_at_all()
    {
        // Not even a process: the chunker can hand over a fragment that is only whitespace.
        FliteSynthesizer flite = Synthesizer(new FliteOptions { ExecutablePath = "lane-flite-not-installed" });

        Assert.Empty(await flite.SynthesizeAsync("   ", new SpeechOptions("unused"), TestContext.Current.CancellationToken)
            .ToListAsync());
    }

    [Fact]
    public async Task A_missing_binary_is_reported_as_configuration_rather_than_as_a_crash()
    {
        FliteSynthesizer flite = Synthesizer(new FliteOptions { ExecutablePath = "lane-flite-not-installed" });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await flite
                .SynthesizeAsync("hello", new SpeechOptions("unused"), TestContext.Current.CancellationToken)
                .ToListAsync());

        Assert.Contains("lane-flite-not-installed", ex.Message);
        Assert.Contains("Lane:Audio:Flite:ExecutablePath", ex.Message);
    }

    [Fact]
    public void An_empty_executable_is_refused_at_construction() =>
        Assert.Throws<ArgumentException>(() => Synthesizer(new FliteOptions { ExecutablePath = "  " }));

    [Fact]
    public void The_declared_native_format_follows_the_configured_rate() =>
        Assert.Equal(new AudioFormat(8000, 1, 16), Synthesizer(new FliteOptions { SampleRate = 8000 }).NativeFormat);

    [Fact]
    public async Task A_voice_flite_does_not_have_is_refused_instead_of_quietly_swapped()
    {
        // Flite ignores an unknown -voice: no message, exit 0, and its default speaks
        // instead. So a typo is not a failure, it is Lane sounding like somebody else —
        // which gets blamed on the setting being ignored rather than on the name.
        Assert.SkipUnless(OnPath("flite"), "flite is not installed on this machine");

        FliteSynthesizer flite = Synthesizer(new FliteOptions { Voice = "not-a-voice" });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await flite
                .SynthesizeAsync("hello", new SpeechOptions("unused"), TestContext.Current.CancellationToken)
                .ToListAsync());

        Assert.Contains("not-a-voice", ex.Message);
        Assert.Contains("kal", ex.Message);          // and it says what it does have
    }

    [Fact]
    public async Task A_voice_flite_does_have_is_left_alone()
    {
        Assert.SkipUnless(OnPath("flite"), "flite is not installed on this machine");

        FliteSynthesizer flite = Synthesizer(new FliteOptions { Voice = "kal" });

        Assert.NotEmpty(await flite
            .SynthesizeAsync("hello", new SpeechOptions("unused"), TestContext.Current.CancellationToken)
            .ToListAsync());
    }

    /// <summary>The real thing, when the machine has it. Skipped rather than failed when it does not.</summary>
    [Fact]
    public async Task The_installed_binary_speaks_into_the_format_the_listener_asked_for()
    {
        Assert.SkipUnless(OnPath("flite"), "flite is not installed on this machine");

        FliteSynthesizer flite = Synthesizer(new FliteOptions());

        SpeechOptions options = new(
            VoiceId: "unused", Tempo: 0f, Pitch: 0f, TargetFormat: AudioFormat.Pcm48kStereo);

        List<AudioFrame> frames = await flite
            .SynthesizeAsync("Lane speaking, locally.", options, TestContext.Current.CancellationToken)
            .ToListAsync();

        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.Equal(AudioFormat.Pcm48kStereo, f.Format));
        Assert.All(frames, f => Assert.Equal(AudioFormat.Pcm48kStereo.BytesFor(TimeSpan.FromMilliseconds(20)), f.Pcm.Length));

        // Something was actually said, rather than a file of silence.
        Assert.Contains(frames, f => f.Pcm.Span.ToArray().Any(b => b != 0));
    }

    // ---- helpers -----------------------------------------------------------

    private static byte[] Wave(int rate, int channels, int samples)
    {
        MemoryStream file = new();

        using (WaveFileWriter writer = new(file, new WaveFormat(rate, 16, channels)))
        {
            byte[] pcm = new byte[samples * 2 * channels];

            for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i & 0xFF);

            writer.Write(pcm, 0, pcm.Length);
        }

        return file.ToArray();
    }

    private static bool OnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(directory => File.Exists(Path.Combine(directory, executable)));
}

/// <summary>
/// Which voice the container hands out.
///
/// The provider is named in configuration, so the failure worth designing for is a typo —
/// and it has to land at startup. A misspelled provider that silently fell back to
/// ElevenLabs would be found by the bill, and one that threw lazily would be found in the
/// middle of a conversation.
/// </summary>
public sealed class SpeechProviderSelectionTests
{
    private static ISpeechSynthesizer Resolve(AudioSetupOptions options)
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddLaneAudio(options);

        return services.BuildServiceProvider().GetRequiredService<ISpeechSynthesizer>();
    }

    [Fact]
    public void Flite_is_chosen_by_name_and_needs_no_key_at_all() =>
        Assert.IsType<FliteSynthesizer>(Resolve(new AudioSetupOptions { Enabled = true, Provider = "flite" }));

    [Fact]
    public void The_default_is_still_elevenlabs() =>
        Assert.IsType<ElevenLabsSynthesizer>(Resolve(new AudioSetupOptions
        {
            Enabled   = true,
            Synthesis = new ElevenLabsOptions { ApiKey = "test-key" },
        }));

    [Fact]
    public void A_misspelled_provider_fails_at_registration_rather_than_at_the_first_clause()
    {
        ServiceCollection services = new();

        services.AddLogging();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => services.AddLaneAudio(new AudioSetupOptions { Enabled = true, Provider = "eleven-labs" }));

        Assert.Contains("eleven-labs", ex.Message);
        Assert.Contains("flite", ex.Message);
    }
}

/// <summary>
/// The shaping both synthesisers share.
///
/// It lives in one place because it is a large part of what makes the voice hers: a copy
/// per provider is two voices as soon as one of them is touched.
/// </summary>
public sealed class SpeechShaperTests
{
    private static readonly SpeechOptions Unshaped =
        new(VoiceId: "unused", Tempo: 0f, Pitch: 0f, Rate: 0f);

    [Fact]
    public void A_source_rate_the_listener_does_not_want_is_resampled()
    {
        // One second of 16 kHz mono into 48 kHz stereo: three times the rate, twice the
        // channels. Flite's voices arrive at 8 or 16 kHz and Discord wants 48 kHz stereo.
        byte[] shaped = SpeechShaper.Shape(
            new byte[16000 * 2], AudioFormat.Pcm16kMono, Unshaped, AudioFormat.Pcm48kStereo);

        Assert.InRange(shaped.Length, 48000 * 4 - 8192, 48000 * 4 + 8192);
    }

    [Fact]
    public void Stereo_is_downmixed_when_the_listener_wants_mono()
    {
        byte[] shaped = SpeechShaper.Shape(
            new byte[48000 * 4], AudioFormat.Pcm48kStereo, Unshaped, AudioFormat.Pcm16kMono);

        Assert.InRange(shaped.Length, 16000 * 2 - 8192, 16000 * 2 + 8192);
    }

    [Fact]
    public void A_faster_tempo_produces_less_audio_for_the_same_words()
    {
        byte[] pcm = new byte[16000 * 2];

        byte[] plain  = SpeechShaper.Shape(pcm, AudioFormat.Pcm16kMono, Unshaped, AudioFormat.Pcm16kMono);
        byte[] hurried = SpeechShaper.Shape(
            pcm, AudioFormat.Pcm16kMono, Unshaped with { Tempo = 50f }, AudioFormat.Pcm16kMono);

        Assert.True(hurried.Length < plain.Length, "a 50% tempo increase should shorten the clip");
    }

    [Fact]
    public void Nothing_in_produces_nothing_out() =>
        Assert.Empty(SpeechShaper.Shape([], AudioFormat.Pcm16kMono, Unshaped, AudioFormat.Pcm48kStereo));

    [Fact]
    public void Anything_but_sixteen_bit_is_refused() =>
        Assert.Throws<NotSupportedException>(() => SpeechShaper.Shape(
            new byte[64], new AudioFormat(16000, 1, 8), Unshaped, AudioFormat.Pcm16kMono));
}
