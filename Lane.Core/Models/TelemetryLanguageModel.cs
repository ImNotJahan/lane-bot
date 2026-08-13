using System.Runtime.CompilerServices;
using Lane.Core.Events;

namespace Lane.Core.Models;

/// <summary>
/// Wraps any model and announces what it cost.
///
/// A decorator rather than a change to each adapter, for two reasons: adapters stay pure
/// translators, and every provider gets telemetry the moment it is added — including ones
/// written later, by someone who never read this file.
/// </summary>
public sealed class TelemetryLanguageModel(ILanguageModel inner, IEventBus bus, string? role = null)
    : ILanguageModel
{
    public ModelDescriptor Descriptor => inner.Descriptor;

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ModelResponse response = await inner.CompleteAsync(request, ct).ConfigureAwait(false);

        bus.Publish(new TokenUsageEvent(Descriptor.InstanceId, role, response.Usage));

        return response;
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (ModelStreamEvent evt in inner.StreamAsync(request, ct).ConfigureAwait(false))
        {
            if (evt is ModelStreamEvent.Completed completed)
                bus.Publish(new TokenUsageEvent(Descriptor.InstanceId, role, completed.Response.Usage));

            yield return evt;
        }
    }
}
