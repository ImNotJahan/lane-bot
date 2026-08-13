using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lane.Core.Messages;
using Lane.Core.Models;

namespace Lane.Testing;

/// <summary>
/// A language model that answers from a script instead of a network.
///
/// This exists so the whole kernel — pipeline, session pumps, and later the tool loop —
/// can be driven deterministically and offline. Every milestone's tests depend on it, so
/// it is built first and treated as production code, not a throwaway stub.
/// </summary>
public sealed class ScriptedLanguageModel : ILanguageModel
{
    private Func<ModelRequest, int, ModelResponse> _respond;
    private readonly ConcurrentQueue<ModelRequest>          _requests = new();
    private int _calls;
    private int _streamed;

    public ScriptedLanguageModel(
        Func<ModelRequest, int, ModelResponse> respond,
        string instanceId = "scripted",
        ModelCapabilities capabilities =
            ModelCapabilities.Tools | ModelCapabilities.ParallelTools | ModelCapabilities.Streaming |
            ModelCapabilities.PromptCaching | ModelCapabilities.StructuredOutput | ModelCapabilities.StopSequences)
    {
        _respond   = respond;
        Descriptor = new ModelDescriptor(instanceId, "scripted", "scripted-v1", capabilities);
    }

    public ModelDescriptor Descriptor { get; }

    /// <summary>Changes the script mid-test, for asserting on what happens after something changes.</summary>
    public void Reprogram(Func<ModelRequest, int, ModelResponse> respond) => _respond = respond;

    /// <summary>Every request the model was asked to answer, in order.</summary>
    public IReadOnlyList<ModelRequest> Requests => [.. _requests];

    public int CallCount => Volatile.Read(ref _calls);

    /// <summary>Artificial latency, for exercising cancellation and concurrency.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Whether the most recent call streamed rather than asking for the whole response.
    ///
    /// Worth recording because streaming is conditional — the loop only streams when an
    /// observer is listening — and that decision is otherwise invisible from outside.
    /// </summary>
    public bool StreamedLast => Volatile.Read(ref _streamed) != 0;

    /// <summary>Always replies with the same text.</summary>
    public static ScriptedLanguageModel Echoing(string reply, string instanceId = "scripted") =>
        new((_, _) => Text(reply), instanceId);

    /// <summary>Replies by transforming the last user message — handy for asserting isolation.</summary>
    public static ScriptedLanguageModel Transforming(
        Func<string, string> transform, string instanceId = "scripted") =>
        new((request, _) => Text(transform(LastUserText(request))), instanceId);

    /// <summary>Walks a fixed list of responses, repeating the last one once exhausted.</summary>
    public static ScriptedLanguageModel Sequence(params ModelResponse[] responses)
    {
        if (responses.Length == 0) throw new ArgumentException("At least one response is required.", nameof(responses));

        return new ScriptedLanguageModel((_, call) => responses[Math.Min(call, responses.Length - 1)]);
    }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        _requests.Enqueue(request);
        Interlocked.Exchange(ref _streamed, 0);
        int call = Interlocked.Increment(ref _calls) - 1;

        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        ModelResponse response = _respond(request, call);

        return response with { Usage = response.Usage with { ModelInstanceId = Descriptor.InstanceId } };
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ModelResponse response = await CompleteAsync(request, ct).ConfigureAwait(false);

        Interlocked.Exchange(ref _streamed, 1);

        foreach (ContentPart part in response.Content)
        {
            switch (part)
            {
                case TextPart text:
                    // Word by word, so sentence chunking for TTS has something to chunk.
                    foreach (string word in text.Text.Split(' '))
                    {
                        ct.ThrowIfCancellationRequested();
                        yield return new ModelStreamEvent.TextDelta(word + " ");
                    }
                    break;

                case ToolUsePart tool:
                    yield return new ModelStreamEvent.ToolUseStarted(tool.ToolCallId, tool.ToolName);
                    yield return new ModelStreamEvent.ToolArgsDelta(tool.ToolCallId, tool.Arguments.GetRawText());
                    break;
            }
        }

        yield return new ModelStreamEvent.Completed(response);
    }

    // ---- response builders -------------------------------------------------

    public static ModelResponse Text(string text, StopReason stop = StopReason.EndTurn) =>
        new([new TextPart(text)], stop, Usage(text));

    public static ModelResponse ToolCall(string toolName, object arguments, string? callId = null) =>
        new(
            [new ToolUsePart(
                callId ?? $"call_{Guid.NewGuid():n}"[..12],
                toolName,
                JsonSerializer.SerializeToElement(arguments))],
            StopReason.ToolUse,
            Usage(toolName));

    public static ModelResponse ToolCalls(params ToolUsePart[] calls) =>
        new([.. calls], StopReason.ToolUse, Usage(string.Join(',', calls.Select(c => c.ToolName))));

    private static TokenUsage Usage(string text) =>
        new(Input: 10, Output: Math.Max(1, text.Length / 4), CacheRead: 0, CacheWrite: 0,
            ModelInstanceId: "scripted", Latency: TimeSpan.Zero);

    private static string LastUserText(ModelRequest request) =>
        request.Messages.LastOrDefault(m => m.Role == LaneRole.User)?.TextContent ?? "";
}
