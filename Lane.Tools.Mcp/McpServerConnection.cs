using Lane.Core.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Lane.Tools.Mcp;

/// <summary>
/// One MCP server, kept alive.
///
/// A stdio server is a child process: it can crash, be killed, or never start at all. The
/// rule this class exists to enforce is that none of that reaches a conversation. A server
/// that is down contributes no tools and Lane simply has fewer abilities for a while; she
/// does not fail a turn, and she does not stop retrying.
/// </summary>
public sealed class McpServerConnection : IAsyncDisposable
{
    private readonly McpServerOptions _options;
    private readonly McpOptions       _shared;
    private readonly ILoggerFactory   _loggers;
    private readonly ILogger          _log;

    private readonly CancellationTokenSource _lifetime = new();

    private volatile McpClient?           _client;
    private volatile IReadOnlyList<ITool> _tools = [];

    private Task? _supervisor;

    /// <summary>Set when the advertised tools actually differ, not merely when we reconnect.</summary>
    public event Action<string>? ToolsChanged;

    public McpServerConnection(
        McpServerOptions options, McpOptions shared, ILoggerFactory loggers)
    {
        _options = options;
        _shared  = shared;
        _loggers = loggers;
        _log     = loggers.CreateLogger($"Lane.Mcp.{options.Id}");
    }

    public string ServerId => _options.Id;

    /// <summary>Null while disconnected. Tools check this rather than assuming.</summary>
    public McpClient? Client => _client;

    public IReadOnlyList<ITool> Tools => _tools;

    public bool IsConnected => _client is not null;

    public void Start() => _supervisor ??= Task.Run(() => SuperviseAsync(_lifetime.Token), CancellationToken.None);

    /// <summary>
    /// Connect, serve, and on failure wait longer each time before trying again.
    ///
    /// The backoff is not politeness. A server that crashes on startup would otherwise be
    /// respawned in a tight loop, and because the tool list is part of the prompt prefix,
    /// every flap would invalidate the provider's prompt cache — a failure that shows up as
    /// a bill rather than an error.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        int failures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using McpClient client = await ConnectAsync(ct).ConfigureAwait(false);

                _client = client;
                failures = 0;

                _log.LogInformation("Connected to MCP server '{Server}' ({Info})",
                    _options.Id, client.ServerInfo?.Name ?? "unnamed");

                await RefreshToolsAsync(client, ct).ConfigureAwait(false);

                // Returns when the session ends, however it ends.
                await client.Completion.WaitAsync(ct).ConfigureAwait(false);

                _log.LogWarning("MCP server '{Server}' closed the session", _options.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;

                // First failure at Error so a typo in the command is noticed; the retries
                // that follow at Warning, so a server that is simply not installed does not
                // fill the log for the rest of the process's life.
                _log.Log(failures == 1 ? LogLevel.Error : LogLevel.Warning, ex,
                    "MCP server '{Server}' is unavailable (attempt {Attempt})", _options.Id, failures);
            }
            finally
            {
                _client = null;

                // Tools vanish with the server. Advertising a tool that cannot be called is
                // worse than having fewer: the model picks it and gets an error back.
                Publish([]);
            }

            if (ct.IsCancellationRequested) break;

            TimeSpan wait = Backoff(failures);

