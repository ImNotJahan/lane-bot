using System.Reflection;
using Lane.Audio.Voiceprints;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The features a speaker-embedding model is fed, checked against the implementation it was
/// trained against.
///
/// This is the same discipline the 48→16 kHz decimator gets, and for the same reason. Every
/// constant in <see cref="FilterBank"/> is half of a contract with a model file, and getting
/// one wrong does not throw — it produces features that are plausible, an embedding that is
/// plausible, and a recognition rate that is merely poor. Nothing downstream can tell that
/// apart from the model being mediocre, so it has to be caught here or not at all.
///
/// The reference comes from <c>torchaudio.compliance.kaldi.fbank</c> called exactly as
/// WeSpeaker calls it — see <c>Fixtures/reference_fbank.py</c>. The waveform is a formula
/// rather than a recording so that both sides can rebuild identical samples, and only the
/// expected output needs committing.
/// </summary>
public sealed class VoiceprintFeatureTests
{
    private const int SampleRate = 16000;
    private const int Samples    = 8000;                 // half a second

    /// <summary>A low fundamental with three formant-ish partials above it.</summary>
    private static readonly (double Frequency, double Amplitude)[] Partials =
        [(130.0, 3000.0), (440.0, 1500.0), (1970.0, 800.0), (3300.0, 400.0)];

    /// <summary>The identical samples the Python reference was generated from.</summary>
    private static byte[] Waveform()
    {
        byte[] pcm = new byte[Samples * 2];

        for (int i = 0; i < Samples; i++)
        {
            double v = 0.0;

            foreach ((double frequency, double amplitude) in Partials)
                v += amplitude * Math.Sin(2.0 * Math.PI * frequency * i / SampleRate);

            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)Math.Floor(v + 0.5));
        }

        return pcm;
    }

    private static (int Frames, int Bins, float[] Values) Reference()
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Lane.Tests.Fixtures.fbank-reference.txt")
            ?? throw new InvalidOperationException("The fbank reference fixture is missing.");

        using StreamReader reader = new(stream);

        string[] header = reader.ReadLine()!.Split(' ');

        int frames = int.Parse(header[0]);
        int bins   = int.Parse(header[1]);

        float[] values = new float[frames * bins];

        for (int f = 0; f < frames; f++)
        {
            string[] row = reader.ReadLine()!.Split(' ');

            for (int b = 0; b < bins; b++) values[f * bins + b] = float.Parse(row[b]);
        }

        return (frames, bins, values);
    }

    [Fact]
    public void The_features_match_the_implementation_the_model_was_trained_against()
    {
        (int frames, int bins, float[] expected) = Reference();

        float[] actual = FilterBank.Compute(Waveform());

        Assert.Equal(frames * bins, actual.Length);
        Assert.Equal(frames, FilterBank.FrameCount(Samples));

        // The fixture is written to six decimal places and both sides accumulate a 512-point
        // transform in single precision, so the tolerance is about the arithmetic rather
        // than about the algorithm — anything structural lands orders of magnitude outside it.
        float worst = 0f;
        int at = -1;

        for (int i = 0; i < expected.Length; i++)
        {
            float difference = Math.Abs(expected[i] - actual[i]);

            if (difference > worst) (worst, at) = (difference, i);
        }

        Assert.True(worst < 1e-3f,
            $"worst band differs by {worst:G4} at frame {at / bins}, bin {at % bins}");
    }

    [Fact]
    public void A_frame_is_twenty_five_milliseconds_every_ten()
    {
        // Half a second is 48 frames, not 50: the last frame has to fit whole, so the tail
        // shorter than a window is dropped rather than zero-padded.
        Assert.Equal(48, FilterBank.FrameCount(8000));

        Assert.Equal(1, FilterBank.FrameCount(400));
        Assert.Equal(0, FilterBank.FrameCount(399));
        Assert.Equal(0, FilterBank.FrameCount(0));
    }

    [Fact]
    public void An_utterance_too_short_to_frame_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(FilterBank.Compute(new byte[128]));
        Assert.Empty(FilterBank.Compute([]));
    }

    [Fact]
    public void Every_band_averages_to_zero_across_the_utterance()
    {
        // The mean subtraction is what makes a voiceprint about the voice rather than the
        // microphone and the room. If it silently stopped happening, the same person on a
        // different device would stop matching themselves — and nothing else would notice.
        float[] features = FilterBank.Compute(Waveform());

        int frames = FilterBank.FrameCount(Samples);

        for (int bin = 0; bin < FilterBank.MelBins; bin++)
        {
            double sum = 0;

            for (int f = 0; f < frames; f++) sum += features[f * FilterBank.MelBins + bin];

            Assert.True(Math.Abs(sum / frames) < 1e-4, $"band {bin} averaged {sum / frames:G4}");
        }
    }

    [Fact]
    public void Silence_does_not_become_an_outlier()
    {
        // Flooring a silent band with the wrong epsilon is the classic way to get this
        // wrong: C#'s float.Epsilon is a denormal, some thirty-eight orders of magnitude
        // below the C++ constant Kaldi uses, and it would drag every utterance mean with it.
        float[] features = FilterBank.Compute(new byte[Samples * 2]);

        Assert.All(features, v => Assert.True(Math.Abs(v) < 1f, $"a silent band came out at {v:G4}"));
    }
}
