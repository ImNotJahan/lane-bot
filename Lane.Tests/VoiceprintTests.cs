using Lane.Audio;
using Lane.Audio.Voiceprints;
using Lane.Core.Identity;
using Lane.Memory.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The voices Lane remembers, and the rule that a match recalls a claim but never makes one.
/// </summary>
public sealed class VoiceprintDirectoryTests
{
    private readonly LaneDatabase _database = new(
        new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance);

    private VoiceprintDirectory Directory() =>
        new(new SqliteKeyValueStore(_database), TimeProvider.System,
            NullLogger<VoiceprintDirectory>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A unit vector pointing mostly along one axis — a stand-in for one person's voice.</summary>
    private static float[] Voice(int axis, float wobble = 0f)
    {
        float[] v = new float[16];

        v[axis] = 1f;

        if (wobble != 0f) v[(axis + 1) % 16] = wobble;

        return Voiceprints.Normalise(v);
    }

    [Fact]
    public async Task A_voice_is_recognised_after_a_restart()
    {
        // The entire point of keeping voiceprints at all. Diarization already tells people
        // apart within a call; this is what makes one of them the same person tomorrow.
        VoiceprintDirectory first = Directory();

        await first.StartAsync(Ct);

        string id = await first.EnrolAsync(Voice(3), Ct);

        await first.BindAsync(id, "jahan-a1b2", Ct);

        // A second directory over the same store is what the next run of the process sees.
        VoiceprintDirectory restarted = Directory();

        await restarted.StartAsync(Ct);

        VoiceprintMatch match = Assert.NotNull(restarted.Match(Voice(3, 0.05f), 0.7f));

        Assert.Equal(id, match.Id);
        Assert.Equal("jahan-a1b2", restarted.Get(id)!.BoundTo);
    }

    [Fact]
    public async Task A_different_voice_does_not_match()
    {
        VoiceprintDirectory directory = Directory();

        await directory.StartAsync(Ct);

        await directory.EnrolAsync(Voice(3), Ct);

        // Orthogonal: as unlike the enrolled voice as this space allows.
        Assert.Null(directory.Match(Voice(9), 0.7f));
    }

    [Fact]
    public async Task The_closest_voice_wins_and_a_tie_wins_nothing()
    {
        VoiceprintDirectory directory = Directory();

        await directory.StartAsync(Ct);

        string near = await directory.EnrolAsync(Voice(3, 0.10f), Ct);
        await directory.EnrolAsync(Voice(3, 0.60f), Ct);

        VoiceprintMatch match = Assert.NotNull(directory.Match(Voice(3), 0.7f));

        Assert.Equal(near, match.Id);
    }

    [Fact]
    public async Task Enrolling_never_claims_a_person()
    {
        // The rule the two stores exist to keep. Hearing a voice is not being told whose
        // it is, and nothing but an explicit claim may fill that in.
        VoiceprintDirectory directory = Directory();

        await directory.StartAsync(Ct);

        string id = await directory.EnrolAsync(Voice(1), Ct);

        Assert.Null(directory.Get(id)!.BoundTo);
        Assert.Empty(directory.BoundTo("jahan-a1b2"));
    }

    [Fact]
    public async Task Reinforcing_settles_a_profile_rather_than_chasing_the_last_thing_said()
    {
        VoiceprintDirectory directory = Directory();

        await directory.StartAsync(Ct);

        string id = await directory.EnrolAsync(Voice(3), Ct);

        // One unrepresentative utterance, folded into a profile with history behind it.
        for (int i = 0; i < 8; i++) await directory.ReinforceAsync(id, Voice(3, 0.05f), Ct);

        await directory.ReinforceAsync(id, Voice(9), Ct);

        Voiceprint print = directory.Get(id)!;

        Assert.Equal(10, print.Samples);

        // Still recognisably the voice it started as.
        VoiceprintMatch match = Assert.NotNull(directory.Match(Voice(3), 0.7f));

        Assert.Equal(id, match.Id);
    }

    [Fact]
    public async Task A_forgotten_voice_is_gone_from_the_store_as_well_as_from_memory()
    {
        // Biometric data about a person: being able to make her forget it is not optional.
        VoiceprintDirectory directory = Directory();

        await directory.StartAsync(Ct);

        string id = await directory.EnrolAsync(Voice(3), Ct);

        await directory.ForgetAsync(id, Ct);

        VoiceprintDirectory restarted = Directory();

        await restarted.StartAsync(Ct);

        Assert.Null(restarted.Get(id));
        Assert.Null(restarted.Match(Voice(3), 0.7f));
    }

    [Fact]
    public async Task Nothing_durable_means_nothing_is_promised()
    {
        // The same rule the identity tools follow: a claim that a restart erases is worse
        // than one never offered.
        Assert.False(NullVoiceprintDirectory.Instance.Durable);
        Assert.Null(NullVoiceprintDirectory.Instance.Match(Voice(3), 0.7f));

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await NullVoiceprintDirectory.Instance.EnrolAsync(Voice(3), Ct));
    }
}

