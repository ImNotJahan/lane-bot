using Lane.Audio;
using Lane.Audio.Dsp;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The signal processing, checked against the original.
///
/// This code moved between projects, and the failure mode of getting it subtly wrong is
/// not a crash — it is Lane mishearing consonants, which would be blamed on the recogniser
/// for weeks. So the v2 implementation is reproduced below verbatim and the port is
/// required to agree with it byte for byte.
/// </summary>
public sealed class AudioDspTests
{
    // ---- the reference: v2's Convert48kTo16kMonoPcm16, copied unchanged --------

    private static class Original
    {
        private static readonly float[] Coefficients = ComputeLowPassFir(31, 8000.0 / 48000.0);

        private static float[] ComputeLowPassFir(int taps, double normalizedCutoff)
        {
            int m = taps - 1;
            float[] h = new float[taps];
            double sum = 0;

            for (int i = 0; i < taps; i++)
            {
                double x = i - m / 2.0;
                double sinc = x == 0
                    ? 2 * normalizedCutoff
                    : Math.Sin(2 * Math.PI * normalizedCutoff * x) / (Math.PI * x);

                double hamming = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / m);

                h[i] = (float)(sinc * hamming);
                sum += h[i];
            }

            for (int i = 0; i < taps; i++) h[i] /= (float)sum;

            return h;
        }

