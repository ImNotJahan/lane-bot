using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Memory;

public sealed class MemoryOptions
{
    public List<MemoryHandlerOptions> Handlers { get; set; } = [];

    /// <summary>Unpinned handler instances idle this long are saved and released.</summary>
    public TimeSpan IdleEviction { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often dirty handler state is written back.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Backstop on live instances; the least recently used are evicted past this.</summary>
    public int MaxInstances { get; set; } = 512;
}

/// <summary>
/// One handler instance plus the lock that makes it safe.
///
/// Handlers hold plain mutable state, and a Global-scoped one is written by every session
/// pump and the monologue at the same time. Serialising here rather than asking every
/// implementation to synchronise is the single most important correctness property of the
/// memory layer.
/// </summary>
internal sealed class ScopedHandler(IMemoryHandler handler, ScopeKey key, bool pinned) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int  _dirty;
    private long _lastUsedTicks = DateTimeOffset.UtcNow.UtcTicks;

    public IMemoryHandler Handler { get; } = handler;
    public ScopeKey       Key     { get; } = key;
    public bool           Pinned  { get; } = pinned;

    public bool IsDirty => Volatile.Read(ref _dirty) != 0;

    public DateTimeOffset LastUsed => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

    public async ValueTask<T> ReadAsync<T>(Func<IMemoryHandler, ValueTask<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            Touch();
            return await action(Handler).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask WriteAsync(Func<IMemoryHandler, ValueTask> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            Touch();
            await action(Handler).ConfigureAwait(false);
            Volatile.Write(ref _dirty, 1);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Snapshots state under the lock and clears the dirty flag.</summary>
    public async ValueTask<JsonNode?> SnapshotAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Cleared before the snapshot so a write landing during the save is not lost —
            // it re-marks dirty and gets picked up by the next flush.
            Volatile.Write(ref _dirty, 0);

            return await Handler.SaveStateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _dirty, 1);
            throw;
        }
        finally { _gate.Release(); }
    }

    private void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTimeOffset.UtcNow.UtcTicks);

    public async ValueTask DisposeAsync()
    {
        await Handler.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>
/// Creates and holds handler instances, one per (handler id, scope key).
///
/// This is what "configurable scope per handler" actually costs: the same configured
/// handler becomes many live instances, created on first touch, loaded from the state
/// store, flushed on a timer and released when idle.
/// </summary>
public sealed class MemoryHandlerPool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<(string HandlerId, string Key), Lazy<Task<ScopedHandler>>> _instances =
        new();

    private readonly Dictionary<string, IMemoryHandlerFactory> _factories;
    private readonly IStateStore      _state;
    private readonly IServiceProvider _services;
    private readonly MemoryOptions    _options;
    private readonly ILogger<MemoryHandlerPool> _log;

    public MemoryHandlerPool(
        IEnumerable<IMemoryHandlerFactory> factories,
        IStateStore      state,
        IServiceProvider services,
        MemoryOptions    options,
        ILogger<MemoryHandlerPool> log)
    {
        _factories = factories.ToDictionary(f => f.TypeName, StringComparer.OrdinalIgnoreCase);
        _state     = state;
        _services  = services;
        _options   = options;
        _log       = log;

        foreach (MemoryHandlerOptions handler in options.Handlers)
        {
            if (!_factories.ContainsKey(handler.Type))
                throw new InvalidOperationException(
                    $"Memory handler '{handler.Id}' names unknown type '{handler.Type}'. " +
                    $"Known types: {string.Join(", ", _factories.Keys)}.");
        }

        List<string> duplicates = [.. options.Handlers.GroupBy(h => h.Id, StringComparer.OrdinalIgnoreCase)
                                                      .Where(g => g.Count() > 1).Select(g => g.Key)];

        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Memory handler ids must be unique; duplicated: {string.Join(", ", duplicates)}. " +
                "Ids key persisted state, so a collision would merge two handlers' storage.");
    }

    public IReadOnlyList<MemoryHandlerOptions> Handlers => _options.Handlers;

    public int LiveInstances => _instances.Count;

    internal Task<ScopedHandler> GetAsync(MemoryHandlerOptions options, ScopeKey key)
    {
        Lazy<Task<ScopedHandler>> lazy = _instances.GetOrAdd(
            (options.Id, key.Value),
            _ => new Lazy<Task<ScopedHandler>>(() => CreateAsync(options, key), LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    private async Task<ScopedHandler> CreateAsync(MemoryHandlerOptions options, ScopeKey key)
    {
        IMemoryHandler handler = _factories[options.Type].Create(options, key, _services);

        JsonNode? saved = await _state.LoadAsync(options.Id, key, CancellationToken.None).ConfigureAwait(false);

        if (saved is not null)
        {
            try
            {
                await handler.LoadStateAsync(saved, CancellationToken.None).ConfigureAwait(false);
                _log.LogDebug("Restored {Handler} at {Key}", options.Id, key);
            }
            catch (Exception ex)
            {
                // Discard unreadable state rather than refusing to start. v2 threw here,
                // which turned any schema drift into a process that would not boot.
                _log.LogWarning(ex, "Discarding unreadable state for {Handler} at {Key}", options.Id, key);
            }
        }

        _log.LogDebug("Created {Handler} at {Key} ({Live} live)", options.Id, key, _instances.Count);

        return new ScopedHandler(handler, key, options.Pinned);
    }

    /// <summary>
    /// Lets handlers catch up on work they deferred during a turn — summarising, keeping a
    /// profile current. Runs off the turn path, so a model call here costs nobody a wait.
    /// </summary>
    public async ValueTask MaintainAsync(CancellationToken ct)
    {
        foreach (Lazy<Task<ScopedHandler>> lazy in _instances.Values)
        {
            if (!lazy.IsValueCreated) continue;

            ScopedHandler scoped;

            try { scoped = await lazy.Value.ConfigureAwait(false); }
            catch { continue; }

            if (scoped.Handler is not IMaintainedMemory maintained || !maintained.NeedsMaintenance) continue;

            try
            {
                // Under the handler's own lock, so a turn writing to it waits rather than
                // racing a rewrite of the same state.
                await scoped.WriteAsync(_ => maintained.MaintainAsync(ct), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Maintenance failed for {Handler} at {Key}", scoped.Handler.Id, scoped.Key);
            }
        }
    }

    /// <summary>Writes back every instance with unsaved changes.</summary>
    public async ValueTask FlushAsync(CancellationToken ct)
    {
        foreach (Lazy<Task<ScopedHandler>> lazy in _instances.Values)
        {
            if (!lazy.IsValueCreated) continue;

            ScopedHandler scoped;

            try { scoped = await lazy.Value.ConfigureAwait(false); }
            catch { continue; }                                   // creation failed; nothing to save

            if (!scoped.IsDirty) continue;

            await SaveAsync(scoped, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask SaveAsync(ScopedHandler scoped, CancellationToken ct)
    {
        try
        {
            JsonNode? state = await scoped.SnapshotAsync(ct).ConfigureAwait(false);

            if (state is null) return;

            await _state.SaveAsync(scoped.Handler.Id, scoped.Key, state, schemaVersion: 1, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Failed to save state for {Handler} at {Key}", scoped.Handler.Id, scoped.Key);
        }
    }

    /// <summary>Saves and releases instances idle past the configured timeout.</summary>
    public async ValueTask EvictIdleAsync(CancellationToken ct)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _options.IdleEviction;

        List<(string, string)> evictable = [];

        foreach (KeyValuePair<(string, string), Lazy<Task<ScopedHandler>>> entry in _instances)
        {
            if (!entry.Value.IsValueCreated) continue;

            ScopedHandler scoped;
            try { scoped = await entry.Value.Value.ConfigureAwait(false); }
            catch { continue; }

            if (scoped.Pinned || scoped.LastUsed > cutoff) continue;

            evictable.Add(entry.Key);
        }

        foreach ((string, string) key in evictable)
        {
            if (!_instances.TryRemove(key, out Lazy<Task<ScopedHandler>>? lazy)) continue;

            ScopedHandler scoped = await lazy.Value.ConfigureAwait(false);

            await SaveAsync(scoped, ct).ConfigureAwait(false);
            await scoped.DisposeAsync().ConfigureAwait(false);

            _log.LogDebug("Evicted idle {Handler} at {Key}", scoped.Handler.Id, scoped.Key);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (Lazy<Task<ScopedHandler>> lazy in _instances.Values)
        {
            if (!lazy.IsValueCreated) continue;

            try { await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch { /* already faulted */ }
        }

        _instances.Clear();
    }
}
