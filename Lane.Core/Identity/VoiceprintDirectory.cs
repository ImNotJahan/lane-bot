using System.Collections.Concurrent;
using System.Security.Cryptography;
using Lane.Core.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Identity;

/// <summary>
/// One voice Lane has heard, and who — if anyone — has claimed it.
/// </summary>
/// <param name="Centroid">
/// The average of the samples enrolled for this voice, unit length. An average rather than a
/// single recording because one utterance carries the sentence as much as the speaker.
/// </param>
/// <param name="BoundTo">
/// The <see cref="Participant.StableKey"/> of the person this voice belongs to, or null while
/// it is still a stranger. Set only by somebody proving it in conversation — never by a match.
/// </param>
public sealed record Voiceprint(
    string Id,
    float[] Centroid,
    int Samples,
    DateTimeOffset EnrolledAt,
    string? BoundTo = null,
    DateTimeOffset? BoundAt = null);

public readonly record struct VoiceprintMatch(string Id, float Similarity);

/// <summary>
/// A voice, treated as an account somebody speaks from.
///
/// Giving a recognised voice a <see cref="ParticipantId"/> of its own is what lets the rest
/// of the system stay ignorant of any of this. A stranger in a room accumulates their own
/// private memory instead of sharing an anonymous pool with everyone else who has ever
/// walked past the microphone, a name they choose is stored the way anyone else's is, and
/// the transcript says who spoke without a single call site knowing what a voiceprint is.
///
/// The convention lives here so that the half which mints these ids and the half which
/// reads them back cannot drift apart.
/// </summary>
public static class VoiceAccounts
{
    private const string Prefix = "voice/";

    public static ParticipantId For(SurfaceId surface, string voiceprintId) =>
        new(surface, Prefix + voiceprintId);

    /// <summary>The voice this account is, or null if it is an ordinary account.</summary>
    public static string? VoiceprintOf(ParticipantId account) =>
        account.LocalId.StartsWith(Prefix, StringComparison.Ordinal)
            ? account.LocalId[Prefix.Length..]
            : null;
}

/// <summary>
/// The voices Lane knows, kept apart from the accounts she knows.
///
/// Separate from <see cref="IIdentityDirectory"/> on purpose, and the separation is the
/// design rather than an accident of layering. An account link is a fact somebody proved;
/// a voice match is a measurement with a threshold, and measurements are wrong sometimes.
/// Keeping them in one store would make "these two accounts are one person" and "this
/// sounded like her" the same kind of claim, and there would be no way afterwards to ask
/// which of the two some piece of memory rested on.
///
/// So matching reads from here and nothing else writes a person into it. The one thing that
/// does is somebody saying, in a way that could only have come from them, that this voice
/// is theirs.
/// </summary>
public interface IVoiceprintDirectory
{
    /// <summary>
    /// False when nothing durable is backing this. A voice enrolled into memory that a
    /// restart erases is worse than one never offered — it invites somebody to claim a
    /// voice that will be a stranger again tomorrow — so the tools refuse instead.
    /// </summary>
    bool Durable { get; }

    /// <summary>
    /// The closest voice to this one above the threshold, or null. In memory and synchronous:
    /// it runs for every utterance anybody speaks.
    /// </summary>
    VoiceprintMatch? Match(ReadOnlySpan<float> embedding, float threshold);

    Voiceprint? Get(string id);

    /// <summary>Every voice claimed by this person. Usually one, occasionally more.</summary>
    IReadOnlyList<Voiceprint> BoundTo(string stableKey);

    /// <summary>Remembers a voice that did not match anything, and returns its new id.</summary>
    ValueTask<string> EnrolAsync(ReadOnlyMemory<float> embedding, CancellationToken ct);

    /// <summary>Folds another sample of a known voice into its average.</summary>
    ValueTask ReinforceAsync(string id, ReadOnlyMemory<float> embedding, CancellationToken ct);

    /// <summary>Records that this voice is this person's. The one write that asserts identity.</summary>
    ValueTask BindAsync(string id, string stableKey, CancellationToken ct);

    ValueTask ForgetAsync(string id, CancellationToken ct);
}

/// <summary>Used when no durable store is configured: nothing is known, and nothing can be.</summary>
public sealed class NullVoiceprintDirectory : IVoiceprintDirectory
{
    public static NullVoiceprintDirectory Instance { get; } = new();

    public bool Durable => false;

    public VoiceprintMatch? Match(ReadOnlySpan<float> embedding, float threshold) => null;

    public Voiceprint? Get(string id) => null;

    public IReadOnlyList<Voiceprint> BoundTo(string stableKey) => [];

    public ValueTask<string> EnrolAsync(ReadOnlyMemory<float> embedding, CancellationToken ct) =>
        throw new NotSupportedException("No durable store is configured for voiceprints.");

