using System.Runtime.CompilerServices;
using Lane.Core.Models;

namespace Lane.Core.Recording;

/// <summary>Hands every completed call to an <see cref="IResponseRecorder"/>. Failed calls are not recorded.</summary>
public sealed class RecordingLanguageModel(ILanguageModel inner, IResponseRecorder recorder) : ILanguageModel
{
    public ModelDescriptor Descriptor => inner.Descriptor;

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ModelResponse response = await inner.CompleteAsync(request, ct).ConfigureAwait(false);

        recorder.Record(Descriptor, request, response);

        return response;
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (ModelStreamEvent evt in inner.StreamAsync(request, ct).ConfigureAwait(false))
        {
            if (evt is ModelStreamEvent.Completed completed) recorder.Record(Descriptor, request, completed.Response);

            yield return evt;
        }
    }
}
