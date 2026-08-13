using Concentus;

namespace Lane.Audio.Dsp;

/// <summary>
/// Opus decoding and 48 kHz → 16 kHz conversion, ported unchanged from v2.
///
/// The filtering here is the reason Lane hears well: decimating 48 kHz straight to 16 kHz
/// folds everything above 8 kHz back down as aliasing, which sounds like a recogniser
/// mishearing consonants. The 31-tap windowed sinc removes it first.
///
/// Kept free of any transport type so it can be tested against fixtures, which is what
/// makes it safe to have moved at all.
/// </summary>
public static class PcmResampler
{
    public const int DiscordSampleRate = 48000;
    public const int TargetSampleRate  = 16000;
    public const int BytesPerSample    = 2;

    /// <summary>One decoder per speaker, reused across their packets.</summary>
    public static IOpusDecoder CreateDecoder(int channels) => OpusCodecFactory.CreateDecoder(48000, channels);

    // 31-tap Hamming-windowed sinc low-pass, cutoff at 8 kHz — Nyquist for the 16 kHz
    // output. Applied before 3:1 decimation to prevent aliasing.
    private static readonly float[] LowPassCoefficients = ComputeLowPassFir(31, 8000.0 / 48000.0);

    private static float[] ComputeLowPassFir(int taps, double normalizedCutoff)
    {
        int     m   = taps - 1;
        float[] h   = new float[taps];
        double  sum = 0;

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

    /// <summary>Decodes one Opus packet into 48 kHz float samples.</summary>
    public static float[] DecodeOpusToPcm(IOpusDecoder decoder, ReadOnlySpan<byte> opusFrame, int channels)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        // The largest Opus frame is 120 ms, which at 48 kHz is 5760 samples per channel.
        const int maxSamplesPerChannel = 5760;

        float[] pcm = new float[maxSamplesPerChannel * channels];

        int samplesPerChannel = decoder.Decode(opusFrame, pcm.AsSpan(), maxSamplesPerChannel, false);

        if (samplesPerChannel <= 0) return [];

        int totalSamples = samplesPerChannel * channels;

        if (totalSamples == pcm.Length) return pcm;

        float[] trimmed = new float[totalSamples];

        Array.Copy(pcm, trimmed, totalSamples);

        return trimmed;
    }

    /// <summary>
    /// Converts 48 kHz float PCM (mono or interleaved stereo) into 16 kHz 16-bit mono,
    /// little-endian — what speech recognition wants.
    /// </summary>
    public static byte[] Convert48kTo16kMonoPcm16(float[] pcm48k, int inputChannels)
    {
        ArgumentNullException.ThrowIfNull(pcm48k);

        if (inputChannels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(inputChannels), "Only mono or stereo input is supported.");

        if (pcm48k.Length % inputChannels != 0)
            throw new ArgumentException("PCM sample count must be divisible by channel count.", nameof(pcm48k));

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

        byte[] output   = new byte[outputFrames * 2];
        int    halfTaps = LowPassCoefficients.Length / 2;

        for (int i = 0; i < outputFrames; i++)
        {
            int   center = i * 3;
            float acc    = 0f;

            for (int t = 0; t < LowPassCoefficients.Length; t++)
            {
                int index = center - halfTaps + t;

                if ((uint)index < (uint)inputFrames) acc += mono48k[index] * LowPassCoefficients[t];
            }

            short pcm16 = (short)Math.Round(Math.Clamp(acc, -1f, 1f) * 32767f);

            output[i * 2]     = (byte)(pcm16 & 0xFF);
            output[i * 2 + 1] = (byte)((pcm16 >> 8) & 0xFF);
        }

        return output;
    }
}
