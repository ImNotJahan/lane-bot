using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lane.Surfaces.Api;

public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

// ---- requests -------------------------------------------------------------

/// <param name="Key">
/// The client's own name for this conversation. Namespaced by client id unless it begins
/// with <c>shared:</c>, which is how two apps deliberately meet in one conversation.
/// </param>
public sealed record CreateSessionRequest(
    string? Key         = null,
    string? DisplayName = null,
    string? MemoryGroup = null,
    bool?   Direct      = null);

/// <param name="Author">
/// Who is speaking, when the client is relaying someone. Namespaced under the client, so a
/// key can only ever invent identities inside its own conversations.
/// </param>
/// <param name="RequiresResponse">
/// False for things Lane should remember but not answer — a client posting context rather
/// than talking to her.
/// </param>
public sealed record SendMessageRequest(
    string  Text,
    ApiAuthor? Author      = null,
    bool    RequiresResponse = true,
    string? ExternalId    = null);

public sealed record ApiAuthor(string Id, string? Name = null);

// ---- responses ------------------------------------------------------------

public sealed record SessionResponse(
    string Id,
    string Key,
    string DisplayName,
    string Surface,
    string Kind,
    string MemoryGroup,
    string State,
    bool   Mine,
    DateTimeOffset LastActivity,
    IReadOnlyList<string> Participants,
    IReadOnlyList<string> Capabilities);

public sealed record AcceptedResponse(string SessionId, string MessageId);

public sealed record MessageResponse(
    string  Id,
    long    Sequence,
    string  Role,
    string  Kind,
    string  Author,
    string  Text,
    DateTimeOffset Timestamp,
    string? ExternalId);

public sealed record HistoryResponse(string SessionId, IReadOnlyList<MessageResponse> Messages);

public sealed record ErrorResponse(string Error, string? Detail = null);

// ---- stream frames --------------------------------------------------------

public sealed record DeltaFrame(string Text);

public sealed record ToolFrame(string Name, string Phase, bool? IsError = null);

public sealed record MessageFrame(string SessionId, string Text, string? ReplyTo);

public sealed record DoneFrame(bool Silent, string? Reason);

public sealed record ErrorFrame(string Error);
