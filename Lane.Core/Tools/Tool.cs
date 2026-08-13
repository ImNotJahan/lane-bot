using System.Text.Json;

namespace Lane.Core.Tools;

/// <summary>
/// The base every built-in ability derives from.
///
/// Adding a new ability to Lane is one class: name it, describe it, declare an arguments
/// record, and implement the body. Registration is assembly scanning. Nothing in the
/// kernel, the pipeline or the prompt changes — which is the whole point, since in v2
/// teaching her to read a book meant editing the orchestrator, the prompt and the settings
/// model together.
/// </summary>
public abstract class Tool<TArgs> : ITool where TArgs : class
{
    private ToolDescriptor? _descriptor;

    protected abstract string Name { get; }
    protected abstract string Description { get; }

    protected virtual ToolAvailability Availability => ToolAvailability.Anywhere;
    protected virtual ToolSafety       Safety       => ToolSafety.ReadOnly;
    protected virtual TimeSpan         Timeout      => TimeSpan.FromSeconds(30);

    public ToolDescriptor Descriptor => _descriptor ??= new ToolDescriptor
    {
        Name         = Name,
        Description  = Description,
        InputSchema  = ToolSchema.For<TArgs>(),
        Availability = Availability,
        Safety       = Safety,
        Timeout      = Timeout
    };

    protected abstract ValueTask<ToolResult> InvokeAsync(TArgs args, ToolContext context, CancellationToken ct);

    async ValueTask<ToolResult> ITool.InvokeAsync(ToolInvocation invocation, CancellationToken ct)
    {
        TArgs args;

        try
        {
            args = invocation.Arguments.Deserialize<TArgs>(ToolSchema.ArgumentOptions)
                   ?? throw new JsonException("arguments were null");
        }
        catch (JsonException ex)
        {
            // Returned rather than thrown: the model sees what it got wrong and usually
            // fixes it on the next step, whereas an exception would end the turn.
            return ToolResult.Error($"Invalid arguments for {Name}: {ex.Message}");
        }

        return await InvokeAsync(args, invocation.Context, ct).ConfigureAwait(false);
    }
}

/// <summary>Arguments record for tools that take none.</summary>
public sealed record NoArgs;

/// <summary>
/// A tool built from a descriptor and a delegate. The escape hatch for tools whose schema
/// comes from elsewhere — an MCP server's advertised schema, or a plugin.
/// </summary>
public sealed class RawTool(
    ToolDescriptor descriptor,
    Func<ToolInvocation, CancellationToken, ValueTask<ToolResult>> handler) : ITool
{
    public ToolDescriptor Descriptor { get; } = descriptor;

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
        handler(invocation, ct);
}
