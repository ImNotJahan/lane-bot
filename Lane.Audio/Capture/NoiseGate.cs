using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Capture;

public sealed class NoiseGateOptions
{
    /// <summary>Off by default: a microphone that has been asked for should be listened to.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How loud the room has to get before Lane treats it as being talked to, in dBFS —
    /// decibels below the loudest thing 16-bit audio can hold, so every usable value is
    /// negative and larger numbers mean stricter.
    ///
    /// Rough bearings, though they move with the microphone and the room: somebody talking
    /// to a microphone a foot away lands around -20, the same person across the room around
    /// -35, and a conversation through a wall or a door around -50. The gap between the last
    /// two is what this setting lives in, and the only way to place it is to watch: the gate
    /// logs the level that opened it at debug, so run it once at the default and see what the
    /// room you actually care about measures.
    ///
    /// Set it too high and she stops hearing anybody who is not leaning in; the failure is
    /// silent from the inside, because a room that is never loud enough looks exactly like a
    /// room where nobody is talking.
    /// </summary>
    public double MinimumLevel { get; set; } = -40;

    /// <summary>
    /// How long the room stays open after it drops back below the threshold.
    ///
    /// Speech is not continuously loud — it has gaps between words and trails off at the end
    /// of a sentence, and a gate that shuts on the first quiet moment cuts sentences into
    /// pieces that recognition then has to guess at. Long enough to ride over a pause,
    /// short enough that the room does not stay open through the next conversation.
    /// </summary>
    public TimeSpan Hold { get; set; } = TimeSpan.FromMilliseconds(800);
}

/// <summary>
/// Stops Lane from listening to the next room.
///
/// A microphone left open in a house hears everything in it: a conversation through a wall,
/// a television, somebody on the phone two rooms away. All of it reaches recognition, some of
/// it comes back as words, and she answers a room that was not talking to her. Loudness is
/// the one signal that separates them, because distance is the thing that made them faint.
///
/// So the room is only listened to while it is loud enough to plausibly be aimed at her, and
/// handed to recognition as silence the rest of the time — the same treatment, and for the
/// same reasons, as <see cref="EchoGate"/>: audio never sent produces no interim result to
/// barge in with and no final one to write down, and silence rather than nothing keeps the
/// stream's timing intact.
///
/// It is one frame behind on purpose. A word does not start at full volume, so the frame that
/// crosses the threshold is usually the second one of it — gating on the spot would clip the
/// front off every sentence, which is exactly where her name would be. Holding one frame back
/// means the frame before the one that opened the gate goes through as well, at the cost of a
/// fixed tenth of a second of latency that nothing downstream measures.
/// </summary>
public sealed class NoiseGate(
    IAudioSource inner,
    NoiseGateOptions options,
    ILogger log) : IAudioSource
{
    /// <summary>Never written to, so every silenced frame can be a slice of the same one.</summary>
    private byte[] _silence = [];

    public AudioSourceId Id => inner.Id;

    public AudioFormat Format => inner.Format;

    public string? SpeakerHint => inner.SpeakerHint;

    public async IAsyncEnumerable<AudioFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        AudioFrame? held = null;
        bool        heldOpen = false;

        DateTimeOffset until = DateTimeOffset.MinValue;

        await foreach (AudioFrame frame in inner.ReadAsync(ct).ConfigureAwait(false))
        {
            double level = Level(frame);

            if (level >= options.MinimumLevel) until = frame.Captured + options.Hold;

            bool open = frame.Captured < until;

            // Both transitions, at debug, because the number is the only way to set the
            // threshold: what the room measures when it is talking to her, and what it
            // measures when it is not, are the two ends of where the setting belongs.
            if (open && !heldOpen)
                log.LogDebug("Listening to {Source} — the room came up to {Level:0.0} dBFS", inner.Id, level);

            else if (!open && heldOpen)
                log.LogDebug("Ignoring {Source} — the room fell to {Level:0.0} dBFS", inner.Id, level);

            // Emitted a frame late: this one is open at its own time, or it is the run-up to
            // one that opened the gate, and a word only just loud enough started before it.
            if (held is { } previous) yield return heldOpen || open ? previous : Silenced(previous);

            held     = frame;
            heldOpen = open;
        }

        if (held is { } last) yield return heldOpen ? last : Silenced(last);
    }

    /// <summary>
    /// How loud a frame is, as RMS in dBFS.
    ///
    /// RMS rather than the loudest sample in it: a single spike is a chair or a door, and
    /// what matters here is whether the room is sustaining a sound, which is what speech does
    /// and what a knock does not.
    /// </summary>
    internal static double Level(AudioFrame frame)
    {
        ReadOnlySpan<byte> pcm = frame.Pcm.Span;

        int samples = pcm.Length / 2;

        if (samples == 0) return double.NegativeInfinity;

        double sum = 0;

        for (int i = 0; i < samples; i++)
        {
            double sample = BitConverter.ToInt16(pcm[(i * 2)..]);

            sum += sample * sample;
        }

        double rms = Math.Sqrt(sum / samples) / 32768d;

        // Digital silence is not quiet, it is nothing at all, and the logarithm of it is not
        // a number recognition or a comparison can do anything sensible with.
        return rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
    }

    private AudioFrame Silenced(AudioFrame frame)
    {
        if (_silence.Length < frame.Pcm.Length) _silence = new byte[frame.Pcm.Length];

        return frame with { Pcm = _silence.AsMemory(0, frame.Pcm.Length) };
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
