using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Node.Sdk;
using Lane.Nodes;
using Lane.Nodes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class NodeEndToEndTests
{
    private sealed class RecordingValidator : INodeResponseValidator
    {
        public List<NodeReply> Seen { get; } = [];

        public ValueTask<NodeValidationResult> ValidateAsync(
            NodeHello node, ModelRequest request, NodeReply reply, CancellationToken ct)
        {
            lock (Seen) Seen.Add(reply);
            return ValueTask.FromResult(NodeValidationResult.Accept);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _node = new();
        private readonly TaskCompletionSource    _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _running = Task.CompletedTask;

        public required NodeListener       Listener  { get; init; }
        public required NodeLanguageModel  Model     { get; init; }
        public required RecordingValidator Validator { get; init; }
        public required INodeKey           Key       { get; init; }

        public static async Task<Rig> StartAsync(
            Func<ModelRequest, CancellationToken, Task<ModelResponse>> handler,
            TimeSpan? acquireTimeout = null,
            INodeKey? key = null)
        {
            NodesOptions options = new()
            {
                Enabled        = true,
                Urls           = "http://127.0.0.1:0",
                AcquireTimeout = acquireTimeout ?? TimeSpan.FromSeconds(5)
            };

            NodePool pool = new();
            RecordingValidator validator = new();

            NodeListener listener = new(pool, options, NullLoggerFactory.Instance);
            await listener.StartAsync(CancellationToken.None);

            Rig rig = new()
            {
                Listener  = listener,
                Validator = validator,
                Key       = key ?? FileNodeKey.Generate(),
                Model     = new NodeLanguageModel("pool", "default", ModelCapabilities.Tools, pool, validator, options,
                    NullLogger<NodeLanguageModel>.Instance)
            };

            LaneNode node = new(
                new LaneNodeOptions { LaneUrl = listener.Addresses[0], Model = "scripted", Name = "test-node" },
                rig.Key,
                handler);

            node.Connected += _ => rig._connected.TrySetResult();
            rig._running = node.RunAsync(rig._node.Token);

            await rig._connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

            return rig;
        }

        public async Task StopNodeAsync()
        {
            await _node.CancelAsync();
            await _running;
        }

        public async ValueTask DisposeAsync()
        {
            await StopNodeAsync();
            await Listener.StopAsync(CancellationToken.None);
            await Listener.DisposeAsync();
            (Key as IDisposable)?.Dispose();
        }
    }

    private static ModelRequest Request(string text) => new()
    {
        System   = [],
        Messages = [LaneMessage.Thought(Participant.LaneInternal, text, DateTimeOffset.UnixEpoch)]
    };

    [Fact]
    public async Task Node_answers_and_the_response_carries_its_signed_identity()
    {
        await using Rig rig = await Rig.StartAsync((request, _) => Task.FromResult(new ModelResponse(
            [new TextPart("echo: " + request.Messages[0].TextContent)],
            StopReason.EndTurn,
            new TokenUsage(3, 4, 0, 0, "whatever", TimeSpan.FromMilliseconds(5)))));

        ModelResponse response = await rig.Model.CompleteAsync(Request("hi"), CancellationToken.None);

        Assert.Equal("echo: hi", response.Text);
        Assert.Equal("pool", response.Usage.ModelInstanceId);

        NodeAttestation origin = Assert.IsType<NodeAttestation>(response.Origin);
        Assert.Equal(rig.Key.Identity, origin.Identity);

        NodeReply seen = Assert.Single(rig.Validator.Seen);
        Assert.Equal(seen.Signature, origin.Signature);
        Assert.Null(origin.Delegation);
        Assert.True(NodeProtocol.Verify(origin, seen.RequestId, seen.Response));
    }

    [Fact]
    public async Task Security_key_node_responses_carry_the_delegation_back_to_the_security_key()
    {
        using FakeSecurityKey securityKey = new();
        using WebAuthnSession session = new();
        WebAuthnNodeKey key = securityKey.Vouch(session);

        await using Rig rig = await Rig.StartAsync(
            (_, _) => Task.FromResult(new ModelResponse([new TextPart("ok")], StopReason.EndTurn, default)),
            key: key);

        ModelResponse response = await rig.Model.CompleteAsync(Request("hi"), CancellationToken.None);

        NodeAttestation origin = Assert.IsType<NodeAttestation>(response.Origin);
        Assert.Equal(NodeProtocol.IdentityFor(securityKey.PublicKey, NodeProtocol.WebAuthnEs256), origin.Identity);
        Assert.Equal(key.Delegation, origin.Delegation);

        NodeReply seen = Assert.Single(rig.Validator.Seen);
        Assert.True(NodeProtocol.Verify(origin, seen.RequestId, seen.Response));
    }

    [Fact]
    public async Task Handler_failure_reaches_the_caller()
    {
        await using Rig rig = await Rig.StartAsync((_, _) => throw new InvalidOperationException("upstream is down"));

        NodeRequestFailedException ex = await Assert.ThrowsAsync<NodeRequestFailedException>(() =>
            rig.Model.CompleteAsync(Request("hi"), CancellationToken.None));

        Assert.Contains("upstream is down", ex.Message);
    }
}
