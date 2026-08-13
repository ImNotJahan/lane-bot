using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lane.Audio.Dsp;

/// <summary>
/// Moves 16-bit PCM between sample rates and channel counts.
///
/// Needed because the format a synthesiser produces and the format a listener wants are
/// independent: Discord voice wants 48 kHz stereo, an API client might want 24 kHz mono,
/// and neither should constrain the other.
/// </summary>
public static class PcmConverter
{
    public static byte[] Convert(byte[] pcm, AudioFormat from, AudioFormat to)
    {
        ArgumentNullException.ThrowIfNull(pcm);

        if (pcm.Length == 0 || from == to) return pcm;

        if (from.BitsPerSample != 16 || to.BitsPerSample != 16)
            throw new NotSupportedException("Only 16-bit PCM is supported.");

        RawSourceWaveStream source = new(
            new MemoryStream(pcm), new WaveFormat(from.SampleRate, 16, from.Channels));

        ISampleProvider samples = source.ToSampleProvider();

        if (from.Channels == 2 && to.Channels == 1) samples = new StereoToMonoSampleProvider(samples);

        if (from.SampleRate != to.SampleRate) samples = new WdlResamplingSampleProvider(samples, to.SampleRate);

        if (samples.WaveFormat.Channels == 1 && to.Channels == 2) samples = new MonoToStereoSampleProvider(samples);

        IWaveProvider wave = samples.ToWaveProvider16();

        using MemoryStream output = new();

        byte[] buffer = new byte[8192];
        int read;

        while ((read = wave.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);

        return output.ToArray();
    }

    /// <summary>
    /// Splits a stream into fixed-size frames, padding the last one.
    ///
    /// Discord wants exactly 20 ms per Opus frame — 3840 bytes at 48 kHz stereo — and a
    /// short final frame is heard as a click.
    /// </summary>
    public static IEnumerable<byte[]> IntoFrames(byte[] pcm, int frameBytes)
    {
        if (frameBytes <= 0) throw new ArgumentOutOfRangeException(nameof(frameBytes));

        for (int offset = 0; offset < pcm.Length; offset += frameBytes)
        {
            byte[] frame = new byte[frameBytes];

            int available = Math.Min(frameBytes, pcm.Length - offset);

            Buffer.BlockCopy(pcm, offset, frame, 0, available);

            yield return frame;
        }
    }
}
