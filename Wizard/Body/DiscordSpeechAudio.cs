using Concentus;

namespace Wizard.Body
{
    public static class DiscordSpeechAudio
    {
        // Discord voice is typically decoded at 48 kHz.
        public const int DiscordSampleRate = 48000;
        public const int TargetSampleRate  = 16000;
        public const int BytesPerSample    = 2; // 16-bit PCM

        /// <summary>
        /// Creates a decoder for one Discord speaker/stream.
        /// Keep one decoder per user/SSRC and reuse it across packets.
        /// </summary>
        public static IOpusDecoder CreateDecoder(int channels) => OpusCodecFactory.CreateDecoder(48000, channels);

        // 31-tap Hamming-windowed sinc low-pass filter, cutoff at 8 kHz (Nyquist for 16k output).
        // Applied before 3:1 decimation to prevent aliasing.
        private static readonly float[] s_lowPassCoeffs = ComputeLowPassFir(31, 8000.0 / 48000.0);

        private static float[] ComputeLowPassFir(int taps, double normalizedCutoff)
        {
            int     m   = taps - 1;
            float[] h   = new float[taps];
            double  sum = 0;

            for (int i = 0; i < taps; i++)
            {
                double x    = i - m / 2.0;
                double sinc = x == 0 
                            ? 2 * normalizedCutoff 
                            : Math.Sin(2 * Math.PI * normalizedCutoff * x) 
                            / (Math.PI * x);
                
                double hamming = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / m);

                h[i] = (float)(sinc * hamming);

                sum += h[i];
            }

            for (int i = 0; i < taps; i++) h[i] /= (float)sum;

            return h;
        }

        /// <summary>
        /// Decodes one Opus packet into 16-bit PCM samples at 48 kHz.
        /// Returns interleaved PCM bytes (little-endian).
        /// </summary>
        public static float[] DecodeOpusToPcm(
            IOpusDecoder decoder,
            ReadOnlySpan<byte> opusFrame,
            int channels)
        {
            ArgumentNullException.ThrowIfNull(decoder);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

            // Max Opus frame: 120 ms at 48 kHz = 5760 samples/channel
            int maxSamplesPerChannel = 5760;
            float[] pcm = new float[maxSamplesPerChannel * channels];

            // Concentus IOpusDecoder overload:
            // Decode(ReadOnlySpan<byte> in_data, Span<float> out_pcm, int frame_size, bool decode_fec = false)
            int samplesPerChannel = decoder.Decode(
                opusFrame,
                pcm.AsSpan(),
                maxSamplesPerChannel,
                false);

            if (samplesPerChannel <= 0) return [];

            int totalSamples = samplesPerChannel * channels;

            if (totalSamples == pcm.Length) return pcm;

            float[] trimmed = new float[totalSamples];

            Array.Copy(pcm, trimmed, totalSamples);

            return trimmed;
        }

        /// <summary>
        /// Converts 48 kHz 16-bit PCM (mono or stereo interleaved) into
        /// 16 kHz 16-bit mono PCM.
        /// </summary>
        public static byte[] Convert48kTo16kMonoPcm16(float[] pcm48k, int inputChannels)
        {
            ArgumentNullException.ThrowIfNull(pcm48k);

            if (inputChannels != 1 && inputChannels != 2)
                throw new ArgumentOutOfRangeException(nameof(inputChannels), "Only mono or stereo input is supported.");

            if (pcm48k.Length % inputChannels != 0)
                throw new ArgumentException("PCM sample count must be divisible by channel count.", nameof(pcm48k));

            int inputFrames = pcm48k.Length / inputChannels;
            if (inputFrames == 0)
                return [];

            // Downmix to mono
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

            // Resample 48k -> 16k: low-pass filter then 3:1 decimation
            int outputFrames = inputFrames / 3;

            if (outputFrames == 0) return [];

            byte[] output = new byte[outputFrames * 2];
            int halfTaps = s_lowPassCoeffs.Length / 2;

            for (int i = 0; i < outputFrames; i++)
            {
                int center = i * 3;
                float acc = 0f;
                for (int t = 0; t < s_lowPassCoeffs.Length; t++)
                {
                    int idx = center - halfTaps + t;
                    
                    if ((uint) idx < (uint) inputFrames) acc += mono48k[idx] * s_lowPassCoeffs[t];
                }

                short pcm16 = (short)Math.Round(Math.Clamp(acc, -1f, 1f) * 32767f);
                output[i * 2]     = (byte)(pcm16 & 0xFF);
                output[i * 2 + 1] = (byte)((pcm16 >> 8) & 0xFF);
            }

            return output;
        }
    }
}