            _log.LogInformation("Retrying MCP server '{Server}' in {Delay:0.#}s", _options.Id, wait.TotalSeconds);

            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogDebug("Supervisor for MCP server '{Server}' stopped", _options.Id);
    }

    private TimeSpan Backoff(int failures)
    {
        if (failures <= 0) return _shared.RetryDelay;

        double seconds = _shared.RetryDelay.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 10));

        seconds = Math.Min(seconds, _shared.MaxRetryDelay.TotalSeconds);

        // Jitter, so several servers failing together do not retry in lockstep forever.
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    private async Task<McpClient> ConnectAsync(CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

        timeout.CancelAfter(_shared.ConnectTimeout);

        McpClientOptions options = new()
        {
            ClientInfo = new Implementation { Name = "Lane", Version = "3.0" },

            Handlers = new McpClientHandlers
            {
                NotificationHandlers =
                [
                    new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                        NotificationMethods.ToolListChangedNotification, OnToolListChangedAsync)
                ]
            }
        };

        return await McpClient
            .CreateAsync(BuildTransport(), options, _loggers, timeout.Token)
            .ConfigureAwait(false);
    }

    private IClientTransport BuildTransport()
    {
        if (_options.Transport == McpTransport.Http)
        {
            if (string.IsNullOrWhiteSpace(_options.Url))
                throw new InvalidOperationException($"MCP server '{_options.Id}' is http but has no Url.");

            HttpClientTransportOptions http = new()
            {
                Name             = _options.Id,
                Endpoint         = new Uri(_options.Url),
                AdditionalHeaders = new Dictionary<string, string>(_options.Headers)
            };

            return new HttpClientTransport(http, _loggers);
        }

        if (string.IsNullOrWhiteSpace(_options.Command))
            throw new InvalidOperationException($"MCP server '{_options.Id}' is stdio but has no Command.");

        StdioClientTransportOptions stdio = new()
        {
            Name             = _options.Id,
            Command          = _options.Command,
            Arguments        = [.. _options.Args],
            WorkingDirectory = _options.WorkingDirectory,
            ShutdownTimeout  = _shared.ShutdownTimeout,

            // Otherwise a child process's diagnostics go to the terminal, which the
            // dashboard owns, and are lost entirely when it is running.
            StandardErrorLines = line => _log.LogDebug("[{Server} stderr] {Line}", _options.Id, line),

            EnvironmentVariables = new Dictionary<string, string?>(
                _options.Env.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
        };

        return new StdioClientTransport(stdio, _loggers);
    }

    private async ValueTask OnToolListChangedAsync(JsonRpcNotification notification, CancellationToken ct)
    {
        McpClient? client = _client;

        if (client is null) return;

        _log.LogInformation("MCP server '{Server}' says its tools changed", _options.Id);

        try
        {
            await RefreshToolsAsync(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not re-read tools from MCP server '{Server}'", _options.Id);
        }
    }

    private async Task RefreshToolsAsync(McpClient client, CancellationToken ct)
    {
        IList<McpClientTool> remote = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);

        List<ITool> built = [];
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

        foreach (McpClientTool tool in remote)
        {
            string name = tool.ProtocolTool.Name;

            if (_options.Deny.Any(p => McpNaming.Matches(name, p)))
            {
                _log.LogDebug("Tool '{Tool}' on '{Server}' is denied by configuration", name, _options.Id);
                continue;
            }

            if (_options.Allow.Count > 0 && !_options.Allow.Any(p => McpNaming.Matches(name, p)))
            {
                _log.LogDebug("Tool '{Tool}' on '{Server}' is not in the allow list", name, _options.Id);
                continue;
            }

            ToolDescriptor descriptor = McpTool.Describe(
                _options.Id, tool.ProtocolTool, _options.Timeout,
                _shared.MaxDescriptionLength, _options.AllowInMonologue);

            // Sanitisation can map two different server-side names onto one. Dropping the
            // second is the only safe answer: keeping it would mean two tools whose calls
            // are indistinguishable once the model picks one.
            if (!taken.Add(descriptor.Name))
            {
                _log.LogWarning(
                    "Tool '{Tool}' on '{Server}' collides with another after sanitising to '{Name}'; ignoring it",
                    name, _options.Id, descriptor.Name);
                continue;
            }

            built.Add(new McpTool(descriptor, this, name));
        }

        _log.LogInformation("MCP server '{Server}' offers {Count} tool(s): {Names}",
            _options.Id, built.Count, string.Join(", ", built.Select(t => t.Descriptor.Name)));

        Publish(built);
    }

    /// <summary>
    /// Swaps the tool list in, and only announces a change when there genuinely is one.
    ///
    /// Reconnecting to a server that offers exactly what it offered before must not count:
    /// the announcement invalidates the cached tool set, and the tool list is part of the
    /// prompt prefix every provider hashes for its cache.
    /// </summary>
    private void Publish(IReadOnlyList<ITool> tools)
    {
        if (Signature(_tools) == Signature(tools)) { _tools = tools; return; }

        _tools = tools;

        try { ToolsChanged?.Invoke($"mcp:{_options.Id}"); }
        catch (Exception ex) { _log.LogWarning(ex, "A tool-change subscriber threw"); }
    }

    private static string Signature(IReadOnlyList<ITool> tools) =>
        string.Join('', tools.Select(t => t.Descriptor.Name + '' + t.Descriptor.Description).Order());

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_supervisor is not null)
        {
            // Deliberately longer than the shutdown timeout the supervisor is inside. Giving
            // up first would return while the child process was still being killed, which is
            // how orphaned server processes outlive the host that started them.
            TimeSpan grace = _shared.ShutdownTimeout + TimeSpan.FromSeconds(2);

            try { await _supervisor.WaitAsync(grace).ConfigureAwait(false); }
            catch (Exception) { _log.LogWarning("MCP server '{Server}' did not shut down cleanly", _options.Id); }
        }

        _lifetime.Dispose();
    }
}
