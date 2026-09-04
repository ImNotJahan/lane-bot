using System.Collections.Concurrent;
using Lane.Core.Identity;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Voiceprints;

/// <summary>
/// Works out who spoke, from two signals that are good at different things.
///
/// The recogniser's label is excellent within a call and meaningless outside one — it can
/// follow a speaker through an hour of conversation but calls somebody else Guest-1
/// tomorrow. A voiceprint is the reverse: it is what recognises a person next week, and it
/// is unreliable on any single utterance, because two seconds of speech carries the sentence
/// almost as much as the speaker.
///
/// So the label is trusted for continuity and the voiceprint for identity. The first decent
/// utterance under a label decides which voice it is, and every utterance after it under the
/// same label goes to that voice without asking again — which is what stops attribution
/// flapping mid-sentence — while quietly feeding the profile so it gets better at recognising
/// that person the next time they walk in.
///
/// Nothing here ever decides that a voice belongs to a *person*. It can recognise a voice it
/// has heard before, and it can report who that voice was claimed by, but a match never
/// creates a claim: that is <c>claim_voice</c>'s business, and it is proved rather than
/// measured.
/// </summary>
public sealed class VoiceprintAttributor(
    IVoiceEncoder encoder,
    IVoiceprintDirectory voiceprints,
    IIdentityResolver identity,
    VoiceprintOptions options,
    ILogger<VoiceprintAttributor> log) : ISpeakerAttributor
{
    private readonly ConcurrentDictionary<string, Roster> _rosters = new(StringComparer.Ordinal);

    public async ValueTask<Participant> AttributeAsync(
        AudioBinding binding, VoiceSample voice, CancellationToken ct)
    {
        Roster roster = _rosters.GetOrAdd(binding.Source.Value, _ => new Roster());

        float[]? embedding = encoder.Embed(voice);

        // Already settled: this label belongs to a voice, and a short or unusable utterance
        // does not get to overturn that. The profile still improves from it when it can.
        if (roster.VoiceOf(voice.Label) is { } known)
        {
            if (embedding is not null)
                await voiceprints.ReinforceAsync(known, embedding, ct).ConfigureAwait(false);

            return Speaker(binding, known, roster.OrdinalOf(known));
        }

        // Not settled, and nothing to settle it with. Falling back keeps the words rather
        // than filing them under a voice picked at random.
        if (embedding is null)
        {
            log.LogDebug("{Label} on {Source} said too little to place ({Duration})",
                voice.Label, binding.Source, voice.Duration);

            return binding.Speaker;
        }

        VoiceprintMatch? match = voiceprints.Match(embedding, options.MatchThreshold);

        string id;

        if (match is { } found)
        {
            id = found.Id;

            await voiceprints.ReinforceAsync(id, embedding, ct).ConfigureAwait(false);

            log.LogInformation("{Label} on {Source} is voice {Id} ({Score:0.00})",
                voice.Label, binding.Source, id, found.Similarity);
        }
        else
        {
            id = await voiceprints.EnrolAsync(embedding, ct).ConfigureAwait(false);

            log.LogInformation("{Label} on {Source} is a voice I have not heard before, {Id}",
                voice.Label, binding.Source, id);
        }

        // Two labels resolving to one voice is the service having split one person in half.
        // Binding both to it is the repair, and it is why the roster is keyed the way it is.
        roster.Claim(voice.Label, id);

        return Speaker(binding, id, roster.OrdinalOf(id));
    }

    /// <summary>
    /// Who this voice is, as far as anyone has said.
    ///
    /// The account is the voice itself, so a stranger accumulates their own private memory
    /// rather than sharing an anonymous pool — and resolving it through
    /// <see cref="IIdentityResolver"/> means a name they chose applies without this class
    /// knowing anything about names. The person, when there is one, comes from the
    /// voiceprint store and is written over the top: identity links live somewhere else,
    /// and a voice must not silently become one.
    /// </summary>
    private Participant Speaker(AudioBinding binding, string voiceprintId, int ordinal)
    {
        ParticipantId id = VoiceAccounts.For(binding.Session.Surface, voiceprintId);

        Participant speaker = identity.Resolve(id, $"Voice {ordinal}");

        return voiceprints.Get(voiceprintId)?.BoundTo is { } person
            ? speaker with { GlobalUserId = person }
            : speaker;
    }

    public void Release(AudioSourceId source) => _rosters.TryRemove(source.Value, out _);

    /// <summary>Which voice each of this microphone's labels turned out to be.</summary>
    private sealed class Roster
    {
        private readonly Lock _gate = new();

        private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _order = [];

        public string? VoiceOf(string label)
        {
            lock (_gate) return _labels.GetValueOrDefault(label);
        }

        public void Claim(string label, string voiceprintId)
        {
            lock (_gate)
            {
                _labels[label] = voiceprintId;

                if (!_order.Contains(voiceprintId)) _order.Add(voiceprintId);
            }
        }

        /// <summary>First-heard order, so the first person to speak is "Voice 1".</summary>
        public int OrdinalOf(string voiceprintId)
        {
            lock (_gate)
            {
                int at = _order.IndexOf(voiceprintId);

                if (at >= 0) return at + 1;

                _order.Add(voiceprintId);

                return _order.Count;
            }
        }
    }
}
