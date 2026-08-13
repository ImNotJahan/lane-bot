using System.Collections.Concurrent;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Sessions;

namespace Lane.Surfaces.Api.Streaming;

/// <summary>What a client watching one conversation is told, as it happens.</summary>
public abstract record TurnStreamEvent
{
    /// <summary>A fragment of the reply, as the model writes it.</summary>
    public sealed record Delta(string Text) : TurnStreamEvent;

    public sealed record ToolStarted(string Name) : TurnStreamEvent;

    public sealed record ToolFinished(string Name, bool IsError) : TurnStreamEvent;

    /// <summary>The finished reply, exactly as it was delivered to the conversation.</summary>
    public sealed record Message(string Text, string? ReplyTo) : TurnStreamEvent;

    /// <summary>The turn is over. <paramref name="Silent"/> when Lane chose not to reply.</summary>
    public sealed record Done(bool Silent, string? Reason) : TurnStreamEvent;

    public sealed record Failed(string Error) : TurnStreamEvent;
}

/// <summary>
/// Carries a turn's progress from inside the kernel out to whoever is watching it over HTTP.
///
/// It exists because the two halves live on opposite sides of the composition root: the
/// observer that sees text deltas is built by the turn pipeline, while the response writing
/// them out belongs to a request that started later. This is the meeting point, keyed by
/// session so a subscriber only ever sees its own conversation.
///
/// It also decides whether streaming happens at all — the pipeline asks
/// <see cref="IsWatched"/>, and a turn nobody is watching runs exactly as it did before the
/// API existed.
/// </summary>
public sealed class TurnStreamHub : IDisposable
{
    private readonly ConcurrentDictionary<string, Subscribers> _sessions = new(StringComparer.Ordinal);
    private readonly IDisposable? _completed;
    private readonly IDisposable? _failed;

    public TurnStreamHub(IEventBus? bus = null)
    {
        if (bus is null) return;

        // A turn ends inside the pipeline, well after the observer has been disposed, so the
        // end of a stream is read from the bus rather than from the observer.
        _completed = bus.Subscribe<TurnCompleted>(evt =>
        {
            // A directive is the monologue volunteering something, which can land in this
            // session at any moment. Ending a client's stream on it would cut off the reply
            // it is actually waiting for.
            if (evt.Kind == TurnKind.Directive) return;

            Publish(evt.Session, new TurnStreamEvent.Done(evt.Suppressed, evt.Suppressed ? "suppressed" : null));
        });

        _failed = bus.Subscribe<TurnFailed>(evt => Publish(evt.Session, new TurnStreamEvent.Failed(evt.Error)));
    }

    /// <summary>True when someone is watching this conversation and streaming is worth doing.</summary>
    public bool IsWatched(SessionId session) =>
        _sessions.TryGetValue(session.Value, out Subscribers? found) && !found.IsEmpty;

    public IDisposable Subscribe(SessionId session, Action<TurnStreamEvent> handler)
    {
        Subscribers subscribers = _sessions.GetOrAdd(session.Value, _ => new Subscribers());

        return subscribers.Add(handler);
    }

    public void Publish(SessionId session, TurnStreamEvent evt)
    {
        if (_sessions.TryGetValue(session.Value, out Subscribers? found)) found.Publish(evt);
    }

    public void Dispose()
    {
        _completed?.Dispose();
        _failed?.Dispose();
    }

    private sealed class Subscribers
    {
        private readonly Lock _gate = new();

        // Copy-on-write: a delta arrives per token, and taking a lock to publish one would
        // put the model's output rate behind a contended lock.
        private Action<TurnStreamEvent>[] _handlers = [];

        public bool IsEmpty => Volatile.Read(ref _handlers).Length == 0;

        public IDisposable Add(Action<TurnStreamEvent> handler)
        {
            lock (_gate) _handlers = [.. _handlers, handler];

            return new Removal(this, handler);
        }

        public void Publish(TurnStreamEvent evt)
        {
            foreach (Action<TurnStreamEvent> handler in Volatile.Read(ref _handlers))
            {
                // A client that has gone away must not stop the others being told.
                try { handler(evt); } catch (Exception) { /* their socket, their problem */ }
            }
        }

        private sealed class Removal(Subscribers owner, Action<TurnStreamEvent> handler) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                lock (owner._gate)
                    owner._handlers = [.. owner._handlers.Where(h => !ReferenceEquals(h, handler))];
            }
        }
    }
}
