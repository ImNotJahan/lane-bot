using Lane.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Mcp;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the configured MCP servers as a tool source.
    ///
    /// One instance serves both roles: the registry reads tools from it, and the host starts
    /// and stops it. Registering it twice would give two sets of child processes, one of
    /// which nobody would ever shut down.
    /// </summary>
    public static IServiceCollection AddLaneMcp(this IServiceCollection services, McpOptions options)
    {
        if (options.Servers.Count(s => s.Enabled) == 0) return services;

        services.AddSingleton(options);

        services.AddSingleton(sp => new McpToolSource(options, sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<IToolSource>(sp => sp.GetRequiredService<McpToolSource>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<McpToolSource>());

        return services;
    }
}
