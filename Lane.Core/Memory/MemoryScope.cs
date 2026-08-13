using Lane.Core.Identity;
using Lane.Core.Sessions;

namespace Lane.Core.Memory;

/// <summary>How widely a memory handler's contents are shared.</summary>
public enum MemoryScope
{
    /// <summary>One instance for all of Lane. Thoughts, long-term recall.</summary>
    Global,

    /// <summary>One per surface instance — everything Discord, separately from everything API.</summary>
    Surface,

    /// <summary>One per conversation, keyed by memory group rather than session id.</summary>
    Session,

    /// <summary>One per person, linked across surfaces by <c>GlobalUserId</c>.</summary>
    User
}

/// <summary>Where a handler's recall lands in the request.</summary>
public enum MemorySlot
{
    /// <summary>
    /// A cached system block. Long-term recall, rolling summaries, persona facts —
    /// anything that changes slowly enough to be worth a cache breakpoint.
    /// </summary>
    Stable,

    /// <summary>An uncached system block at the tail: the clock, the roster, the session list.</summary>
    Volatile,

    /// <summary>
    /// Real conversation turns appended to the request's message list.
    ///
    /// The recent window must live here. tool_use and tool_result blocks only survive as
    /// structured turns; flattening them into a text block — which is what v2 did with all
    /// memory — leaves the next request with a tool call nobody answered.
    /// </summary>
    Inline
}

/// <summary>The resolved identity of one handler instance's storage.</summary>
public readonly record struct ScopeKey(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Who and where a memory operation is happening for.</summary>
public sealed record MemoryContext
{
    public SessionDescriptor? Session { get; init; }

    public SurfaceId? Surface { get; init; }

    /// <summary>Whose turn it is. Drives <see cref="MemoryScope.User"/>.</summary>
    public Participant? Focus { get; init; }

    public TurnKind Turn { get; init; } = TurnKind.Respond;

    /// <summary>The surface in play, from whichever source knows it.</summary>
    public SurfaceId? EffectiveSurface => Surface ?? Session?.Id.Surface;

    /// <summary>Lane thinking to herself: no session, no channel, no focus.</summary>
    public static MemoryContext Monologue { get; } = new() { Turn = TurnKind.Monologue };
}

public static class ScopeKeys
{
    public static ScopeKey Derive(MemoryScope scope, MemoryContext ctx) => scope switch
    {
        MemoryScope.Global => new ScopeKey("global"),

        MemoryScope.Surface => new ScopeKey(
            $"surface:{ctx.EffectiveSurface?.Value ?? "detached"}"),

        // Keyed on the memory group, not the session id: a Discord text channel and the
        // voice channel beside it are two sessions but one conversation.
        MemoryScope.Session => new ScopeKey(
            $"session:{ctx.Session?.MemoryGroup ?? "detached"}"),

        // Falls back to the surface-local id when no cross-surface link is configured, so
        // an unlinked user still gets their own memory rather than sharing an "anonymous" one.
        MemoryScope.User => new ScopeKey(
            $"user:{ctx.Focus?.StableKey ?? "anon"}"),

        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown memory scope.")
    };
}
