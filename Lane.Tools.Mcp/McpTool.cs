using System.Text.Json;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Lane.Tools.Mcp;

/// <summary>
/// One tool on one MCP server, as far as the rest of Lane is concerned.
///
/// This is the whole integration surface. Nothing above it knows MCP exists: the registry
/// gates it, the agent loop calls it and memory records it exactly as it does a built-in
/// tool, because it is an <c>ITool</c> like any other. That was the point of having tools be
/// an interface rather than a switch statement.
/// </summary>
public sealed class McpTool(
    ToolDescriptor descriptor,
    McpServerConnection connection,
    string remoteName) : ITool
{
    public ToolDescriptor Descriptor => descriptor;

    /// <summary>What the server itself calls this tool, before prefixing and sanitisation.</summary>
    public string RemoteName => remoteName;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
    {
        McpClient? client = connection.Client;

        if (client is null)
            return ToolResult.Error(
                $"The '{connection.ServerId}' server is not connected right now. Try again shortly.");

        try
        {
            CallToolResult result = await client
                .CallToolAsync(remoteName, ToArguments(invocation.Arguments), cancellationToken: ct)
                .ConfigureAwait(false);

            return Convert(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Returned rather than thrown. The provider requires an answer to every call it
            // made, and a server going down mid-call must not poison the conversation.
            return ToolResult.Error($"{descriptor.Name} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The model's arguments, as the SDK wants them.
    ///
    /// Values are passed through as <see cref="JsonElement"/> rather than converted to CLR
    /// types: the schema is the server's, Lane has no model to bind it to, and re-encoding
    /// through <c>object</c> is where numeric precision and null-versus-absent go wrong.
    /// </summary>
    private static Dictionary<string, object?> ToArguments(JsonElement arguments)
    {
        Dictionary<string, object?> mapped = [];

        if (arguments.ValueKind != JsonValueKind.Object) return mapped;

        foreach (JsonProperty property in arguments.EnumerateObject())
            mapped[property.Name] = property.Value;

        return mapped;
    }

    private static ToolResult Convert(CallToolResult result)
    {
        List<ContentPart> content = [];

        foreach (ContentBlock block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text when !string.IsNullOrEmpty(text.Text):
                    content.Add(new TextPart(text.Text));
                    break;

                case ImageContentBlock image:
                    content.Add(new ImagePart(null, image.DecodedData, image.MimeType));
                    break;

                default:
                    // Audio, embedded resources and anything a later revision adds. Named
                    // rather than dropped, so a puzzling empty result is at least explicable.
                    content.Add(new TextPart($"({block.Type} content, which Lane cannot read)"));
                    break;
            }
        }

        if (content.Count == 0) content.Add(new TextPart("(the tool returned nothing)"));

        return new ToolResult { Content = content, IsError = result.IsError == true };
    }

    /// <summary>
    /// Builds the descriptor Lane advertises for a server-side tool.
    /// </summary>
    /// <param name="allowInMonologue">
    /// When false — the default — the tool is offered for replies but never during the
    /// monologue. See <see cref="McpServerOptions.AllowInMonologue"/> for why.
    /// </param>
    public static ToolDescriptor Describe(
        string serverId, Tool tool, TimeSpan timeout, int maxDescriptionLength, bool allowInMonologue) => new()
        {
            Name        = McpNaming.Qualify(serverId, tool.Name),
            Description = McpNaming.CleanDescription(tool.Description, maxDescriptionLength),
            InputSchema = tool.InputSchema,
            Timeout     = timeout,
            SourceId    = $"mcp:{serverId}",

            // Mutating, not ReadOnly: Lane cannot know what somebody else's tool does, and
            // the safe assumption is the one that does not under-report.
            Safety = ToolSafety.Mutating,

            Availability = new ToolAvailability
            {
                AllowedTurns = allowInMonologue
                    ? TurnKind.All
                    : TurnKind.Respond | TurnKind.Directive,

                Tags = ["mcp", serverId]
            }
        };
}
