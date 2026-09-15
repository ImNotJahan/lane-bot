using System.Collections.Concurrent;
using System.Globalization;
using Lane.Core.Memory;

namespace Lane.Surfaces.Discord;

/// <summary>Which Discord users have run /opt-in, keyed by Discord user id and shared by every bot instance.</summary>
public sealed class DiscordConsent(IKeyValueStore? store)
{
    private const string Prefix = "discord-opt-in:";

    private static readonly ScopeKey Scope = new("global");

    private readonly ConcurrentDictionary<ulong, bool> _known = new();

    public async ValueTask<bool> IsOptedInAsync(ulong userId, CancellationToken ct)
    {
        if (_known.TryGetValue(userId, out bool optedIn)) return optedIn;
        if (store is null) return false;

        optedIn = await store.GetAsync<bool>(Scope, Key(userId), ct).ConfigureAwait(false);

        return _known.GetOrAdd(userId, optedIn);
    }

    public async ValueTask SetAsync(ulong userId, bool optedIn, CancellationToken ct)
    {
        if (store is not null)
        {
            if (optedIn) await store.SetAsync(Scope, Key(userId), true, ct).ConfigureAwait(false);
            else         await store.RemoveAsync(Scope, Key(userId), ct).ConfigureAwait(false);
        }

        _known[userId] = optedIn;
    }

    private static string Key(ulong userId) => Prefix + userId.ToString(CultureInfo.InvariantCulture);
}
