using Microsoft.Extensions.Configuration;

namespace Lane.Host.Configuration;

public static class LaneConfiguration
{
    /// <summary>
    /// Where Lane's settings come from, in one place.
    ///
    /// Shared so that a side command like `say` reads exactly what the running bot reads —
    /// a voice tested against different configuration than the one that speaks in a channel
    /// is worse than no test at all.
    ///
    /// Anchored to the assembly directory, not the shell's cwd, so `dotnet run` from the
    /// repo root and a published binary behave the same.
    /// </summary>
    public static IConfigurationBuilder AddLaneSources(this IConfigurationBuilder builder, bool reloadOnChange = true) =>
        builder
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: reloadOnChange)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: reloadOnChange)
            .AddEnvironmentVariables("LANE_");
}
