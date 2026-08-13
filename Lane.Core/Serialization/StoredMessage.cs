using System.Text.Json;
using System.Text.Json.Serialization;
using Lane.Core.Identity;
using Lane.Core.Messages;

namespace Lane.Core.Serialization;

/// <summary>
/// The on-disk shape of a message.
///
/// Deliberately a separate type from <see cref="LaneMessage"/>. The in-memory model is
/// free to change — nested records, new conveniences, different nullability — without
/// rewriting everything already on disk, and the persisted format stays flat and readable
/// when someone opens the database by hand.
/// </summary>
public sealed record StoredMessage
{
    /// <summary>Bumped when this shape changes incompatibly. Readers tolerate older values.</summary>
    public int V { get; init; } = 1;

    public required string  Id      { get; init; }
    public string?          Session { get; init; }
    public required int     Role    { get; init; }
    public required int     Kind    { get; init; }

    public required string AuthorSurface { get; init; }
    public required string AuthorId      { get; init; }
    public required string AuthorName    { get; init; }
    public string?         GlobalUserId  { get; init; }
    public bool            IsLane        { get; init; }

    public required IReadOnlyList<ContentPart> Content { get; init; }

    public required DateTimeOffset Timestamp  { get; init; }
    public long                    Sequence   { get; init; }
    public string?                 ExternalId { get; init; }
}

public static class LaneJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false
    };

    public static StoredMessage ToStored(LaneMessage message) => new()
    {
        Id            = message.Id.Value.ToString("n"),
        Session       = message.Session?.Value,
        Role          = (int)message.Role,
        Kind          = (int)message.Kind,
        AuthorSurface = message.Author.Id.Surface.Value,
        AuthorId      = message.Author.Id.LocalId,
        AuthorName    = message.Author.DisplayName,
        GlobalUserId  = message.Author.GlobalUserId,
        IsLane        = message.Author.IsLane,
        Content       = message.Content,
        Timestamp     = message.Timestamp,
        Sequence      = message.Sequence,
        ExternalId    = message.ExternalId
    };

    public static LaneMessage FromStored(StoredMessage stored)
    {
        SessionId? session = null;
        if (stored.Session is not null) SessionId.TryParse(stored.Session, out session);

        Participant author = new(
            new ParticipantId(new SurfaceId(stored.AuthorSurface), stored.AuthorId),
            stored.AuthorName,
            stored.GlobalUserId,
            stored.IsLane);

        return new LaneMessage
        {
            // A malformed id is not worth failing a load over; a fresh one keeps the
            // message readable and only costs its identity.
            Id         = Guid.TryParseExact(stored.Id, "n", out Guid id) ? new MessageId(id) : MessageId.New(),
            Session    = session,
            Role       = Enum.IsDefined((LaneRole)stored.Role) ? (LaneRole)stored.Role : LaneRole.User,
            Kind       = Enum.IsDefined((MessageKind)stored.Kind) ? (MessageKind)stored.Kind : MessageKind.Utterance,
            Author     = author,
            Content    = stored.Content,
            Timestamp  = stored.Timestamp,
            Sequence   = stored.Sequence,
            ExternalId = stored.ExternalId
        };
    }

    public static string SerializeContent(IReadOnlyList<ContentPart> content) =>
        JsonSerializer.Serialize(content, Options);

    public static IReadOnlyList<ContentPart> DeserializeContent(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ContentPart>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            // A part type this build does not know about must not take out the whole load.
            return [];
        }
    }
}
