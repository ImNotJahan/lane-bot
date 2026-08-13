using Lane.Audio.Dsp;
using NAudio.Wave;

namespace Lane.Audio.Playback;

/// <summary>
/// Collects frames into a RIFF WAV on disk.
///
/// Frames carry their own format, so the header is taken from the first one that arrives
/// rather than agreed in advance — and a later frame in a different format is converted
/// into the one already written, because a file whose header stops describing its contents
/// halfway through is worse than a resample.
/// </summary>
public static class WaveFile
{
    public static async Task<(AudioFormat Format, TimeSpan Duration)> WriteAsync(
        string path, IAsyncEnumerable<AudioFrame> audio, CancellationToken ct)
    {
        AudioFormat?    format = null;
        WaveFileWriter? writer = null;

        long written = 0;

        try
        {
            await foreach (AudioFrame frame in audio.WithCancellation(ct).ConfigureAwait(false))
            {
                if (frame.Pcm.Length == 0) continue;

                if (writer is null)
                {
                    format = frame.Format;

                    writer = new WaveFileWriter(
                        path, new WaveFormat(format.Value.SampleRate, format.Value.BitsPerSample, format.Value.Channels));
                }

                ReadOnlyMemory<byte> pcm = frame.Format == format
                    ? frame.Pcm
                    : PcmConverter.Convert(frame.Pcm.ToArray(), frame.Format, format!.Value);

                await writer.WriteAsync(pcm, ct).ConfigureAwait(false);

                written += pcm.Length;
            }
        }
        finally
        {
            // The header carries the sizes, and they are only filled in on dispose.
            if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
        }

        return format is null ? (default, TimeSpan.Zero) : (format.Value, format.Value.DurationOf((int)written));
    }
}
