namespace Lane.Audio.Voiceprints;

/// <summary>
/// Turns a stretch of speech into a point in a space where the same person lands twice in
/// roughly the same place, and two people do not.
///
/// The whole of the persistence story rests on this. Diarization can say two voices in a
/// room are different; only an embedding can say one of them is the voice that was here
/// last week, because it is the only thing that survives the connection closing.
/// </summary>
public interface IVoiceEncoder : IDisposable
{
    int Dimensions { get; }

    /// <summary>
    /// The voiceprint of one utterance, unit length so that comparing two is a dot product.
    ///
    /// Null when the audio cannot support one: too short to hold a voice, or gone entirely.
    /// A caller that gets null must fall back rather than treat it as "no match" — the
    /// difference between "this is someone else" and "I could not tell" is the difference
    /// between enrolling a stranger and quietly misfiling a person.
    /// </summary>
    float[]? Embed(VoiceSample sample);
}

/// <summary>How alike two voiceprints are: 1 is identical, 0 unrelated.</summary>
public static class Voiceprints
{
    /// <summary>
    /// Cosine similarity, which for unit-length vectors is the dot product. Both sides are
    /// normalised at the point they are made, so nothing here needs to divide.
    /// </summary>
    public static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;

        float sum = 0f;

        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];

        return sum;
    }

    /// <summary>Scales a vector to unit length, leaving an all-zero one alone.</summary>
    public static float[] Normalise(float[] vector)
    {
        double sum = 0;

        foreach (float v in vector) sum += (double)v * v;

        if (sum <= 0) return vector;

        float scale = (float)(1.0 / Math.Sqrt(sum));

        for (int i = 0; i < vector.Length; i++) vector[i] *= scale;

        return vector;
    }
}
