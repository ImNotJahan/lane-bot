namespace Lane.Core.Models;

/// <summary>
/// Lane's own port for a chat model. Deliberately not a general-purpose chat abstraction:
/// per-block cache breakpoints, provider-specific stop handling and capability
/// introspection all degrade into untyped property bags through a generic interface, and
/// those are exactly the things the harness needs to control.
///
/// Implementations are pure translators. The agentic loop lives in the kernel.
/// </summary>
public interface ILanguageModel
{
    ModelDescriptor Descriptor { get; }

    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct);

    /// <summary>
    /// Streams the same result. Always ends with <see cref="ModelStreamEvent.Completed"/>,
    /// so a caller that only wants the final response can drain and take the last event.
    /// </summary>
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct);
}

/// <summary>Raised by adapters after every call so telemetry does not need model references.</summary>
public sealed record TokenUsageReported(string ModelInstanceId, string? Role, TokenUsage Usage);
