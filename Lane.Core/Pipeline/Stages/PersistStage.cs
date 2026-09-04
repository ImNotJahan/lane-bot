using Lane.Core.Memory;
using Lane.Core.Messages;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Pipeline.Stages;

/// <summary>
/// Commits everything the turn produced: the durable transcript first, then memory.
///
/// The ordering within a turn matters and is fixed here. An assistant message carrying
/// tool_use blocks and the tool results answering them are committed together, in order —
/// a half-written history leaves a tool call nobody answered, and every subsequent request
/// in that session is rejected because of it.
/// </summary>
public sealed class PersistStage(
    IMemoryService     memory,
    ITranscriptStore   transcript,
    ILogger<PersistStage> log) : ITurnStage
{
    public async Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct)
    {
        await CommitAsync(ctx).ConfigureAwait(false);

        await next().ConfigureAwait(false);
    }

    private async Task CommitAsync(TurnContext ctx)
    {
        if (ctx.Produced.Count == 0) return;

        MemoryContext memoryContext = ctx.Items.TryGetValue("memory", out object? found) && found is MemoryContext mc
            ? mc
            : new MemoryContext { Session = ctx.Descriptor, Surface = ctx.Session.Id.Surface, Turn = ctx.Kind };

        // Not cancellable: a turn cut short still has to record what it already did, or the
        // next request reads a history that never happened.
        CancellationToken ct = CancellationToken.None;

        foreach (LaneMessage message in ctx.Produced)
        {
            try
            {
                long sequence = await transcript.AppendAsync(message, ct).ConfigureAwait(false);

                // Handlers order inline recall by (timestamp, sequence), so they need the
                // sequence the store just assigned, not the zero it was created with.
                LaneMessage stored = sequence > 0 ? message with { Sequence = sequence } : message;

                // The transcript is unconditional; memory is not. A turn she slept through is
                // recorded in full and shown to no handler — see TurnContext.RecordToMemory.
                if (ctx.RecordToMemory)
                    await memory.RememberAsync(stored, memoryContext, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to persist a message for {Session}", ctx.Session.Id);
            }
        }
    }
}
