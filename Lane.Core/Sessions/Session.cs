using System.Threading.Channels;
using Lane.Core.Identity;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Sessions;

/// <summary>
/// One conversation, and the guarantee that it behaves like one.
///
/// Every way of making Lane act in this conversation — a message arriving, the monologue
/// volunteering something, a nudge — goes onto a single queue drained by a single pump.
/// That gives three properties v2 could not offer:
///
///   * two messages in the same channel never race each other,
///   * a message arriving mid-reply queues for the next turn instead of being dropped
///     (v2's <c>isResponding</c> discarded it), and
///   * different conversations still run concurrently, bounded only by the shared
///     <see cref="TurnBudget"/>.
/// </summary>
public sealed class Session : IAsyncDisposable
{
    private readonly Channel<SessionWorkItem> _inbox;
    private readonly ITurnExecutor            _executor;
    private readonly TurnBudget               _budget;
    private readonly SessionOptions           _options;
    private readonly TimeProvider             _time;
    private readonly ILogger                  _log;

    private readonly List<ISessionChannel> _channels = [];
    private readonly Lock                  _channelLock = new();

    private CancellationTokenSource? _currentTurn;
    private Task?                    _pump;
    private long                     _lastActivityTicks;

    internal Session(
        SessionDescriptor descriptor,
        ITurnExecutor     executor,
        TurnBudget        budget,
        SessionOptions    options,
        TimeProvider      time,
        ILogger           log)
    {
        Descriptor = descriptor;
        _executor  = executor;
        _budget    = budget;
        _options   = options;
        _time      = time;
        _log       = log;

        _inbox = Channel.CreateBounded<SessionWorkItem>(new BoundedChannelOptions(options.InboxCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode     = BoundedChannelFullMode.Wait
        });

        _lastActivityTicks = time.GetUtcNow().UtcTicks;
    }

    public SessionId Id => Descriptor.Id;

    public SessionDescriptor Descriptor { get; private set; }

    public SessionState State { get; private set; } = SessionState.Idle;

    public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public IReadOnlyList<ISessionChannel> Channels
    {
        get { lock (_channelLock) return [.. _channels]; }
    }

    /// <summary>Merges in a refreshed descriptor — participants join, a channel is renamed.</summary>
    public void UpdateDescriptor(SessionDescriptor descriptor)
    {
        if (descriptor.Id != Descriptor.Id)
            throw new ArgumentException($"Descriptor is for {descriptor.Id}, not {Descriptor.Id}.", nameof(descriptor));

        Descriptor = descriptor;
    }

    internal IDisposable AttachChannel(ISessionChannel channel)
    {
        lock (_channelLock) _channels.Add(channel);

        _log.LogDebug("Attached {Surface} channel to session {Session}", channel.Surface, Id);

        return new Detach(this, channel);
    }

    /// <summary>Resolves the channels an outbound should reach, honouring the delivery target.</summary>
    public IReadOnlyList<T> ResolveOutputs<T>(DeliveryTarget target) where T : class
    {
        List<T> found = [];

        foreach (ISessionChannel channel in Channels)
        {
            if (target.Mode == DeliveryMode.Capability &&
                (channel.Capabilities & target.Required) != target.Required) continue;

            if (!channel.TryGetService(out T? service)) continue;

            found.Add(service);

            if (target.Mode == DeliveryMode.Primary) break;
        }

        return found;
    }

    public ValueTask PostAsync(SessionWorkItem item, CancellationToken ct = default)
    {
        Touch();
        return _inbox.Writer.WriteAsync(item, ct);
    }

