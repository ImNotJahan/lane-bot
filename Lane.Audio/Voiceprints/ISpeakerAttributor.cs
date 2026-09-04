using Lane.Core.Identity;

namespace Lane.Audio.Voiceprints;

/// <summary>
/// Decides who just spoke, for sources where that is not settled in advance.
///
/// Separate from the router because there is more than one honest answer and they differ in
/// what they cost. A label roster can tell two people in a room apart for as long as the
/// microphone stays open and no longer; a voiceprint can recognise one of them next week but
/// needs a model, an enrolment and a threshold somebody calibrated. The router should not
/// know which of those is installed, and neither should the surfaces.
/// </summary>
public interface ISpeakerAttributor
{
    /// <summary>
    /// Who said this. Falls back to <see cref="AudioBinding.Speaker"/> rather than guessing
    /// when the audio does not support an answer — a wrong name on a line is worse than a
    /// vague one, because it is a person being quoted as somebody else.
    /// </summary>
    ValueTask<Participant> AttributeAsync(AudioBinding binding, VoiceSample voice, CancellationToken ct);

    /// <summary>
    /// The source has gone. Its labels are about to be handed to different people, so
    /// anything remembered against them has to go with it.
    /// </summary>
    void Release(AudioSourceId source);
}

/// <summary>Used when nothing diarizes: the binding already says who is speaking.</summary>
public sealed class NullSpeakerAttributor : ISpeakerAttributor
{
    public static NullSpeakerAttributor Instance { get; } = new();

    public ValueTask<Participant> AttributeAsync(
        AudioBinding binding, VoiceSample voice, CancellationToken ct) =>
        ValueTask.FromResult(binding.Speaker);

    public void Release(AudioSourceId source) { }
}
