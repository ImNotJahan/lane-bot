using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lane.Audio.Dsp;

/// <summary>
/// Tempo, pitch, rate, sample rate and channel count, in one place.
///
/// Every synthesiser wants the same chain and none of them should own it: this shaping is
/// a large part of what makes Lane's voice hers, so a copy per provider would be a voice
/// per provider the first time one of them was touched. It also means the source format is
/// whatever the synthesiser actually produced — 22.05 kHz from ElevenLabs, 8 or 16 kHz from
/// a flite voice — while the caller still gets exactly the format it asked for.
///
/// A clip is shaped whole rather than streamed, because SoundTouch is stateful and
/// tempo-shifting a partial buffer does not give the same answer as shifting the buffer.
/// That is affordable only because callers send one clause at a time.
/// </summary>
public static class SpeechShaper
{
    public static byte[] Shape(byte[] pcm, AudioFormat source, SpeechOptions options, AudioFormat target)
    {
        ArgumentNullException.ThrowIfNull(pcm);

        if (pcm.Length == 0) return [];

        if (source.BitsPerSample != 16 || target.BitsPerSample != 16)
            throw new NotSupportedException("Only 16-bit PCM is supported.");

        RawSourceWaveStream stream = new(
            new MemoryStream(pcm), new WaveFormat(source.SampleRate, 16, source.Channels));

        using SoundTouchSampleProvider touch = new(
            stream.ToSampleProvider(),
            tempo: options.Tempo,
            pitchSemiTones: options.Pitch,
            rate: options.Rate,
            tuneForSpeech: options.TuneForSpeech);

        ISampleProvider shaped = touch;

        if (shaped.WaveFormat.Channels == 2 && target.Channels == 1)
            shaped = new StereoToMonoSampleProvider(shaped);

        if (shaped.WaveFormat.SampleRate != target.SampleRate)
            shaped = new WdlResamplingSampleProvider(shaped, target.SampleRate);

        if (shaped.WaveFormat.Channels == 1 && target.Channels == 2)
            shaped = new MonoToStereoSampleProvider(shaped);

        IWaveProvider wave = shaped.ToWaveProvider16();

        using MemoryStream output = new();

        byte[] buffer = new byte[8192];
        int read;

        while ((read = wave.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);

        return output.ToArray();
    }
}
