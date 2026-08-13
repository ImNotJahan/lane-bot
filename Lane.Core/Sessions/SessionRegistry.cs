using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Lane.Core.Events;
using Lane.Core.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Core.Sessions;

public enum SessionLifecycle { Opened, Updated, ChannelAttached, ChannelDetached, Closed }

public sealed record SessionLifecycleEvent(SessionLifecycle Kind, SessionId Session, string? Detail = null);

public interface ISessionRegistry
{
    /// <summary>Idempotent. Surfaces call this on every inbound; the descriptor is merged in.</summary>
    Session GetOrCreate(SessionDescriptor descriptor);

    bool TryGet(SessionId id, [NotNullWhen(true)] out Session? session);

    IReadOnlyCollection<Session> Active { get; }

    /// <summary>Attaches an output channel to an existing session. Dispose the token to detach.</summary>
    IDisposable Attach(ISessionChannel channel);

    ValueTask CloseAsync(SessionId id, string reason);

    event Action<SessionLifecycleEvent>? Lifecycle;
}

public sealed class SessionRegistry : ISessionRegistry, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ITurnExecutor  _executor;
    private readonly TurnBudget     _budget;
    private readonly SessionOptions _options;
    private readonly TimeProvider   _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SessionRegistry> _log;
    private readonly IEventBus _bus;
    private readonly CancellationTokenSource  _lifetime = new();
    private int _disposed;

    public SessionRegistry(
        ITurnExecutor           executor,
        TurnBudget              budget,
        IOptions<SessionOptions> options,
        ILoggerFactory          loggerFactory,
        IEventBus?              bus  = null,
        TimeProvider?           time = null)
    {
        _bus           = bus ?? new NullEventBus();
        _executor      = executor;
        _budget        = budget;
        _options       = options.Value;
        _time          = time ?? TimeProvider.System;
        _loggerFactory = loggerFactory;
        _log           = loggerFactory.CreateLogger<SessionRegistry>();
    }

    public event Action<SessionLifecycleEvent>? Lifecycle;

    public IReadOnlyCollection<Session> Active => [.. _sessions.Values];

    public Session GetOrCreate(SessionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        bool created = false;

        Session session = _sessions.GetOrAdd(descriptor.Id.Value, _ =>
        {
            created = true;

            Session fresh = new(
                descriptor, _executor, _budget, _options, _time,
                _loggerFactory.CreateLogger($"Lane.Session.{descriptor.Id.Surface.Value}"));

            fresh.Start(_lifetime.Token);
            return fresh;
        });

        if (created)
        {
            _log.LogInformation("Session opened: {Session} ({Name})", descriptor.Id, descriptor.DisplayName);
            Raise(new SessionLifecycleEvent(SessionLifecycle.Opened, descriptor.Id, descriptor.DisplayName));
        }
        else
        {
            // Refresh participants, display name, capabilities — they drift as people join and leave.
            session.UpdateDescriptor(descriptor);
            Raise(new SessionLifecycleEvent(SessionLifecycle.Updated, descriptor.Id));
        }

        return session;
    }

    public bool TryGet(SessionId id, [NotNullWhen(true)] out Session? session) =>
        _sessions.TryGetValue(id.Value, out session);

    public IDisposable Attach(ISessionChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (!TryGet(channel.Id, out Session? session))
            throw new InvalidOperationException(
                $"No session {channel.Id} to attach to. Call GetOrCreate with a descriptor first.");

        IDisposable detach = session.AttachChannel(channel);

        Raise(new SessionLifecycleEvent(SessionLifecycle.ChannelAttached, channel.Id, channel.Surface.Value));

        return new DetachToken(detach, () =>
            Raise(new SessionLifecycleEvent(SessionLifecycle.ChannelDetached, channel.Id, channel.Surface.Value)));
    }

    public async ValueTask CloseAsync(SessionId id, string reason)
    {
        if (!_sessions.TryRemove(id.Value, out Session? session)) return;

        _log.LogInformation("Session closed: {Session} ({Reason})", id, reason);

        await session.DisposeAsync().ConfigureAwait(false);

        Raise(new SessionLifecycleEvent(SessionLifecycle.Closed, id, reason));
    }

    private void Raise(SessionLifecycleEvent evt)
    {
        try { Lifecycle?.Invoke(evt); }
        catch (Exception ex) { _log.LogWarning(ex, "A session lifecycle subscriber threw"); }

        _bus.Publish(new SessionEvent(evt.Kind, evt.Session, evt.Detail));
    }

    public async ValueTask DisposeAsync()
    {
        // The registry is resolvable as both SessionRegistry and ISessionRegistry, so the
        // container disposes it once per service type. Disposal has to be idempotent.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _lifetime.CancelAsync().ConfigureAwait(false);

        foreach (Session session in _sessions.Values) await session.DisposeAsync().ConfigureAwait(false);

        _sessions.Clear();
        _lifetime.Dispose();
    }

    private sealed class DetachToken(IDisposable inner, Action onDetach) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            inner.Dispose();
            onDetach();
        }
    }
}
