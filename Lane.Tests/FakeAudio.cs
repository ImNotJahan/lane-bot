using System.Runtime.CompilerServices;
using System.Text;
using Lane.Audio;

namespace Lane.Tests;

/// <summary>
/// A recogniser that reads words straight out of the bytes it is given.
///
/// The trick is deliberate: 16 kHz mono audio passes through the API source untouched, so a
/// test can write UTF-8 into the socket and assert on what Lane heard. It exercises the
/// whole path — socket, source, router, session pump, model, reply — with nothing stubbed
/// except the part that would otherwise need a network and a human voice.
///
/// When the source is diarized it reads a speaker off the front of the same bytes —
/// <c>"Guest-2|it is raining"</c> — so a test can put two people on one socket and assert
/// they arrive as two, which is the one thing a single-speaker fake cannot show.
/// </summary>
public sealed class TranscribingBytesRecognizer(SpeakerAttribution attribution = SpeakerAttribution.Known)
    : ISpeechRecognizer
{
    public async IAsyncEnumerable<Transcript> TranscribeAsync(
        IAudioSource source, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (AudioFrame frame in source.ReadAsync(ct).ConfigureAwait(false))
        {
            string heard = Encoding.UTF8.GetString(frame.Pcm.Span).TrimEnd('\0');

            if (heard.Length == 0) continue;

            if (attribution != SpeakerAttribution.Diarized)
            {
                yield return new Transcript(heard, IsFinal: true, TimeSpan.Zero);
                continue;
            }

            // Unlabelled speech on a diarized source is what the service does before it has
            // made its mind up, and the router has to cope with it either way.
            int bar = heard.IndexOf('|');

            string label = bar < 0 ? "Unknown" : heard[..bar];
            string text  = bar < 0 ? heard : heard[(bar + 1)..];

            if (text.Length == 0) continue;

            yield return new Transcript(text, IsFinal: true, TimeSpan.Zero,
                Voice: new VoiceSample(label, frame.Pcm, source.Format));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The mirror image: speech whose bytes are the words, so a test can read them back.</summary>
public sealed class EncodingBytesSynthesizer : ISpeechSynthesizer
{
    public AudioFormat NativeFormat => AudioFormat.Pcm16kMono;

    public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(
        string text, SpeechOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        ct.ThrowIfCancellationRequested();

        yield return AudioFrame.Of(Encoding.UTF8.GetBytes(text), options.TargetFormat ?? NativeFormat);
    }
}
