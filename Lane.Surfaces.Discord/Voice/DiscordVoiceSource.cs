using System.Threading.Channels;
using Concentus;
using Lane.Audio;
using Lane.Audio.Dsp;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Discord.Voice;

/// <summary>
/// One Discord speaker, as an audio source.
///
/// Discord sends 48 kHz Opus per speaker; recognition wants 16 kHz mono PCM. The decode and
/// downsample happen here so nothing downstream knows what Discord is — the same recogniser
/// serves a desktop microphone or an API socket unchanged.
/// </summary>
internal sealed class DiscordVoiceSource : IAudioSource
{
    private const int OpusChannels = 1;

    private readonly Channel<AudioFrame> _frames = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,

            // Audio is only useful live. Falling behind is better than growing a backlog
            // that puts recognition further and further behind the conversation.
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly IOpusDecoder _decoder = PcmResampler.CreateDecoder(OpusChannels);
    private readonly ILogger _log;

    public DiscordVoiceSource(AudioSourceId id, string? speakerHint, ILogger log)
    {
        Id          = id;
        SpeakerHint = speakerHint;
        _log        = log;
    }

    public AudioSourceId Id { get; }

    public AudioFormat Format => AudioFormat.Pcm16kMono;

    public string? SpeakerHint { get; }

    /// <summary>Feeds one Opus packet in. Called from the gateway's receive handler.</summary>
    public void Write(ReadOnlySpan<byte> opusFrame)
    {
        try
        {
            float[] pcm48k = PcmResampler.DecodeOpusToPcm(_decoder, opusFrame, OpusChannels);

            if (pcm48k.Length == 0) return;

            byte[] pcm16kMono = PcmResampler.Convert48kTo16kMonoPcm16(pcm48k, OpusChannels);

            if (pcm16kMono.Length == 0) return;

            _frames.Writer.TryWrite(AudioFrame.Of(pcm16kMono, Format));
        }
        catch (Exception ex)
        {
            // A malformed packet is not worth ending the stream over.
            _log.LogDebug(ex, "Could not decode a packet from {Source}", Id);
        }
    }

    public IAsyncEnumerable<AudioFrame> ReadAsync(CancellationToken ct) => _frames.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();
        _decoder.Dispose();

        return ValueTask.CompletedTask;
    }
}
