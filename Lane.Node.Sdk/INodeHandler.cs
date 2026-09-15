using Lane.Core.Models;

namespace Lane.Node.Sdk;

public interface INodeHandler
{
    Task<ModelResponse> HandleAsync(ModelRequest request, CancellationToken ct);
}

public sealed class DelegateNodeHandler(Func<ModelRequest, CancellationToken, Task<ModelResponse>> handle) : INodeHandler
{
    public Task<ModelResponse> HandleAsync(ModelRequest request, CancellationToken ct) => handle(request, ct);
}
