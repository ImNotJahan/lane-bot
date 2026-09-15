using System.Collections.Concurrent;
using Lane.Core.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Sessions;

/// <summary>
/// Per-conversation overrides of <see cref="Pipeline.Stages.ResponsePolicyOptions.DefaultThreshold"/>,
/// keyed by <see cref="SessionDescriptor.MemoryGroup"/>.
/// </summary>
public interface ISessionThresholds
{
    /// <summary>The override for this conversation, or null when it uses the default.</summary>
    float? For(SessionDescriptor? session);

    bool Durable { get; }

    /// <summary>Records an override against a memory group, or clears it when <paramref name="threshold"/> is null.</summary>
    ValueTask SetAsync(string memoryGroup, float? threshold, CancellationToken ct);
}

public sealed class NullSessionThresholds : ISessionThresholds
{
    public static NullSessionThresholds Instance { get; } = new();

    public float? For(SessionDescriptor? session) => null;

    public bool Durable => false;

    public ValueTask SetAsync(string memoryGroup, float? threshold, CancellationToken ct) =>
        throw new NotSupportedException("No durable store is configured for response thresholds.");
}

/// <summary>Overrides kept in the key-value store, read into memory at startup.</summary>
public sealed class SessionThresholds(IKeyValueStore store, ILogger<SessionThresholds> log)
    : ISessionThresholds, IHostedService
{
    private const string Prefix = "response-threshold:";

    private static readonly ScopeKey Scope = new("global");

    private readonly ConcurrentDictionary<string, float> _thresholds = new(StringComparer.Ordinal);

    public bool Durable => true;

    public async Task StartAsync(CancellationToken ct)
    {
        IReadOnlyList<string> keys = await store.ListKeysAsync(Scope, Prefix, ct).ConfigureAwait(false);

        foreach (string key in keys)
        {
            float? value = await store.GetAsync<float?>(Scope, key, ct).ConfigureAwait(false);

            if (value is { } threshold) _thresholds[key[Prefix.Length..]] = Math.Clamp(threshold, 0f, 1f);
        }

        if (!_thresholds.IsEmpty)
            log.LogInformation("Loaded {Count} response threshold(s)", _thresholds.Count);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public float? For(SessionDescriptor? session) =>
        session is not null && _thresholds.TryGetValue(session.MemoryGroup, out float threshold) ? threshold : null;

    public async ValueTask SetAsync(string memoryGroup, float? threshold, CancellationToken ct)
    {
        if (threshold is not { } value)
        {
            await store.RemoveAsync(Scope, Prefix + memoryGroup, ct).ConfigureAwait(false);
            _thresholds.TryRemove(memoryGroup, out _);

            log.LogInformation("Reset the response threshold of {Group}", memoryGroup);
            return;
        }

        value = Math.Clamp(value, 0f, 1f);

        await store.SetAsync<float?>(Scope, Prefix + memoryGroup, value, ct).ConfigureAwait(false);
        _thresholds[memoryGroup] = value;

        log.LogInformation("Set the response threshold of {Group} to {Threshold:0.00}", memoryGroup, value);
    }
}
