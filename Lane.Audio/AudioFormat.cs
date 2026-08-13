namespace Lane.Audio;

/// <param name="SampleRate">Samples per second, per channel.</param>
public readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    /// <summary>What speech recognisers want.</summary>
    public static AudioFormat Pcm16kMono { get; } = new(16000, 1, 16);

    /// <summary>What Discord voice wants.</summary>
    public static AudioFormat Pcm48kStereo { get; } = new(48000, 2, 16);

    public int BytesPerFrame => Channels * BitsPerSample / 8;

    public int BytesPerSecond => SampleRate * BytesPerFrame;

    /// <summary>Bytes in a span of this length. Used to size Opus-sized chunks.</summary>
    public int BytesFor(TimeSpan duration) => (int)(BytesPerSecond * duration.TotalSeconds);

    public TimeSpan DurationOf(int bytes) => TimeSpan.FromSeconds((double)bytes / BytesPerSecond);

    public override string ToString() => $"{SampleRate}Hz/{Channels}ch/{BitsPerSample}bit";
}

/// <summary>
/// A block of PCM audio, tagged with what it is and when it was captured.
///
/// Carrying the format on every frame rather than agreeing it out of band is what lets a
/// synthesiser hand audio to a Discord channel and an API socket that want different
/// things, without either end knowing about the other.
/// </summary>
public sealed record AudioFrame(ReadOnlyMemory<byte> Pcm, AudioFormat Format, DateTimeOffset Captured)
{
    public TimeSpan Duration => Format.DurationOf(Pcm.Length);

    public static AudioFrame Of(byte[] pcm, AudioFormat format) => new(pcm, format, DateTimeOffset.UtcNow);
}

/// <summary>
/// Identifies one physical stream of audio: a Discord speaker, a desktop microphone, an
/// API socket. Distinct from the speaker — one person can arrive on two sources.
/// </summary>
public readonly record struct AudioSourceId(string Value)
{
    public override string ToString() => Value;
}
