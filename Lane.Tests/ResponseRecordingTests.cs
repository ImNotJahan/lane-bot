using System.Text.Json;
using System.Text.Json.Nodes;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Recording;
using Lane.Core.Sessions;
using Lane.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class ResponseRecordingTests
{
    private static readonly SurfaceId       Discord = new("discord.main");
    private static readonly SessionId       General = new(Discord, SessionKind.Text, "general");
    private static readonly ModelDescriptor Model   = new("gemini", "openrouter", "google/gemini", ModelCapabilities.Tools);
    private static readonly Participant     Lane    = Participant.Lane(Discord);

    private static Participant Person(string localId, string name, string? globalId = null) =>
        new(new ParticipantId(Discord, localId), name, globalId);

    [Fact]
    public void Every_name_a_person_goes_by_gets_one_placeholder()
    {
        NameRedactor redactor = new([Person("1", "Alice", "alice-3f9a"), Person("2", "Bob"), Lane]);

        Assert.Equal(
            "[NAME_1] told [NAME_2] that [NAME_1] likes Lane",
            redactor.Redact("Alice told bob that alice-3f9a likes Lane"));
    }

    [Fact]
    public void Only_whole_words_are_redacted()
    {
        NameRedactor redactor = new([Person("1", "Al")]);

        Assert.Equal("[NAME_1] went to Alabama, @[NAME_1]", redactor.Redact("Al went to Alabama, @al"));
    }

    [Fact]
    public void Prompt_tool_traffic_and_completion_are_all_redacted()
    {
        DateTimeOffset now   = DateTimeOffset.UtcNow;
        Participant    alice = Person("1", "Alice");

        ModelRequest request = new()
        {
            System   = [new PromptBlock("You are talking with Alice.")],
            Messages =
            [
                LaneMessage.User(General, alice, "hi, I'm Alice", now),
                LaneMessage.Assistant(General, Lane,
                    [new ToolUsePart("c1", "remember", JsonSerializer.SerializeToElement(new { note = "Alice likes tea" }))], now),
                LaneMessage.ToolResults(General, Lane, [ToolResultPart.Text("c1", "Saved a note about Alice")], now)
            ],
            CacheLineage = "respond:gemini|tools:abc"
        };

        JsonObject record = ResponseRecord.Build(
            Model, request, ScriptedLanguageModel.Text("Nice to meet you, Alice."), new NameRedactor([alice]), now);

        Assert.DoesNotContain("Alice", record.ToJsonString());
        Assert.Equal("respond", (string?)record["task"]);
        Assert.Equal("provider", (string?)record["source"]);

        JsonArray messages = record["messages"]!.AsArray();

        Assert.Equal<string?>(["system", "user", "assistant", "tool"], messages.Select(m => (string?)m!["role"]));
        Assert.Equal("You are talking with [NAME_1].", (string?)messages[0]!["content"]);
        Assert.Equal("[NAME_1]: hi, I'm [NAME_1]\n", (string?)messages[1]!["content"]);
        Assert.Equal("[NAME_1] likes tea", (string?)messages[2]!["tool_calls"]![0]!["function"]!["arguments"]!["note"]);
        Assert.Equal("Nice to meet you, [NAME_1].", (string?)record["completion"]!["content"]);
    }

    [Fact]
    public void Node_answers_name_the_node()
    {
        ModelRequest request = new()
        {
            System   = [],
            Messages = [LaneMessage.User(General, Person("1", "Alice"), "hi", DateTimeOffset.UtcNow)]
        };

        ModelResponse response = ScriptedLanguageModel.Text("hello") with
        {
            Origin = new NodeAttestation(new NodeIdentity("abc123", "ecdsa-p256-sha256", ""), "sig")
        };

        JsonObject record = ResponseRecord.Build(Model, request, response, new NameRedactor([]), DateTimeOffset.UtcNow);

        Assert.Equal("node", (string?)record["source"]);
        Assert.Equal("abc123", (string?)record["node"]!["key_id"]);
    }

    [Fact]
    public async Task Calls_are_written_as_they_were_when_the_model_answered()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"lane-recording-{Guid.NewGuid():n}");

        try
        {
            JsonlResponseRecorder recorder = new(
                new ResponseRecordingOptions { Directory = directory },
                () => [Person("2", "Bob")],
                TimeProvider.System,
                NullLogger<JsonlResponseRecorder>.Instance);

            await recorder.StartAsync(CancellationToken.None);

            RecordingLanguageModel model = new(ScriptedLanguageModel.Echoing("hello Bob"), recorder);

            List<LaneMessage> conversation = [LaneMessage.User(General, Person("1", "Alice"), "hi", DateTimeOffset.UtcNow)];
            ModelRequest      request      = new() { System = [], Messages = conversation };

            await model.CompleteAsync(request, CancellationToken.None);

            conversation.Add(LaneMessage.User(General, Person("1", "Alice"), "still there?", DateTimeOffset.UtcNow));

            await foreach (ModelStreamEvent _ in model.StreamAsync(request, CancellationToken.None)) { }

            await recorder.StopAsync(CancellationToken.None);

            string[] lines = [.. Directory.GetFiles(directory, "*.jsonl").SelectMany(File.ReadAllLines)];

            Assert.Equal(2, lines.Length);

            JsonNode first  = JsonNode.Parse(lines[0])!;
            JsonNode second = JsonNode.Parse(lines[1])!;

            Assert.Single(first["messages"]!.AsArray());
            Assert.Equal(2, second["messages"]!.AsArray().Count);
            Assert.Equal("[NAME_1]: hi\n", (string?)first["messages"]![0]!["content"]);
            Assert.Equal("hello [NAME_2]", (string?)first["completion"]!["content"]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
