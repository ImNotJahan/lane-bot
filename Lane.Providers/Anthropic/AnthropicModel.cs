using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Lane.Core.Models;
using Microsoft.Extensions.Logging;
using LaneStopReason = Lane.Core.Models.StopReason;

namespace Lane.Providers.Anthropic;

public sealed class AnthropicModelOptions
{
    public required string InstanceId { get; init; }
    public required string Model      { get; init; }
    public required string ApiKey     { get; init; }
}

/// <summary>
/// The Anthropic adapter. A pure translator: it maps a <see cref="ModelRequest"/> onto the
/// SDK's types and maps the answer back. No tool execution, no retries against the
/// conversation, no memory — those are kernel concerns, which is what keeps adding a
/// second or third provider cheap.
/// </summary>
public sealed class AnthropicModel : ILanguageModel
{
    private readonly AnthropicClient          _client;
    private readonly string                   _model;
    private readonly ILogger<AnthropicModel>  _log;

    public AnthropicModel(AnthropicModelOptions options, ILogger<AnthropicModel> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);

        _client = new AnthropicClient { ApiKey = options.ApiKey };
        _model  = options.Model;
        _log    = log;

        Descriptor = new ModelDescriptor(
            options.InstanceId,
            "anthropic",
            options.Model,
            ModelCapabilities.Tools | ModelCapabilities.ParallelTools | ModelCapabilities.Streaming |
            ModelCapabilities.Images | ModelCapabilities.PromptCaching | ModelCapabilities.StopSequences |
            ModelCapabilities.Thinking);
    }

    public ModelDescriptor Descriptor { get; }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        MessageCreateParams parameters = BuildParameters(request);

        long started = Stopwatch.GetTimestamp();

        Message response = await _client.Messages.Create(parameters, ct).ConfigureAwait(false);

        TimeSpan latency = Stopwatch.GetElapsedTime(started);

        TokenUsage usage = new(
            Input:      (int)response.Usage.InputTokens,
            Output:     (int)response.Usage.OutputTokens,
            CacheRead:  (int)(response.Usage.CacheReadInputTokens ?? 0),
            CacheWrite: (int)(response.Usage.CacheCreationInputTokens ?? 0),
            ModelInstanceId: Descriptor.InstanceId,
            Latency:    latency);

        _log.LogDebug(
            "{Model} in={In} out={Out} cacheRead={Read} cacheWrite={Write} in {Latency}ms (lineage {Lineage})",
            Descriptor.InstanceId, usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite,
            (int)latency.TotalMilliseconds, request.CacheLineage ?? "-");

        return new ModelResponse(
            AnthropicMessageMapper.FromContent(response.Content),
            AnthropicMessageMapper.FromStopReason(response.StopReason),
            usage);
    }

    /// <summary>
    /// Streams text as the model produces it.
    ///
    /// This is what makes speech start in a few hundred milliseconds rather than after the
    /// whole reply: the first clause can be synthesised while the rest is still being
    /// written. Tool arguments accumulate here too, since a partial JSON fragment is no use
    /// to anybody until the block closes.
    /// </summary>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        MessageCreateParams parameters = BuildParameters(request);

        long started = Stopwatch.GetTimestamp();

        StringBuilder text = new();

        // Keyed by content-block index, which is how the wire format identifies them.
        Dictionary<long, ToolCallBuilder> toolCalls = [];

        LaneStopReason stop = LaneStopReason.EndTurn;
        TokenUsage usage = default;

        await foreach (RawMessageStreamEvent evt in
            _client.Messages.CreateStreaming(parameters, ct).ConfigureAwait(false))
        {
            if (evt.TryPickContentBlockStart(out RawContentBlockStartEvent? blockStart))
            {
                if (blockStart.ContentBlock.TryPickToolUse(out ToolUseBlock? tool))
                {
                    toolCalls[blockStart.Index] = new ToolCallBuilder(tool.ID, tool.Name);

                    yield return new ModelStreamEvent.ToolUseStarted(tool.ID, tool.Name);
                }

                continue;
            }

            if (evt.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? delta))
            {
                if (delta.Delta.TryPickText(out TextDelta? textDelta) && textDelta.Text.Length > 0)
                {
                    text.Append(textDelta.Text);

                    yield return new ModelStreamEvent.TextDelta(textDelta.Text);
                }
                else if (delta.Delta.TryPickInputJson(out InputJsonDelta? json) &&
                         toolCalls.TryGetValue(delta.Index, out ToolCallBuilder? building))
                {
                    building.Arguments.Append(json.PartialJson);

                    yield return new ModelStreamEvent.ToolArgsDelta(building.Id, json.PartialJson);
                }

                continue;
            }

            if (evt.TryPickDelta(out RawMessageDeltaEvent? messageDelta))
            {
                stop = AnthropicMessageMapper.FromStopReason(messageDelta.Delta.StopReason);

                usage = usage with { Output = (int)messageDelta.Usage.OutputTokens };

                continue;
            }

            if (evt.TryPickStart(out RawMessageStartEvent? start))
            {
                usage = new TokenUsage(
                    (int)start.Message.Usage.InputTokens,
                    (int)start.Message.Usage.OutputTokens,
                    (int)(start.Message.Usage.CacheReadInputTokens ?? 0),
                    (int)(start.Message.Usage.CacheCreationInputTokens ?? 0),
                    Descriptor.InstanceId,
                    TimeSpan.Zero);
            }
        }

        List<Core.Messages.ContentPart> content = [];

        if (text.Length > 0) content.Add(new Core.Messages.TextPart(text.ToString()));

        foreach (ToolCallBuilder call in toolCalls.Values) content.Add(call.Build());

        // Some providers omit the stop reason when they return tool calls.
        if (content.OfType<Core.Messages.ToolUsePart>().Any()) stop = LaneStopReason.ToolUse;

        usage = usage with { ModelInstanceId = Descriptor.InstanceId, Latency = Stopwatch.GetElapsedTime(started) };

        yield return new ModelStreamEvent.Completed(new ModelResponse(content, stop, usage));
    }

    /// <summary>Accumulates a tool call whose arguments arrive as JSON fragments.</summary>
    private sealed class ToolCallBuilder(string id, string name)
    {
        public string Id { get; } = id;

        public StringBuilder Arguments { get; } = new();

        public Core.Messages.ToolUsePart Build()
        {
            string json = Arguments.ToString();

            JsonElement arguments;

            try
            {
                arguments = string.IsNullOrWhiteSpace(json)
                    ? JsonDocument.Parse("{}").RootElement.Clone()
                    : JsonDocument.Parse(json).RootElement.Clone();
            }
            catch (JsonException)
            {
                // A truncated stream leaves half an object behind. The tool reports the
                // validation error, which the model usually corrects on its next step.
                arguments = JsonDocument.Parse("{}").RootElement.Clone();
            }

            return new Core.Messages.ToolUsePart(Id, name, arguments);
        }
    }

    private MessageCreateParams BuildParameters(ModelRequest request)
    {
        List<MessageParam> messages = AnthropicMessageMapper.ToMessages(request.Messages);

        if (messages.Count == 0)
            throw new InvalidOperationException(
                "Anthropic requires at least one message; the request had none after mapping.");

        // The SDK's parameter properties are init-only, so everything is decided up front.
        MessageCreateParams parameters = new()
        {
            Model         = _model,
            MaxTokens     = request.MaxOutputTokens,
            Temperature   = request.Temperature,
            System        = AnthropicMessageMapper.ToSystemBlocks(request.System),
            Messages      = messages,
            StopSequences = request.StopSequences is { Count: > 0 } stops ? [.. stops] : null,
            Tools         = request.Tools.Count > 0 ? AnthropicMessageMapper.ToTools(request.Tools) : null,
            ToolChoice    = request.Tools.Count > 0
                ? AnthropicMessageMapper.ToToolChoice(request.ToolChoice)
                : null
        };

        return parameters;
    }
}
