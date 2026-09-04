using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lane.Audio.Voiceprints;

public sealed class VoiceprintOptions
{
    /// <summary>
    /// Off by default. With it off Lane still tells the people in a room apart for the
    /// length of a call; what it buys is recognising one of them the next time.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A speaker-embedding model in ONNX form — a WeSpeaker export such as
    /// <c>voxceleb_CAM++_LM.onnx</c>. Tens of megabytes, so it is pointed at rather than
    /// shipped.
    /// </summary>
    public string ModelPath { get; set; } = "";

    /// <summary>
    /// How alike two voiceprints must be to be called the same person.
    ///
    /// A starting point, not a result: it depends on the model, the microphones and the
    /// room, and the only way to set it honestly is to measure it on recordings of the
    /// people who will actually be in the room. Too low merges two people permanently;
    /// too high just means being asked who you are more often, which is the direction to
    /// err in.
    /// </summary>
    public float MatchThreshold { get; set; } = 0.70f;

    /// <summary>
    /// Below this, an utterance is too short to say anything reliable about a voice, and
    /// attribution falls back rather than guessing from it.
    /// </summary>
    public TimeSpan MinimumUtterance { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How many samples a profile averages before it stops taking new ones. Enough to
    /// settle, few enough that one bad slice cannot drag a person's voice off itself.
    /// </summary>
    public int MaxSamplesPerProfile { get; set; } = 20;
}

/// <summary>
/// A speaker-embedding model, run locally.
///
/// Local because the hosted alternative is not available: Azure's Speaker Recognition, the
/// one part of that service which holds a voice across sessions, is a Limited Access product
/// that has to be applied for as a managed customer. Everything else in the audio path talks
/// to a service; this one had to come in-process, which is why there is a filter bank in
/// this project at all.
/// </summary>
public sealed class OnnxVoiceEncoder : IVoiceEncoder
{
    private readonly InferenceSession _session;
    private readonly VoiceprintOptions _options;
    private readonly ILogger<OnnxVoiceEncoder> _log;

    private readonly string _input;
    private readonly string _output;

    private readonly Lock _gate = new();

    public OnnxVoiceEncoder(VoiceprintOptions options, ILogger<OnnxVoiceEncoder> log)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ModelPath))
            throw new InvalidOperationException(
                "Lane:Audio:Voiceprints:Enabled is on but no ModelPath is set. Point it at a " +
                "speaker-embedding model in ONNX form, or turn voiceprints off.");

        if (!File.Exists(options.ModelPath))
            throw new InvalidOperationException(
                $"The voiceprint model '{options.ModelPath}' does not exist. Download a WeSpeaker " +
                "ONNX export to that path, or turn Lane:Audio:Voiceprints off.");

        _options = options;
        _log     = log;
        _session = new InferenceSession(options.ModelPath);

        // Read rather than assume. Exports disagree about what these are called — feats,
        // input, x; embs, output, embedding — and a hard-coded name that happens to be
        // wrong fails at the first utterance rather than at startup.
        _input  = _session.InputMetadata.Keys.First();
        _output = _session.OutputMetadata.Keys.First();

        Dimensions = _session.OutputMetadata[_output].Dimensions.LastOrDefault();

        log.LogInformation("Voiceprints on: {Model}, {Dimensions}-dimensional, '{Input}' to '{Output}'",
            Path.GetFileName(options.ModelPath), Dimensions, _input, _output);
    }

    /// <summary>Zero when the export does not declare it; the first embedding settles it.</summary>
    public int Dimensions { get; private set; }

    public float[]? Embed(VoiceSample sample)
    {
        if (sample.IsLabelOnly || sample.Duration < _options.MinimumUtterance) return null;

        float[] features = FilterBank.Compute(sample.Pcm.Span);

        if (features.Length == 0) return null;

        int frames = features.Length / FilterBank.MelBins;

        DenseTensor<float> input = new(features, [1, frames, FilterBank.MelBins]);

        try
        {
            // One session shared across microphones, and Run is thread-safe, but the
            // sessions here are short and the lock keeps memory predictable when three
            // people in a room finish a sentence at once.
            lock (_gate)
            {
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                    _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)]);

                float[] embedding = [.. results.First().AsEnumerable<float>()];

                if (embedding.Length == 0) return null;

                Dimensions = embedding.Length;

                return Lane.Audio.Voiceprints.Voiceprints.Normalise(embedding);
            }
        }
        catch (OnnxRuntimeException ex)
        {
            // A failure here must not take a conversation down with it — the words were
            // still heard, and attribution has a fallback.
            _log.LogError(ex, "The voiceprint model rejected a {Duration} utterance", sample.Duration);

            return null;
        }
    }

    public void Dispose() => _session.Dispose();
}
