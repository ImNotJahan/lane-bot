using Lane.Core.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Mcp;

/// <summary>
/// Every configured MCP server, as one source of tools.
///
/// It is a hosted service as well as a source because the servers are long-lived things —
/// child processes and open sockets — that have to start with the host and be shut down
/// with it. What it hands the registry is only ever whatever is connected right now.
/// </summary>
public sealed class McpToolSource : IToolSource, IHostedService, IAsyncDisposable
{
    private readonly List<McpServerConnection> _connections = [];
    private readonly ILogger<McpToolSource>    _log;

    public McpToolSource(McpOptions options, ILoggerFactory loggers)
    {
        _log = loggers.CreateLogger<McpToolSource>();

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (McpServerOptions server in options.Servers)
        {
            if (!server.Enabled) continue;

            if (!McpNaming.IsValidServerId(server.Id))
                throw new InvalidOperationException(
                    $"MCP server id '{server.Id}' must be 1–32 characters of letters, digits, '-' or '_'. " +
                    "It becomes part of every tool name this server contributes.");

            if (!seen.Add(server.Id))
                throw new InvalidOperationException(
                    $"Two MCP servers share the id '{server.Id}'. Ids namespace tool names and must be unique.");

            McpServerConnection connection = new(server, options, loggers);

            connection.ToolsChanged += Raise;

            _connections.Add(connection);
        }
    }

    public string SourceId => "mcp";

    public event Action<string>? ToolsChanged;

    public IReadOnlyList<McpServerConnection> Connections => _connections;

    public Task StartAsync(CancellationToken ct)
    {
        if (_connections.Count == 0) return Task.CompletedTask;

        _log.LogInformation("Starting {Count} MCP server(s): {Servers}",
            _connections.Count, string.Join(", ", _connections.Select(c => c.ServerId)));

        // Not awaited. A server that takes thirty seconds to install itself with npx must
        // not hold up Discord, the terminal and the API behind it.
        foreach (McpServerConnection connection in _connections) connection.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken ct)
    {
        List<ITool> all = [];

        foreach (McpServerConnection connection in _connections) all.AddRange(connection.Tools);

        return ValueTask.FromResult<IReadOnlyList<ITool>>(all);
    }

    private void Raise(string source)
    {
        try { ToolsChanged?.Invoke(source); }
        catch (Exception ex) { _log.LogWarning(ex, "A tool-change subscriber threw"); }
    }

    public async ValueTask DisposeAsync()
    {
        // Together, not one after another. Every stdio server costs a shutdown timeout
        // whether or not it deserves one, and serialising them would multiply that by the
        // number configured — straight onto the time it takes Lane to stop.
        await Task.WhenAll(_connections.Select(async c => await c.DisposeAsync().ConfigureAwait(false)))
                  .ConfigureAwait(false);

        _connections.Clear();
    }
}
