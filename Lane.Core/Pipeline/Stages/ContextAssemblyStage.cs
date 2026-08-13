using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Pipeline.Stages;

/// <summary>
/// Asks memory what it knows before the model is called.
///
/// Runs after ingest but before the model, and crucially before persistence: the messages
/// this turn is answering are not yet in the window, so they arrive exactly once — as the
/// live turn — rather than being both recalled and re-sent.
/// </summary>
public sealed class ContextAssemblyStage(
    IMemoryService memory,
    ILogger<ContextAssemblyStage> log) : ITurnStage
{
    public async Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct)
    {
        if (ctx.Kind == Sessions.TurnKind.Directive)
        {
            // Lane already decided what to say; there is nothing to remember first.
            await next().ConfigureAwait(false);
            return;
        }

        LaneMessage? cue = ctx.Incoming.LastOrDefault();

        MemoryContext memoryContext = new()
        {
            Session = ctx.Descriptor,
            Surface = ctx.Session.Id.Surface,
            Focus   = FocusOf(ctx),
            Turn    = ctx.Kind
        };

        ctx.Items["memory"] = memoryContext;

        try
        {
            ctx.Context = await memory.AssembleAsync(memoryContext, cue, ct).ConfigureAwait(false);

            log.LogDebug("Assembled {Inline} inline, {Stable} stable, {Volatile} volatile block(s)",
                ctx.Context.InlineMessages.Count, ctx.Context.StableBlocks.Count, ctx.Context.VolatileBlocks.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Answering with no memory is worse than answering well, but far better than
            // not answering at all.
            log.LogError(ex, "Context assembly failed for {Session}; continuing without memory", ctx.Session.Id);
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>Whose User-scoped memory this turn is about: the last person to speak.</summary>
    private static Participant? FocusOf(TurnContext ctx) =>
        ctx.Incoming.LastOrDefault(m => m.Role == LaneRole.User)?.Author
        ?? ctx.Descriptor.KnownParticipants.FirstOrDefault(p => !p.IsLane);
}
