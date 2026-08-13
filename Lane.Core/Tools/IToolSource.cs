namespace Lane.Core.Tools;

/// <summary>
/// Somewhere tools come from. Built-in tools are one source; each MCP server is another,
/// and a plugin directory could be a third. The registry does not care which.
/// </summary>
public interface IToolSource
{
    string SourceId { get; }

    ValueTask<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken ct);

    /// <summary>Raised when the source's tool list changes — an MCP server reconnecting.</summary>
    event Action<string>? ToolsChanged;
}

/// <summary>The tools registered in the container, discovered by assembly scanning.</summary>
public sealed class DiToolSource(IEnumerable<ITool> tools) : IToolSource
{
    private readonly IReadOnlyList<ITool> _tools = [.. tools];

    public string SourceId => "builtin";

    // Built-in tools are fixed for the life of the process.
    public event Action<string>? ToolsChanged { add { } remove { } }

    public ValueTask<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken ct) => ValueTask.FromResult(_tools);
}

public abstract record ToolEvent
{
    public sealed record Invoked(string Name, string CallId, string? Session) : ToolEvent;

    public sealed record Completed(string Name, string CallId, bool IsError, TimeSpan Duration) : ToolEvent;

    public sealed record Rejected(string Name, string Reason) : ToolEvent;
}
