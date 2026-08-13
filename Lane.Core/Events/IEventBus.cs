using System.Collections.Concurrent;
using Lane.Core.Identity;
using Lane.Core.Models;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Events;

public interface ILaneEvent
{
    DateTimeOffset At => DateTimeOffset.UtcNow;
}

/// <summary>
/// One place everything interesting is announced, and anywhere that cares subscribes.
///
/// v2 wired observers directly: the emoticon travelled on a C# event from the orchestrator
/// into one HTTP server, and the token view had to hold a reference to every model instance
/// in order to add a handler to each. Neither survives having several of anything. Here the
/// dashboard, the face server and the API's event stream are independent subscribers that
/// know nothing about each other, or about how many models and surfaces exist.
/// </summary>
public interface IEventBus
{
    void Publish<T>(T evt) where T : ILaneEvent;

    IObservable<T> Observe<T>() where T : ILaneEvent;
}

// ---- events ---------------------------------------------------------------

public sealed record TokenUsageEvent(string ModelInstanceId, string? Role, TokenUsage Usage) : ILaneEvent;

public sealed record SessionEvent(SessionLifecycle Kind, SessionId Session, string? Detail) : ILaneEvent;

public sealed record TurnStarted(SessionId Session, TurnKind Kind, int IncomingCount) : ILaneEvent;

public sealed record TurnCompleted(
    SessionId Session, TurnKind Kind, bool Suppressed, int ToolCalls, TimeSpan Duration) : ILaneEvent;

public sealed record TurnFailed(SessionId Session, string Error) : ILaneEvent;

public sealed record ToolInvokedEvent(string Tool, string? Session) : ILaneEvent;

public sealed record ToolCompletedEvent(string Tool, bool IsError, TimeSpan Duration) : ILaneEvent;

public sealed record SurfaceStateChanged(SurfaceId Surface, bool Connected, string? Detail = null) : ILaneEvent;

// ---- implementation -------------------------------------------------------

public sealed class EventBus(ILogger<EventBus>? log = null) : IEventBus
{
    private readonly ConcurrentDictionary<Type, object> _streams = new();

    public void Publish<T>(T evt) where T : ILaneEvent
    {
        if (!_streams.TryGetValue(typeof(T), out object? stream)) return;

        ((Stream<T>)stream).Publish(evt, log);
    }

    public IObservable<T> Observe<T>() where T : ILaneEvent =>
        (Stream<T>)_streams.GetOrAdd(typeof(T), _ => new Stream<T>());

    /// <summary>
    /// A minimal hot observable. Deliberately not System.Reactive: the kernel needs one
    /// multicast list and nothing else, and keeping Core free of that dependency is what
    /// stops it accumulating others.
    /// </summary>
    private sealed class Stream<T> : IObservable<T>
    {
        private readonly Lock _gate = new();

        // Copy-on-write, so publishing never takes the lock.
        private IObserver<T>[] _subscribers = [];

        public IDisposable Subscribe(IObserver<T> observer)
        {
            lock (_gate) _subscribers = [.. _subscribers, observer];

            return new Unsubscriber(() =>
            {
                lock (_gate) _subscribers = [.. _subscribers.Where(o => !ReferenceEquals(o, observer))];
            });
        }

        public void Publish(T value, ILogger? log)
        {
            // Snapshot outside the lock: a subscriber that publishes while handling would
            // otherwise deadlock, and the dashboard does exactly that kind of thing.
            IObserver<T>[] current = Volatile.Read(ref _subscribers);

            foreach (IObserver<T> observer in current)
            {
                try { observer.OnNext(value); }
                catch (Exception ex) { log?.LogWarning(ex, "An event subscriber threw handling {Event}", typeof(T).Name); }
            }
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }
}

public static class EventBusExtensions
{
    /// <summary>Subscribes with a plain callback, which is all any subscriber here needs.</summary>
    public static IDisposable Subscribe<T>(this IEventBus bus, Action<T> handler) where T : ILaneEvent =>
        bus.Observe<T>().Subscribe(new DelegateObserver<T>(handler));

    private sealed class DelegateObserver<T>(Action<T> handler) : IObserver<T>
    {
        public void OnNext(T value) => handler(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>Swallows everything. The default until a host wires a real bus.</summary>
public sealed class NullEventBus : IEventBus
{
    public void Publish<T>(T evt) where T : ILaneEvent { }

    public IObservable<T> Observe<T>() where T : ILaneEvent => Empty<T>.Instance;

    private sealed class Empty<T> : IObservable<T>
    {
        public static Empty<T> Instance { get; } = new();
        public IDisposable Subscribe(IObserver<T> observer) => Disposable.Instance;
    }

    private sealed class Disposable : IDisposable
    {
        public static Disposable Instance { get; } = new();
        public void Dispose() { }
    }
}
