using System.Runtime.CompilerServices;
using Lane.Core.Models;
using Lane.Nodes.Protocol;
using Microsoft.Extensions.Logging;

namespace Lane.Nodes;

/// <summary>A model instance answered by whichever node in its pool is least busy.</summary>
public sealed class NodeLanguageModel : ILanguageModel
{
    private readonly string                     _pool;
    private readonly NodePool                   _nodes;
    private readonly INodeResponseValidator     _validator;
    private readonly NodesOptions               _options;
    private readonly ILogger<NodeLanguageModel> _log;
    private readonly NodeBookkeeper?            _bookkeeper;

    public NodeLanguageModel(
        string                     instanceId,
        string                     pool,
        ModelCapabilities          capabilities,
        NodePool                   nodes,
        INodeResponseValidator     validator,
        NodesOptions               options,
        ILogger<NodeLanguageModel> log,
        NodeBookkeeper?            bookkeeper = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pool);

        _pool       = pool;
        _nodes      = nodes;
        _validator  = validator;
        _options    = options;
        _log        = log;
        _bookkeeper = bookkeeper;

        Descriptor = new ModelDescriptor(instanceId, "node", $"pool:{pool}", capabilities);

        nodes.Expect(pool, capabilities);
    }

    public ModelDescriptor Descriptor { get; }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        int attempts = Math.Max(1, _options.MaxAttempts);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await AttemptAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < attempts && IsRetryable(ex) && !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "{Model} attempt {Attempt} of {Attempts} failed; retrying on another node",
                    Descriptor.InstanceId, attempt, attempts);
            }
        }
    }

    private static bool IsRetryable(Exception ex) =>
        ex is TimeoutException or NodeDisconnectedException or NodeRequestFailedException or NodeResponseRejectedException;

    private async Task<ModelResponse> AttemptAsync(ModelRequest request, CancellationToken ct)
    {
        using NodeLease lease = await _nodes.AcquireAsync(_pool, _options.AcquireTimeout, ct).ConfigureAwait(false);

        NodeConnection node = lease.Node;

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);

        NodeReply reply;

        try
        {
            reply = await node.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Node {node} did not answer {Descriptor} within {_options.RequestTimeout}.");
        }

        NodeValidationResult verdict = await _validator.ValidateAsync(node.Hello, request, reply, ct).ConfigureAwait(false);

        if (!verdict.Accepted)
            throw new NodeResponseRejectedException(node.ToString(), verdict.Reason ?? "no reason given");

        if (_bookkeeper is not null) await _bookkeeper.AnsweredAsync(node.Hello.Identity).ConfigureAwait(false);

        ModelResponse response = reply.Response with
        {
            Origin = new NodeAttestation(node.Hello.Identity, reply.Signature, node.Hello.Delegation),
            Usage  = reply.Response.Usage with { ModelInstanceId = Descriptor.InstanceId }
        };

        _log.LogDebug("{Model} answered by node {Node} in={In} out={Out} in {Latency}ms",
            Descriptor.InstanceId, node, response.Usage.Input, response.Usage.Output,
            (int)response.Usage.Latency.TotalMilliseconds);

        return response;
    }

    /// <summary>Emits the finished response as one delta.</summary>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ModelResponse response = await CompleteAsync(request, ct).ConfigureAwait(false);

        if (response.Text.Length > 0) yield return new ModelStreamEvent.TextDelta(response.Text);

        foreach (Lane.Core.Messages.ToolUsePart call in response.ToolCalls)
            yield return new ModelStreamEvent.ToolUseStarted(call.ToolCallId, call.ToolName);

        yield return new ModelStreamEvent.Completed(response);
    }
}
