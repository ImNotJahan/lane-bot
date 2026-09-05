using System.Runtime.CompilerServices;
using Lane.Core.Identity;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Capture;

/// <summary>
/// Stops Lane from hearing herself.
///
/// A microphone and a speaker in one room are a loop: she answers out loud, the microphone
/// picks the answer up, recognition writes it down, and the first interim result of her own
/// sentence takes the floor away from her — so she stops mid-word, every time, and whatever
/// she managed to say arrives back as something the room said to her.
///
/// The cure is to not hear it at all. While she holds the floor, this hands recognition
/// silence in place of what the microphone captured: no interim result exists to barge in
/// with, and no final one exists to write down. Gating the audio rather than discarding the
/// transcripts is deliberate — a transcript arrives a moment after the speech that produced
/// it, so judging one by the clock at the time it surfaces lets the tail of her own sentence
/// through exactly when she has just stopped talking.
///
/// Silence rather than dropped frames, because dropping them shortens the stream: everything
/// after the gap moves earlier, and both the recogniser's own timing and the offsets an
/// utterance's audio is sliced back out by are measured from the start of the stream.
///
/// The cost, and it is a real one: nobody can interrupt her through this microphone either.
/// Telling her voice from a person's in one mixed signal is acoustic echo cancellation,
/// which needs the played audio and the captured audio sample-aligned against each other —
/// far more than a room microphone is worth. Barge-in still works everywhere a listener has
/// their own microphone: Discord, and an API socket.
/// </summary>
/// <param name="tail">
/// How long after she stops to keep ignoring the room. It covers the sound still in the
/// speakers when playback ends and the audio already captured but not yet read — without it
/// the last syllable of her own sentence arrives just as the gate opens.
/// </param>
public sealed class EchoGate(
    IAudioSource inner,
    IVoiceFloor floor,
    SessionId session,
    TimeSpan tail,
    ILogger log) : IAudioSource
{
    /// <summary>Never written to, so every frame can be a slice of the same one.</summary>
    private byte[] _silence = [];

    public AudioSourceId Id => inner.Id;

    public AudioFormat Format => inner.Format;

    public string? SpeakerHint => inner.SpeakerHint;

    public async IAsyncEnumerable<AudioFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        DateTimeOffset until = DateTimeOffset.MinValue;
        bool           shut  = false;

        await foreach (AudioFrame frame in inner.ReadAsync(ct).ConfigureAwait(false))
        {
            // Pushed forward on every frame she is still speaking over, so the tail is
            // measured from the last of her voice rather than the first of it. The floor
            // only answers for now, so the question is asked as each frame is read — which
            // is a moment after it was captured, and another reason the tail is not zero.
            if (floor.IsSpeaking(session)) until = frame.Captured + tail;

            if (frame.Captured >= until)
            {
                if (shut)
                {
                    shut = false;
                    log.LogDebug("Listening to {Source} again", inner.Id);
                }

                yield return frame;
                continue;
            }

            if (!shut)
            {
                shut = true;
                log.LogDebug("Ignoring {Source} while Lane is speaking in {Session}", inner.Id, session);
            }

            yield return frame with { Pcm = Silence(frame.Pcm.Length) };
        }
    }

    private ReadOnlyMemory<byte> Silence(int length)
    {
        if (_silence.Length < length) _silence = new byte[length];

        return _silence.AsMemory(0, length);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