    public void CancelCurrentTurn(string reason)
    {
        CancellationTokenSource? cts = Volatile.Read(ref _currentTurn);
        if (cts is null) return;

        _log.LogInformation("Cancelling turn in {Session}: {Reason}", Id, reason);

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* turn already finished */ }
    }

    internal void Start(CancellationToken ct) => _pump ??= Task.Run(() => RunPumpAsync(ct), CancellationToken.None);

    private async Task RunPumpAsync(CancellationToken ct)
    {
        _log.LogDebug("Session pump started for {Session}", Id);

        try
        {
            while (await _inbox.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                List<SessionWorkItem> batch = await DrainAsync(ct).ConfigureAwait(false);

                // Work items run in arrival order. Runs of inbound messages coalesce into a
                // single turn; everything else is handled on its own so a volunteered
                // utterance can never be merged into a reply.
                for (int i = 0; i < batch.Count;)
                {
                    if (batch[i] is SessionWorkItem.Inbound)
                    {
                        int start = i;
                        while (i < batch.Count && batch[i] is SessionWorkItem.Inbound) i++;

                        List<InboundEvent> events =
                            [.. batch[start..i].Cast<SessionWorkItem.Inbound>().Select(b => b.Event)];

                        await RunInboundAsync(events, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await RunSingleAsync(batch[i], ct).ConfigureAwait(false);
                        i++;
                    }
                }

                State = SessionState.Idle;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogDebug("Session pump stopped for {Session}", Id);
        }
        catch (Exception ex)
        {
            // Reaching here means the pump itself broke, not a turn — turns are caught below.
            _log.LogError(ex, "Session pump for {Session} faulted and will no longer process work", Id);
        }
    }

    /// <summary>
    /// Takes everything already queued, then holds the batch window open for stragglers.
    /// A burst of messages becomes one turn; a lone message waits only the window.
    /// </summary>
    private async Task<List<SessionWorkItem>> DrainAsync(CancellationToken ct)
    {
        State = SessionState.Batching;

        List<SessionWorkItem> batch = [];

        while (_inbox.Reader.TryRead(out SessionWorkItem? item)) batch.Add(item);

        TimeSpan window = Descriptor.Id.Kind == SessionKind.Voice
            ? _options.VoiceBatchWindow
            : _options.BatchWindow;

        if (window > TimeSpan.Zero && batch.Count > 0)
        {
            try
            {
                await Task.Delay(window, _time, ct).ConfigureAwait(false);
                while (_inbox.Reader.TryRead(out SessionWorkItem? extra)) batch.Add(extra);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
        }

        return batch;
    }

    private async Task RunInboundAsync(List<InboundEvent> events, CancellationToken ct)
    {
        // Observe-only traffic is still remembered, but never triggers a reply on its own.
        TurnKind kind = events.Any(e => e.RequiresResponse) ? TurnKind.Respond : TurnKind.None;

        TurnRequest request = new()
        {
            Session  = this,
            Kind     = kind == TurnKind.None ? TurnKind.Respond : kind,
            Incoming = events,
            Reason   = kind == TurnKind.None ? "observe" : "inbound"
        };

        await ExecuteAsync(request, ct).ConfigureAwait(false);
    }

    private async Task RunSingleAsync(SessionWorkItem item, CancellationToken ct)
    {
        switch (item)
        {
            case SessionWorkItem.Speak speak:
                await ExecuteAsync(new TurnRequest
                {
                    Session       = this,
                    Kind          = TurnKind.Directive,
                    DirectiveText = speak.Text,
                    Target        = speak.Target,
                    Reason        = speak.Reason
                }, ct).ConfigureAwait(false);
                break;

            case SessionWorkItem.Nudge nudge:
                await ExecuteAsync(new TurnRequest
                {
                    Session = this,
                    Kind    = TurnKind.Respond,
                    Reason  = nudge.Reason
                }, ct).ConfigureAwait(false);
                break;

            case SessionWorkItem.Control control:
                HandleControl(control);
                break;
        }
    }

    private void HandleControl(SessionWorkItem.Control control)
    {
        switch (control.Action)
        {
            case ControlAction.Cancel:
                CancelCurrentTurn(control.Reason);
                break;

            case ControlAction.Close:
                _inbox.Writer.TryComplete();
                break;
        }
    }

    private async Task ExecuteAsync(TurnRequest request, CancellationToken ct)
    {
        using IDisposable slot = await _budget.AcquireAsync(ct).ConfigureAwait(false);

        using CancellationTokenSource turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Volatile.Write(ref _currentTurn, turnCts);
        State = request.Kind == TurnKind.Directive ? SessionState.Speaking : SessionState.Running;

        try
        {
            await _executor.ExecuteAsync(request, turnCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _log.LogInformation("Turn in {Session} was cancelled", Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed turn must never kill the pump — the next message still deserves a reply.
            _log.LogError(ex, "Turn in {Session} failed", Id);
        }
        finally
        {
            Volatile.Write(ref _currentTurn, null);
            Touch();
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, _time.GetUtcNow().UtcTicks);

    public async ValueTask DisposeAsync()
    {
        _inbox.Writer.TryComplete();

        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }

        foreach (ISessionChannel channel in Channels) await channel.DisposeAsync().ConfigureAwait(false);

        lock (_channelLock) _channels.Clear();
    }

    private sealed class Detach(Session session, ISessionChannel channel) : IDisposable
    {
        public void Dispose()
        {
            lock (session._channelLock) session._channels.Remove(channel);
        }
    }
}
