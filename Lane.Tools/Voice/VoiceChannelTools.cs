using System.ComponentModel;
using System.Text;
using Lane.Core.Tools;
using Lane.Core.Voice;

namespace Lane.Tools.Voice;

/// <summary>
/// Lane's view of where she could go and listen. Without it <c>join_voice_channel</c> has
/// nothing to name.
/// </summary>
[LaneTool]
public sealed class ListVoiceChannelsTool(VoiceChannelHosts hosts) : Tool<NoArgs>
{
    protected override string Name => "list_voice_channels";

    protected override string Description =>
        "List the voice channels you could join, with the server each is in and how many people are in it. " +
        "Use before join_voice_channel so you know what you are joining.";

    protected override async ValueTask<ToolResult> InvokeAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        IReadOnlyList<VoiceChannelSummary> channels = await ListAsync(hosts, ct).ConfigureAwait(false);

        if (channels.Count == 0) return ToolResult.Ok("(no voice channels you can join)");

        StringBuilder sb = new();

        foreach (VoiceChannelSummary channel in channels)
        {
            sb.Append(channel.Id)
              .Append(" — ").Append(channel.Name);

            if (channel.Space is { Length: > 0 } space) sb.Append(" in ").Append(space);

            sb.Append(channel.Occupants switch
            {
                0 => "; empty",
                1 => "; 1 person",
                int n => $"; {n} people"
            });

            if (channel.Joined) sb.Append("; you are in this one");

            sb.AppendLine();
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    /// <summary>Every host's channels in one list. A host that cannot answer contributes none.</summary>
    internal static async Task<IReadOnlyList<VoiceChannelSummary>> ListAsync(
        VoiceChannelHosts hosts, CancellationToken ct)
    {
        List<VoiceChannelSummary> channels = [];

        foreach (IVoiceChannelHost host in hosts.All)
        {
            try { channels.AddRange(await host.ListChannelsAsync(ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        return [.. channels.OrderBy(c => c.Space, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>
/// Sitting in a voice channel is something she decides, not something the config decides
/// for her at startup. She leaves on her own once the channel has been quiet a while.
/// </summary>
[LaneTool]
public sealed class JoinVoiceChannelTool(VoiceChannelHosts hosts) : Tool<JoinVoiceChannelTool.Args>
{
    public sealed record Args(
        [property: Description("Channel id exactly as list_voice_channels gave it, or its name.")] string Channel);

    protected override string Name => "join_voice_channel";

    protected override string Description =>
        "Join a voice channel and listen. Do it when someone asks you to come and talk. " +
        "You leave by yourself once nothing has been said there for a while.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override TimeSpan Timeout => TimeSpan.FromSeconds(60);

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        string wanted = args.Channel?.Trim() ?? "";

        if (wanted.Length == 0) return ToolResult.Error("A channel id or name is required.");

        IReadOnlyList<VoiceChannelSummary> channels =
            await ListVoiceChannelsTool.ListAsync(hosts, ct).ConfigureAwait(false);

        if (channels.Count == 0) return ToolResult.Error("There are no voice channels you can join.");

        VoiceChannelSummary[] matches = [.. channels.Where(c => c.Id == wanted)];

        if (matches.Length == 0)
            matches = [.. channels.Where(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase))];

        if (matches.Length == 0)
            return ToolResult.Error($"No voice channel called \"{wanted}\". Run list_voice_channels first.");

        // Two servers can both have a "General"; the ids are what tells them apart.
        if (matches.Length > 1)
            return ToolResult.Error(
                $"\"{wanted}\" matches {matches.Length} channels: " +
                string.Join(", ", matches.Select(c => $"{c.Id} in {c.Space}")) +
                ". Name one by its id.");

        VoiceChannelSummary match = matches[0];

        IVoiceChannelHost? host = hosts.All.FirstOrDefault(h => h.Surface == match.Surface);

        if (host is null) return ToolResult.Error($"{match.Name} is no longer reachable.");

        VoiceJoinResult result = await host.JoinAsync(match.Id, ct).ConfigureAwait(false);

        return result.Joined ? ToolResult.Ok(result.Detail) : ToolResult.Error(result.Detail);
    }
}
