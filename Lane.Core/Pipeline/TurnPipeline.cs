using Lane.Core.Events;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Pipeline;

/// <summary>
/// One step of a turn. Stages compose as middleware, so adding moderation, a rate limiter
/// or an "only respond when mentioned" rule is a new class and a config entry rather than
/// a change to the orchestrator.
/// </summary>
public interface ITurnStage
{
    string Name => GetType().Name;

    Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct);
}

/// <summary>
/// Runs the configured stages in order for each turn a session pump hands over.
/// </summary>
public sealed class TurnPipeline : ITurnExecutor
{
    private readonly IReadOnlyList<ITurnStage> _stages;
    private readonly ILogger<TurnPipeline>     _log;
    private readonly IEventBus                 _bus;

    public TurnPipeline(IEnumerable<ITurnStage> stages, ILogger<TurnPipeline> log, IEventBus? bus = null)
    {
        _stages = [.. stages];
        _log    = log;
        _bus    = bus ?? new NullEventBus();

        if (_stages.Count == 0)
            throw new InvalidOperationException("The turn pipeline has no stages registered.");
    }

    public async Task ExecuteAsync(TurnRequest request, CancellationToken ct)
    {
        TurnContext ctx = new()
        {
            Session       = request.Session,
            Kind          = request.Kind,
            Incoming      = [.. request.Incoming.Select(e => e.Message)],
            DirectiveText = request.DirectiveText,
            Reason        = request.Reason,
            Target        = request.Target
        };

        using IDisposable? scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["session"] = request.Session.Id.Value,
            ["turn"]    = request.Kind.ToString()
        });

        long started = Environment.TickCount64;

        _bus.Publish(new TurnStarted(request.Session.Id, request.Kind, request.Incoming.Count));

        try
        {
            await InvokeAsync(ctx, 0, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _bus.Publish(new TurnFailed(request.Session.Id, ex.Message));
            throw;
        }

        TimeSpan elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        _bus.Publish(new TurnCompleted(
            request.Session.Id, request.Kind, ctx.Suppressed, ctx.Result?.NewMessages.Count ?? 0, elapsed));

        _log.LogDebug("Turn finished in {Elapsed}ms (suppressed: {Suppressed})",
            (int)elapsed.TotalMilliseconds, ctx.Suppressed);
    }

    private Task InvokeAsync(TurnContext ctx, int index, CancellationToken ct)
    {
        if (index >= _stages.Count) return Task.CompletedTask;

        ITurnStage stage = _stages[index];

        return stage.ExecuteAsync(ctx, () => InvokeAsync(ctx, index + 1, ct), ct);
    }
}
