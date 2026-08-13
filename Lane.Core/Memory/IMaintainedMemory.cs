namespace Lane.Core.Memory;

/// <summary>
/// A handler with work to do that must not happen while someone is waiting.
///
/// Summarising and keeping a profile current both cost a model call. Doing that inside
/// <see cref="IMemoryHandler.RememberAsync"/> would put a whole round trip between Lane
/// deciding what to say and actually saying it, because messages are committed before they
/// are delivered. So handlers buffer during a turn and catch up on a timer instead.
/// </summary>
public interface IMaintainedMemory
{
    /// <summary>True when there is buffered work worth spending a model call on.</summary>
    bool NeedsMaintenance { get; }

    ValueTask MaintainAsync(CancellationToken ct);
}
