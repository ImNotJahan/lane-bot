using System.Text.Json;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Microsoft.Extensions.Logging;

namespace Lane.Host.Migration;

public sealed record MigrationReport(int Messages, int Thoughts, int BookPositions, int Skipped)
{
    public override string ToString() =>
        $"{Messages} message(s), {Thoughts} thought(s), {BookPositions} book position(s); {Skipped} skipped";
}

/// <summary>
/// Brings what v2 remembered into v3's transcript.
///
/// Only the transcript: v2's handler state is a snapshot of windows whose shapes have all
/// changed, so replaying it into the new handlers would be reconstructing a cache. The
/// durable thing is what was actually said, and the new handlers rebuild themselves from
/// that as the conversation continues.
///
/// v2 wrote everything into one <c>data.json</c> keyed by handler id, each holding
/// <c>MessageContainer</c> objects: <c>{ content, author, time, type }</c> with author
/// 0=user/1=Lane and type 0=text/1=image/2=thought.
/// </summary>
public sealed class V2Migrator(ITranscriptStore transcript, IKeyValueStore keyValues, ILogger<V2Migrator> log)
{
    private static readonly ScopeKey Global = new("global");

    public async Task<MigrationReport> MigrateAsync(
        string dataPath, SessionId target, bool dryRun, CancellationToken ct)
    {
        if (!File.Exists(dataPath)) throw new FileNotFoundException($"No v2 data file at {dataPath}", dataPath);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(dataPath, ct).ConfigureAwait(false));

        JsonElement root = document.RootElement;

        Participant person = new(new ParticipantId(target.Surface, "v2"), "them");
        Participant lane   = Participant.Lane(target.Surface);

        List<LaneMessage> collected = [];
        int skipped = 0;

        foreach (JsonProperty handler in root.EnumerateObject())
        {
            if (handler.NameEquals("BookPositions")) continue;

            foreach (JsonElement raw in Messages(handler.Value))
            {
                LaneMessage? message = Convert(raw, target, person, lane);

                if (message is null) { skipped++; continue; }

                collected.Add(message);
            }
        }

        // v2 kept the same message in several handlers at once — a sliding window and the
        // RAG write buffer both held it — so the same text arrives more than once here.
        List<LaneMessage> ordered =
        [
            .. collected
                .GroupBy(m => (m.TextContent, m.Timestamp))
                .Select(g => g.First())
                .OrderBy(m => m.Timestamp)
        ];

        int thoughts = ordered.Count(m => m.Kind == MessageKind.Thought);

        int positions = 0;

        if (root.TryGetProperty("BookPositions", out JsonElement books) &&
            books.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty book in books.EnumerateObject())
            {
                if (!book.Value.TryGetInt32(out int position)) continue;

                if (!dryRun)
                    await keyValues.SetAsync(Global, $"book:{book.Name}", position, ct).ConfigureAwait(false);

                positions++;
            }
        }

        if (!dryRun)
        {
            foreach (LaneMessage message in ordered)
                await transcript.AppendAsync(message, ct).ConfigureAwait(false);
        }

        MigrationReport report = new(ordered.Count - thoughts, thoughts, positions, skipped);

        log.LogInformation("{Mode}: {Report}", dryRun ? "Would migrate" : "Migrated", report);

        return report;
    }

    /// <summary>v2 handler states were either a bare array or an object with a message list.</summary>
    private static IEnumerable<JsonElement> Messages(JsonElement state)
    {
        if (state.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in state.EnumerateArray()) yield return item;
            yield break;
        }

        if (state.ValueKind != JsonValueKind.Object) yield break;

        foreach (string name in (string[])["messages", "Messages", "window", "summary", "Summary"])
        {
            if (!state.TryGetProperty(name, out JsonElement found)) continue;

            if (found.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in found.EnumerateArray()) yield return item;
            }
            else if (found.ValueKind == JsonValueKind.Object)
            {
                yield return found;
            }
        }
    }

    private static LaneMessage? Convert(
        JsonElement raw, SessionId target, Participant person, Participant lane)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;

        if (!raw.TryGetProperty("content", out JsonElement contentElement)) return null;

        string content = contentElement.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(content)) return null;

        int author = raw.TryGetProperty("author", out JsonElement a) && a.TryGetInt32(out int parsedAuthor)
            ? parsedAuthor : 0;

        int type = raw.TryGetProperty("type", out JsonElement t) && t.TryGetInt32(out int parsedType)
            ? parsedType : 0;

        DateTimeOffset timestamp =
            raw.TryGetProperty("time", out JsonElement time) && time.ValueKind != JsonValueKind.Null &&
            time.TryGetDateTimeOffset(out DateTimeOffset parsedTime)
                ? parsedTime
                : DateTimeOffset.UnixEpoch;

        bool fromLane = author == 1;

        // v2 type: 0 text, 1 image, 2 thought.
        MessageKind kind = type == 2 ? MessageKind.Thought : MessageKind.Utterance;

        // Thoughts belonged to no conversation then and do not now.
        SessionId? session = kind == MessageKind.Thought ? null : target;

        return new LaneMessage
        {
            Id        = MessageId.New(),
            Session   = session,
            Role      = fromLane ? LaneRole.Assistant : LaneRole.User,
            Kind      = kind,
            Author    = fromLane ? lane : person,
            Content   = [new TextPart(content)],
            Timestamp = timestamp
        };
    }
}
