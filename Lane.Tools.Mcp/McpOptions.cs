namespace Lane.Tools.Mcp;

public enum McpTransport
{
    /// <summary>A child process speaking JSON-RPC over its own stdin and stdout.</summary>
    Stdio,

    /// <summary>Streamable HTTP, or the older SSE transport, at a URL.</summary>
    Http
}

public sealed class McpOptions
{
    public List<McpServerOptions> Servers { get; set; } = [];

    /// <summary>How long to wait for a server to finish initialising before giving up on it.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>First retry delay. Doubles per failure, up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long to wait for a stdio server's process to exit before killing it.
    ///
    /// This is paid on every shutdown, not only by servers that misbehave: the SDK does not
    /// close the child's stdin, so a server waiting politely for end-of-input never gets it
    /// and is killed once this elapses. The SDK's own default is five seconds, which is a
    /// long time to stare at a process that will not exit — twice over, if two are configured.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Longest tool description Lane will pass on to the model.
    ///
    /// A description is text a third party wrote that ends up verbatim in the prompt. A cap
    /// bounds both the token cost and how much room a hostile one has to work with.
    /// </summary>
    public int MaxDescriptionLength { get; set; } = 1024;
}

/// <summary>
/// One MCP server.
///
/// Servers are named here and nowhere else: none is discovered automatically, because
/// connecting to a server means putting text it authored into Lane's prompt and letting the
/// model call code it controls. That should be a decision someone made on purpose.
/// </summary>
public sealed class McpServerOptions
{
    /// <summary>Namespaces this server's tools as <c>mcp__{id}__{tool}</c>.</summary>
    public string Id { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public McpTransport Transport { get; set; } = McpTransport.Stdio;

    // ---- stdio ----

    public string  Command          { get; set; } = "";
    public List<string> Args        { get; set; } = [];
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Extra environment for the child process. Values may be secret references
    /// (<c>env:NAME</c>, <c>file:/path</c>) and are resolved once at composition.
    /// </summary>
    public Dictionary<string, string> Env { get; set; } = [];

    // ---- http ----

    public string? Url { get; set; }

    /// <summary>Values may be secret references, resolved at composition.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    // ---- policy ----

    /// <summary>How long one call may take before it is abandoned and answered as an error.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Offer this server's tools to the monologue as well.
    ///
    /// Off by default, and that default is the point. The monologue runs unattended on a
    /// timer with nobody reading it, so a tool whose description is written by somebody else
    /// is at its most dangerous exactly there — an instruction smuggled into a description
    /// gets followed with no one present to notice.
    /// </summary>
    public bool AllowInMonologue { get; set; }

    /// <summary>Tool-name globs, matched against the *unprefixed* name the server reports.</summary>
    public List<string> Allow { get; set; } = ["*"];

    public List<string> Deny { get; set; } = [];
}