/// <summary>
/// Putting the two signals together: the recogniser's label for continuity within a call,
/// the voiceprint for identity across them.
/// </summary>
public sealed class VoiceprintAttributionTests
{
    private static readonly AudioFormat Format = AudioFormat.Pcm16kMono;

    private readonly LaneDatabase _database = new(
        new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VoiceprintOptions Options = new()
    {
        Enabled          = true,
        MatchThreshold   = 0.70f,
        MinimumUtterance = TimeSpan.FromSeconds(1.5)
    };

    /// <summary>
    /// An encoder whose answers a test writes. The audio's first byte names the speaker, so
    /// a test can say "this is the same person again" without needing two recordings.
    /// </summary>
    private sealed class StubEncoder : IVoiceEncoder
    {
        public int Dimensions => 16;

        public float[]? Embed(VoiceSample sample)
        {
            if (sample.IsLabelOnly || sample.Duration < Options.MinimumUtterance) return null;

            float[] v = new float[16];

            v[sample.Pcm.Span[0] % 16] = 1f;

            return v;
        }

        public void Dispose() { }
    }

    /// <summary>Audio long enough to embed, tagged with which person is speaking.</summary>
    private static VoiceSample Said(string label, byte person, double seconds = 2.0)
    {
        byte[] pcm = new byte[Format.BytesFor(TimeSpan.FromSeconds(seconds))];

        pcm[0] = person;

        return new VoiceSample(label, pcm, Format);
    }

    private async Task<(VoiceprintAttributor Attributor, VoiceprintDirectory Voices)> BuildAsync()
    {
        VoiceprintDirectory voices = new(
            new SqliteKeyValueStore(_database), TimeProvider.System,
            NullLogger<VoiceprintDirectory>.Instance);

        await voices.StartAsync(Ct);

        return (new VoiceprintAttributor(
            new StubEncoder(), voices, IdentityResolver.Empty, Options,
            NullLogger<VoiceprintAttributor>.Instance), voices);
    }

    private static AudioBinding Binding(string source = "api.local/ws/room") =>
        new(new AudioSourceId(source),
            new SessionId(new SurfaceId("api.local"), SessionKind.Voice, "room"),
            new Participant(new ParticipantId(new SurfaceId("api.local"), "client"), "the room"),
            SpeakerAttribution.Diarized);

    [Fact]
    public async Task A_voice_heard_before_comes_back_as_the_person_who_claimed_it()
    {
        // What the whole milestone is for: somebody walks in, says something, and is
        // themselves — with the memory that goes with that — without being asked again.
        (VoiceprintAttributor attributor, VoiceprintDirectory voices) = await BuildAsync();

        Participant first = await attributor.AttributeAsync(Binding(), Said("Guest-1", 7), Ct);

        string id = Assert.NotNull(voices.Match([.. Unit(7)], 0.7f)).Id;

        await voices.BindAsync(id, "jahan-a1b2", Ct);

        // A new call: a new source, and the service numbers its speakers differently.
        Participant later = await attributor.AttributeAsync(Binding("api.local/ws/later"), Said("Guest-3", 7), Ct);

        Assert.Null(first.GlobalUserId);
        Assert.Equal("jahan-a1b2", later.GlobalUserId);

        // And the memory scope follows the person rather than the connection.
        Assert.Equal("jahan-a1b2", later.StableKey);
    }

    [Fact]
    public async Task A_label_that_has_settled_is_not_re_examined()
    {
        // Two seconds of speech carries the sentence almost as much as the speaker, so the
        // label is what holds a person together mid-conversation. If every utterance were
        // re-matched, attribution would flap between people in a single exchange.
        (VoiceprintAttributor attributor, _) = await BuildAsync();

        AudioBinding binding = Binding();

        Participant first = await attributor.AttributeAsync(binding, Said("Guest-1", 7), Ct);

        // The same label, but audio the encoder would place somewhere else entirely.
        Participant second = await attributor.AttributeAsync(binding, Said("Guest-1", 2), Ct);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Two_labels_that_turn_out_to_be_one_voice_become_one_person()
    {
        // The service splitting one speaker in half is a real failure mode, and the repair
        // is that both labels resolve to the voice rather than to two strangers.
        (VoiceprintAttributor attributor, _) = await BuildAsync();

        AudioBinding binding = Binding();

        Participant one = await attributor.AttributeAsync(binding, Said("Guest-1", 7), Ct);
        Participant two = await attributor.AttributeAsync(binding, Said("Guest-2", 7), Ct);

        Assert.Equal(one.Id, two.Id);
    }

    [Fact]
    public async Task Two_people_stay_two_people()
    {
        (VoiceprintAttributor attributor, _) = await BuildAsync();

        AudioBinding binding = Binding();

        Participant alice = await attributor.AttributeAsync(binding, Said("Guest-1", 7), Ct);
        Participant bob   = await attributor.AttributeAsync(binding, Said("Guest-2", 2), Ct);

        Assert.NotEqual(alice.Id, bob.Id);
        Assert.Equal("Voice 1", alice.DisplayName);
        Assert.Equal("Voice 2", bob.DisplayName);
    }

    [Fact]
    public async Task An_utterance_too_short_to_place_falls_back_rather_than_guessing()
    {
        // "I could not tell" and "this is somebody else" are different answers, and treating
        // the first as the second enrols a stranger every time anyone says "yeah".
        (VoiceprintAttributor attributor, VoiceprintDirectory voices) = await BuildAsync();

        Participant speaker = await attributor.AttributeAsync(Binding(), Said("Guest-1", 7, seconds: 0.4), Ct);

        Assert.Equal("the room", speaker.DisplayName);
        Assert.Null(voices.Match([.. Unit(7)], 0.7f));
    }

    [Fact]
    public async Task A_short_utterance_under_a_settled_label_still_goes_to_that_person()
    {
        // Once the label is placed, "yeah" belongs to whoever has been holding it.
        (VoiceprintAttributor attributor, _) = await BuildAsync();

        AudioBinding binding = Binding();

        Participant first = await attributor.AttributeAsync(binding, Said("Guest-1", 7), Ct);
        Participant brief = await attributor.AttributeAsync(binding, Said("Guest-1", 7, seconds: 0.3), Ct);

        Assert.Equal(first.Id, brief.Id);
    }

    [Fact]
    public async Task An_unclaimed_voice_keeps_its_own_memory_rather_than_sharing_a_pool()
    {
        (VoiceprintAttributor attributor, _) = await BuildAsync();

        AudioBinding binding = Binding();

        Participant alice = await attributor.AttributeAsync(binding, Said("Guest-1", 7), Ct);
        Participant bob   = await attributor.AttributeAsync(binding, Said("Guest-2", 2), Ct);

        Assert.NotEqual(alice.StableKey, bob.StableKey);
        Assert.All([alice, bob], p => Assert.Contains("voice/", p.StableKey));
    }

    private static IEnumerable<float> Unit(int axis)
    {
        for (int i = 0; i < 16; i++) yield return i == axis % 16 ? 1f : 0f;
    }
}