        public static byte[] Convert(float[] pcm48k, int inputChannels)
        {
            int inputFrames = pcm48k.Length / inputChannels;
            if (inputFrames == 0) return [];

            float[] mono48k = new float[inputFrames];

            if (inputChannels == 1)
            {
                Array.Copy(pcm48k, mono48k, inputFrames);
            }
            else
            {
                for (int i = 0; i < inputFrames; i++)
                {
                    int j = i * 2;
                    mono48k[i] = (pcm48k[j] + pcm48k[j + 1]) * 0.5f;
                }
            }

            int outputFrames = inputFrames / 3;
            if (outputFrames == 0) return [];

            byte[] output = new byte[outputFrames * 2];
            int halfTaps = Coefficients.Length / 2;

            for (int i = 0; i < outputFrames; i++)
            {
                int center = i * 3;
                float acc = 0f;

                for (int t = 0; t < Coefficients.Length; t++)
                {
                    int idx = center - halfTaps + t;
                    if ((uint)idx < (uint)inputFrames) acc += mono48k[idx] * Coefficients[t];
                }

                short pcm16 = (short)Math.Round(Math.Clamp(acc, -1f, 1f) * 32767f);
                output[i * 2] = (byte)(pcm16 & 0xFF);
                output[i * 2 + 1] = (byte)((pcm16 >> 8) & 0xFF);
            }

            return output;
        }
    }

    // ---- the port must agree exactly ---------------------------------------

    [Theory]
    [InlineData(1, 480)]
    [InlineData(1, 960)]
    [InlineData(2, 960)]
    [InlineData(2, 2880)]
    [InlineData(1, 5760)]
    public void The_port_matches_the_original_byte_for_byte(int channels, int frames)
    {
        Random random = new(Seed: 20260813);

        float[] pcm = new float[frames * channels];

        for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(random.NextDouble() * 2 - 1);

        Assert.Equal(Original.Convert(pcm, channels), PcmResampler.Convert48kTo16kMonoPcm16(pcm, channels));
    }

    [Fact]
    public void A_speech_shaped_signal_matches_the_original()
    {
        // A sine sweep through the voice band, which is what the filter was tuned for.
        float[] pcm = new float[9600];

        for (int i = 0; i < pcm.Length; i++)
        {
            double t = i / 48000.0;
            pcm[i] = (float)(0.6 * Math.Sin(2 * Math.PI * (200 + 3000 * t) * t));
        }

        Assert.Equal(Original.Convert(pcm, 1), PcmResampler.Convert48kTo16kMonoPcm16(pcm, 1));
    }

    // ---- properties worth stating outright --------------------------------

    [Fact]
    public void Three_input_frames_become_one_output_frame()
    {
        float[] pcm = new float[3000];

        byte[] output = PcmResampler.Convert48kTo16kMonoPcm16(pcm, 1);

        Assert.Equal(1000 * 2, output.Length);      // 16-bit samples
    }

    [Fact]
    public void Stereo_is_downmixed_before_resampling()
    {
        // Two channels in exact antiphase cancel to silence.
        float[] stereo = new float[1200];

        for (int i = 0; i < stereo.Length; i += 2)
        {
            stereo[i]     = 0.5f;
            stereo[i + 1] = -0.5f;
        }

        Assert.All(PcmResampler.Convert48kTo16kMonoPcm16(stereo, 2), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Content_above_the_new_nyquist_is_filtered_out_rather_than_folded_back()
    {
        // This is the whole point of the filter. A 15 kHz tone decimated without it aliases
        // down to 1 kHz — audible, and heard by a recogniser as a mangled consonant.
        float[] tone = new float[14400];

        for (int i = 0; i < tone.Length; i++) tone[i] = (float)Math.Sin(2 * Math.PI * 15000 * i / 48000.0);

        byte[] output = PcmResampler.Convert48kTo16kMonoPcm16(tone, 1);

        double peak = 0;

        // Skip the filter's start-up transient at either end.
        for (int i = 40; i < output.Length / 2 - 40; i++)
        {
            short sample = (short)(output[i * 2] | (output[i * 2 + 1] << 8));
            peak = Math.Max(peak, Math.Abs(sample / 32767.0));
        }

        Assert.True(peak < 0.05, $"15 kHz leaked through at {peak:P1}; the low-pass is not working");
    }

    [Fact]
    public void A_signal_inside_the_voice_band_survives()
    {
        float[] tone = new float[14400];

        for (int i = 0; i < tone.Length; i++) tone[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / 48000.0);

        byte[] output = PcmResampler.Convert48kTo16kMonoPcm16(tone, 1);

        double peak = 0;

        for (int i = 40; i < output.Length / 2 - 40; i++)
        {
            short sample = (short)(output[i * 2] | (output[i * 2 + 1] << 8));
            peak = Math.Max(peak, Math.Abs(sample / 32767.0));
        }

        Assert.True(peak > 0.85, $"1 kHz was attenuated to {peak:P1}; the filter is too aggressive");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Too_little_audio_to_resample_yields_nothing(int frames) =>
        Assert.Empty(PcmResampler.Convert48kTo16kMonoPcm16(new float[frames], 1));

    [Fact]
    public void Only_mono_and_stereo_are_accepted() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PcmResampler.Convert48kTo16kMonoPcm16(new float[300], 3));

    [Fact]
    public void A_sample_count_that_does_not_divide_by_channels_is_refused() =>
        Assert.Throws<ArgumentException>(
            () => PcmResampler.Convert48kTo16kMonoPcm16(new float[301], 2));
}

public sealed class PcmConverterTests
{
    [Fact]
    public void Converting_to_the_same_format_is_a_no_op()
    {
        byte[] pcm = [1, 2, 3, 4];

        Assert.Same(pcm, PcmConverter.Convert(pcm, AudioFormat.Pcm16kMono, AudioFormat.Pcm16kMono));
    }

    [Fact]
    public void Mono_becomes_stereo_and_the_rate_changes()
    {
        // 16 kHz mono → 48 kHz stereo: three times the rate, twice the channels.
        byte[] mono = new byte[16000 * 2];              // one second

        byte[] stereo = PcmConverter.Convert(mono, AudioFormat.Pcm16kMono, AudioFormat.Pcm48kStereo);

        Assert.InRange(stereo.Length, 48000 * 4 - 4096, 48000 * 4 + 4096);
    }

    [Fact]
    public void Frames_are_padded_so_the_last_one_is_never_short()
    {
        // Discord hears a short final frame as a click.
        byte[] pcm = new byte[3840 + 100];

        byte[][] frames = [.. PcmConverter.IntoFrames(pcm, 3840)];

        Assert.Equal(2, frames.Length);
        Assert.All(frames, f => Assert.Equal(3840, f.Length));
    }

    [Fact]
    public void An_exact_multiple_produces_no_extra_frame()
    {
        Assert.Equal(2, PcmConverter.IntoFrames(new byte[7680], 3840).Count());
    }
}
