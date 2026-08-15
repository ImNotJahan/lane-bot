using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Identity;

/// <summary>
/// Links surface-local identities to one person.
///
/// This is what makes <see cref="Lane.Core.Memory.MemoryScope.User"/> mean anything: the
/// Discord account and the terminal login and the API client that are all Jahan share one
/// memory, and two strangers never do.
///
/// The map is configuration rather than inference. Guessing that two accounts with the
/// same display name are the same person would merge two people's memories, which is a
/// worse failure than not linking them at all.
///
/// <paramref name="runtime"/> is the one other way in, and it is not inference either: a
/// link made through <c>link_identity</c> is proof that one person holds both accounts,
/// because the code has to be repeated from the second one. Configuration still wins where
/// the two disagree — an operator's map is not something a conversation can edit.
///
/// It also carries the name someone asked to be called. Applying it here rather than at
/// each call site is what makes one request cover every place a name appears: speaker
/// attribution in the prompt, the transcript, <c>list_sessions</c>, the dashboard. A surface
/// keeps passing the name the account carries; this is the last word on it.
/// </summary>
public sealed class IdentityResolver : IIdentityResolver
{
    private readonly FrozenDictionary<string, string> _links;
    private readonly FrozenSet<string>                _people;
    private readonly IIdentityDirectory?              _runtime;

    /// <param name="identities">Global user id → the surface-local ids belonging to them,
    /// each written <c>surface:localId</c>.</param>
    /// <param name="log">For reporting how much was linked at startup.</param>
    /// <param name="runtime">Links proved and names chosen in conversation, if a durable
    /// store holds them.</param>
    public IdentityResolver(
        IReadOnlyDictionary<string, IReadOnlyList<string>> identities,
        ILogger<IdentityResolver>? log = null,
        IIdentityDirectory? runtime = null)
    {
        _runtime = runtime;
        Dictionary<string, string> links = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string globalId, IReadOnlyList<string> locals) in identities)
        {
            foreach (string local in locals)
            {
                if (string.IsNullOrWhiteSpace(local)) continue;

                if (links.TryGetValue(local, out string? existing) && existing != globalId)
                {
                    // One account cannot be two people. Left unreported this would silently
                    // route someone's memory to whichever entry happened to load last.
                    throw new InvalidOperationException(
                        $"Identity '{local}' is claimed by both '{existing}' and '{globalId}'.");
                }

                links[local] = globalId;
            }
        }

        _links  = links.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _people = identities.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        if (_links.Count > 0)
            log?.LogInformation("Linked {Count} identities across surfaces", _links.Count);
    }

    public static IdentityResolver Empty { get; } = new(new Dictionary<string, IReadOnlyList<string>>());

    public Participant Resolve(ParticipantId id, string displayName)
    {
        // Configuration first: an account an operator has already placed cannot be moved by
        // anything agreed in a conversation.
        string? globalId = _links.GetValueOrDefault(id.ToString()) ?? _runtime?.GlobalIdFor(id);

        // Keyed on the person where there is one, so a name chosen on Discord is the name
        // in the terminal too.
        string? chosen = _runtime?.NameFor(globalId ?? id.ToString());

        return new Participant(id, chosen ?? displayName, globalId);
    }

    public bool IsPerson(string globalUserId) =>
        _people.Contains(globalUserId) || _runtime?.IsPerson(globalUserId) == true;
}
