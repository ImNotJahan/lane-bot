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
/// </summary>
public sealed class IdentityResolver : IIdentityResolver
{
    private readonly FrozenDictionary<string, string> _links;

    /// <param name="identities">Global user id → the surface-local ids belonging to them,
    /// each written <c>surface:localId</c>.</param>
    public IdentityResolver(
        IReadOnlyDictionary<string, IReadOnlyList<string>> identities,
        ILogger<IdentityResolver>? log = null)
    {
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

        _links = links.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        if (_links.Count > 0)
            log?.LogInformation("Linked {Count} identities across surfaces", _links.Count);
    }

    public static IdentityResolver Empty { get; } = new(new Dictionary<string, IReadOnlyList<string>>());

    public Participant Resolve(ParticipantId id, string displayName) =>
        new(id, displayName, _links.GetValueOrDefault(id.ToString()));
}
