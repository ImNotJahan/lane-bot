using System.Runtime.CompilerServices;
using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;
using ElevenLabs.Voices;
using Lane.Audio.Dsp;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lane.Audio.Synthesis;

public sealed class ElevenLabsOptions
{
    public string? ApiKey { get; set; }

    /// <summary>Frame size handed to the listener. 20 ms at the target format suits Discord.</summary>
    public TimeSpan FrameDuration { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// How hard ElevenLabs should trade quality for a faster first byte, 0–4.
    ///
    /// Worth 3 here: the wait before Lane starts talking is the part a listener notices,
    /// and at 2 the normaliser stops being applied, which is where most of the saving is.
    /// </summary>
    public int OptimizeStreamingLatency { get; set; } = 2;
}

/// <summary>
/// Speech from ElevenLabs, shaped by the same SoundTouch chain v2 used.
///
/// The output format comes from the caller rather than being hardcoded to 48 kHz stereo,
/// so a Discord channel and an API socket can want different things.
///
/// One clip is still awaited whole before it is shaped, because SoundTouch is stateful and
/// tempo-shifting a partial buffer changes the result. That is affordable here only because
/// the caller sends one clause at a time rather than a whole reply — the pipelining that
/// matters happens above this, in <see cref="SentenceChunker"/>.
/// </summary>
public sealed class ElevenLabsSynthesizer : ISpeechSynthesizer
{
    private readonly ElevenLabsClient _client;
    private readonly ElevenLabsOptions _options;
    private readonly ILogger<ElevenLabsSynthesizer> _log;

    private readonly SemaphoreSlim _voiceLock = new(1, 1);
    private Voice? _voice;
    private string? _voiceId;

    public ElevenLabsSynthesizer(ElevenLabsOptions options, ILogger<ElevenLabsSynthesizer> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);

        _client  = new ElevenLabsClient(options.ApiKey);
        _options = options;
        _log     = log;
    }

    /// <summary>What ElevenLabs hands back before any shaping.</summary>
    public AudioFormat NativeFormat { get; } = new(22050, 1, 16);

    public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(
        string text, SpeechOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        Voice voice = await ResolveVoiceAsync(options.VoiceId, ct).ConfigureAwait(false);

        AudioFormat target = options.TargetFormat ?? AudioFormat.Pcm48kStereo;

        VoiceClip clip = await _client.TextToSpeechEndpoint
            .TextToSpeechAsync(
                text: text,
                voice: voice,
                voiceSettings: new VoiceSettings(
                    stability: options.Stability,
                    similarityBoost: options.Similarity),
                model: Model.FlashV2_5,
                outputFormat: OutputFormat.PCM_22050,
                optimizeStreamingLatency: _options.OptimizeStreamingLatency,
                cancellationToken: ct)
            .ConfigureAwait(false);

        byte[] raw = clip.ClipData.ToArray();

        if (raw.Length == 0) yield break;

        byte[] shaped = Shape(raw, options, target);

        int frameBytes = Math.Max(target.BytesPerFrame, target.BytesFor(_options.FrameDuration));

        foreach (byte[] frame in PcmConverter.IntoFrames(shaped, frameBytes))
        {
            ct.ThrowIfCancellationRequested();

            yield return AudioFrame.Of(frame, target);
        }
    }

    /// <summary>Tempo, pitch, resample and channel-map, as in v2 — but to a format the caller chose.</summary>
    private byte[] Shape(byte[] raw, SpeechOptions options, AudioFormat target)
    {
        RawSourceWaveStream source = new(
            new MemoryStream(raw), new WaveFormat(NativeFormat.SampleRate, 16, NativeFormat.Channels));

        ISampleProvider shaped = new SoundTouchSampleProvider(
            source.ToSampleProvider(),
            tempo: options.Tempo,
            pitchSemiTones: options.Pitch,
            rate: options.Rate,
            tuneForSpeech: options.TuneForSpeech);

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

    private async Task<Voice> ResolveVoiceAsync(string voiceId, CancellationToken ct)
    {
        // Looked up once and cached: it is a network round trip that would otherwise sit in
        // front of every single utterance.
        if (_voice is not null && _voiceId == voiceId) return _voice;

        await _voiceLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_voice is not null && _voiceId == voiceId) return _voice;

            _voice   = await _client.VoicesEndpoint.GetVoiceAsync(voiceId, cancellationToken: ct).ConfigureAwait(false);
            _voiceId = voiceId;

            _log.LogInformation("Using ElevenLabs voice {Voice}", _voice.Name);

            return _voice;
        }
        finally { _voiceLock.Release(); }
    }
}
