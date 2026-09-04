using System.Collections.Concurrent;
using Lane.Core.Identity;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Voiceprints;

/// <summary>
/// Tells the people on one microphone apart, for as long as that microphone stays open.
///
/// This is what diarization alone can honestly support. The service's labels distinguish
/// speakers reliably within a transcription session and mean nothing outside one, so the
/// people they name are people for the length of a call and strangers again afterwards.
/// That is a real improvement on the alternative — three people in a room arriving as one
/// person contradicting themselves — and it is deliberately not dressed up as more than it
/// is: the account each voice gets is scoped to the source, so a stranger next week inherits
/// nobody's memory, and nothing here is written down.
///
/// <see cref="VoiceprintAttributor"/> is the same idea with a voiceprint underneath, which
/// is what makes a voice outlive the call it was heard on.
/// </summary>
public sealed class LabelRosterAttributor(IIdentityResolver identity, ILogger<LabelRosterAttributor> log)
    : ISpeakerAttributor
{
    private readonly ConcurrentDictionary<string, Roster> _rosters = new(StringComparer.Ordinal);

    public ValueTask<Participant> AttributeAsync(
        AudioBinding binding, VoiceSample voice, CancellationToken ct)
    {
        Roster roster = _rosters.GetOrAdd(binding.Source.Value, _ => new Roster());

        int ordinal = roster.OrdinalOf(voice.Label);

        // Scoped to the source, not the surface: these labels are reassigned to whoever
        // talks next time, so an id that outlived the connection would hand one person's
        // User-scoped memory to the next stranger who happened to be labelled Guest-1.
        ParticipantId id = new(binding.Session.Surface, $"voice/{binding.Source.Value}/{voice.Label}");

        Participant speaker = identity.Resolve(id, $"Voice {ordinal}");

        if (roster.IsNew(voice.Label))
            log.LogInformation("A new voice on {Source} is {Name}", binding.Source, speaker.DisplayName);

        return ValueTask.FromResult(speaker);
    }

    public void Release(AudioSourceId source) => _rosters.TryRemove(source.Value, out _);

    /// <summary>First-seen order, so the first person to speak is "Voice 1" however the service numbers them.</summary>
    private sealed class Roster
    {
        private readonly Lock _gate = new();

        private readonly Dictionary<string, int> _ordinals = new(StringComparer.OrdinalIgnoreCase);

        private string? _newest;

        public int OrdinalOf(string label)
        {
            lock (_gate)
            {
                if (_ordinals.TryGetValue(label, out int existing))
                {
                    _newest = null;
                    return existing;
                }

                int ordinal = _ordinals.Count + 1;

                _ordinals[label] = ordinal;
                _newest = label;

                return ordinal;
            }
        }

        public bool IsNew(string label)
        {
            lock (_gate) return string.Equals(_newest, label, StringComparison.OrdinalIgnoreCase);
        }
    }
}
