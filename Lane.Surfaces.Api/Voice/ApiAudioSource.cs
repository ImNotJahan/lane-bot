using System.Threading.Channels;
using Lane.Audio;
using Lane.Audio.Dsp;

namespace Lane.Surfaces.Api.Voice;

/// <summary>
/// A client application's microphone, arriving over a WebSocket.
///
/// This is the practical answer to "multiple microphones": NAudio capture is Windows-only,
/// so a phone, a browser tab and a desktop helper all become audio sources by streaming PCM
/// here. Each socket is one source, bound to one conversation and one speaker, and several
/// can be open at once.
/// </summary>
public sealed class ApiAudioSource : IAudioSource
{
    /// <summary>
    /// What a socket is allowed to send.
    ///
    /// Deliberately short. 16 kHz mono is what recognition wants and needs no conversion at
    /// all; 48 kHz is what browsers and Discord produce, and goes through the same decimation
    /// path the Discord source uses — code that is checked sample-for-sample against v2's.
    /// Anything else would mean resampling a live stream chunk by chunk, and a resampler
    /// restarted on every packet does not fail loudly, it just quietly makes her mishear.
    /// </summary>
    public static bool IsSupported(AudioFormat format) =>
        format.BitsPerSample == 16 &&
        ((format.SampleRate == 16000 && format.Channels == 1) ||
         (format.SampleRate == 48000 && format.Channels is 1 or 2));

    public static string SupportedFormats => "16000 Hz mono, or 48000 Hz mono or stereo, 16-bit signed PCM";

    private readonly Channel<AudioFrame> _frames = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,

            // Audio is only useful live. A socket that falls behind should lose the oldest
            // audio rather than build a backlog that puts recognition further and further
            // behind the conversation.
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly AudioFormat _incoming;

    public ApiAudioSource(AudioSourceId id, AudioFormat incoming, string? speakerHint)
    {
        if (!IsSupported(incoming))
            throw new ArgumentException($"Unsupported capture format {incoming}. Expected {SupportedFormats}.",
                nameof(incoming));

        Id          = id;
        _incoming   = incoming;
        SpeakerHint = speakerHint;
    }

    public AudioSourceId Id { get; }

    /// <summary>Always what recognition wants, whatever the socket sent.</summary>
    public AudioFormat Format => AudioFormat.Pcm16kMono;

    public string? SpeakerHint { get; }

    /// <summary>Feeds one binary frame in. Called from the socket's receive loop.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 2) return;

        byte[] converted = _incoming.SampleRate == 16000 && _incoming.Channels == 1
            ? pcm.ToArray()
            : PcmResampler.Convert48kTo16kMonoPcm16(ToFloats(pcm), _incoming.Channels);

        if (converted.Length == 0) return;

        _frames.Writer.TryWrite(AudioFrame.Of(converted, Format));
    }

    /// <summary>Interleaved 16-bit samples as floats, which is what the decimator takes.</summary>
    private static float[] ToFloats(ReadOnlySpan<byte> pcm)
    {
        int count = pcm.Length / 2;

        float[] samples = new float[count];

        for (int i = 0; i < count; i++)
            samples[i] = BitConverter.ToInt16(pcm[(i * 2)..]) / 32768f;

        return samples;
    }

    public IAsyncEnumerable<AudioFrame> ReadAsync(CancellationToken ct) => _frames.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }
}