    public ValueTask ReinforceAsync(string id, ReadOnlyMemory<float> embedding, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask BindAsync(string id, string stableKey, CancellationToken ct) =>
        throw new NotSupportedException("No durable store is configured for voiceprints.");

    public ValueTask ForgetAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
}

/// <summary>
/// The voices kept in the key-value store, read into memory at startup.
///
/// Loaded once for the same reason the identity directory is: matching happens on the path
/// of every utterance and has to be synchronous, and comparing against a few dozen vectors
/// in memory is nothing while a round trip to storage per sentence is not.
/// </summary>
public sealed class VoiceprintDirectory(
    IKeyValueStore store,
    TimeProvider time,
    ILogger<VoiceprintDirectory> log) : IVoiceprintDirectory, IHostedService
{
    private const string Prefix = "voiceprint:";

    private static readonly ScopeKey Scope = new("global");

    private readonly ConcurrentDictionary<string, Voiceprint> _prints = new(StringComparer.OrdinalIgnoreCase);

    public bool Durable => true;

    public async Task StartAsync(CancellationToken ct)
    {
        IReadOnlyList<string> keys = await store.ListKeysAsync(Scope, Prefix, ct).ConfigureAwait(false);

        foreach (string key in keys)
        {
            Voiceprint? print = await store.GetAsync<Voiceprint>(Scope, key, ct).ConfigureAwait(false);

            if (print is not null) _prints[print.Id] = print;
        }

        if (!_prints.IsEmpty)
            log.LogInformation("Loaded {Count} voice(s), {Bound} of them claimed",
                _prints.Count, _prints.Values.Count(p => p.BoundTo is not null));
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public VoiceprintMatch? Match(ReadOnlySpan<float> embedding, float threshold)
    {
        if (embedding.IsEmpty) return null;

        string? best = null;
        float score = threshold;

        foreach (Voiceprint print in _prints.Values)
        {
            float similarity = Similarity(embedding, print.Centroid);

            // Strictly better, so the first of two equally close voices does not win by
            // enumeration order — a tie means neither is a match worth acting on.
            if (similarity > score) (best, score) = (print.Id, similarity);
        }

        return best is null ? null : new VoiceprintMatch(best, score);
    }

    public Voiceprint? Get(string id) => _prints.GetValueOrDefault(id);

    public IReadOnlyList<Voiceprint> BoundTo(string stableKey) =>
        [.. _prints.Values.Where(p =>
            string.Equals(p.BoundTo, stableKey, StringComparison.OrdinalIgnoreCase))];

    public async ValueTask<string> EnrolAsync(ReadOnlyMemory<float> embedding, CancellationToken ct)
    {
        string id = NewId();

        Voiceprint print = new(id, embedding.ToArray(), Samples: 1, time.GetUtcNow());

        await SaveAsync(print, ct).ConfigureAwait(false);

        log.LogInformation("Remembered a new voice, {Id}", id);

        return id;
    }

    public async ValueTask ReinforceAsync(string id, ReadOnlyMemory<float> embedding, CancellationToken ct)
    {
        if (!_prints.TryGetValue(id, out Voiceprint? print)) return;

        float[] centroid = new float[print.Centroid.Length];

        if (embedding.Length != centroid.Length) return;

        // A running mean. Each new sample counts for less than the one before it, so a
        // profile settles rather than chasing whichever sentence was said most recently.
        for (int i = 0; i < centroid.Length; i++)
            centroid[i] = (print.Centroid[i] * print.Samples + embedding.Span[i]) / (print.Samples + 1);

        await SaveAsync(print with
        {
            Centroid = Normalise(centroid),
            Samples  = print.Samples + 1
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask BindAsync(string id, string stableKey, CancellationToken ct)
    {
        if (!_prints.TryGetValue(id, out Voiceprint? print))
            throw new InvalidOperationException($"There is no voice here called '{id}'.");

        await SaveAsync(print with { BoundTo = stableKey, BoundAt = time.GetUtcNow() }, ct)
            .ConfigureAwait(false);

        log.LogInformation("Voice {Id} is {Person} from now on", id, stableKey);
    }

    public async ValueTask ForgetAsync(string id, CancellationToken ct)
    {
        await store.RemoveAsync(Scope, Prefix + id, ct).ConfigureAwait(false);

        _prints.TryRemove(id, out _);

        log.LogInformation("Forgot voice {Id}", id);
    }

    /// <summary>
    /// Written to the store before the in-memory copy, the same way identity links are: a
    /// voice that is live for this run but absent after a restart would let somebody claim
    /// it twice and mean it once.
    /// </summary>
    private async ValueTask SaveAsync(Voiceprint print, CancellationToken ct)
    {
        await store.SetAsync(Scope, Prefix + print.Id, print, ct).ConfigureAwait(false);

        _prints[print.Id] = print;
    }

    private string NewId()
    {
        string id;

        do
        {
            id = $"v-{RandomNumberGenerator.GetHexString(8, lowercase: true)}";
        }
        while (_prints.ContainsKey(id));

        return id;
    }

    private static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;

        float sum = 0f;

        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];

        return sum;
    }

    private static float[] Normalise(float[] vector)
    {
        double sum = 0;

        foreach (float v in vector) sum += (double)v * v;

        if (sum <= 0) return vector;

        float scale = (float)(1.0 / Math.Sqrt(sum));

        for (int i = 0; i < vector.Length; i++) vector[i] *= scale;

        return vector;
    }
}
