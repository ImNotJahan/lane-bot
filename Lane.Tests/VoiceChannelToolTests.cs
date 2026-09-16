using System.Text.Json;
using Lane.Core.Identity;
using Lane.Core.Tools;
using Lane.Core.Voice;
using Lane.Tools.Voice;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Where Lane sits in voice is hers to decide. v2's Discord body walked into a channel on
/// every server it could see the moment it connected; these are the two tools that replaced
/// that with being asked.
/// </summary>
public sealed class VoiceChannelToolTests
{
    private static ToolContext Context() =>
        new() { Services = new ServiceCollection().BuildServiceProvider() };

    private static ValueTask<ToolResult> Invoke(ITool tool, object args) =>
        tool.InvokeAsync(new ToolInvocation("c1", JsonSerializer.SerializeToElement(args), Context()), default);

    private sealed class FakeHost(string surface, params VoiceChannelSummary[] channels) : IVoiceChannelHost
    {
        public SurfaceId Surface { get; } = new(surface);

        public List<string> Joined { get; } = [];

        public bool Refuse { get; set; }

        public ValueTask<IReadOnlyList<VoiceChannelSummary>> ListChannelsAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<VoiceChannelSummary>>(channels);

        public ValueTask<VoiceJoinResult> JoinAsync(string channelId, CancellationToken ct)
        {
            if (Refuse) return ValueTask.FromResult(new VoiceJoinResult(false, "no audio here"));

            Joined.Add(channelId);

            return ValueTask.FromResult(new VoiceJoinResult(true, $"Joined {channelId}."));
        }
    }

    private static VoiceChannelSummary Channel(string surface, string id, string name, string space, int occupants = 0) =>
        new() { Surface = new SurfaceId(surface), Id = id, Name = name, Space = space, Occupants = occupants };

    [Fact]
    public async Task Listing_names_every_channel_with_its_server_and_how_busy_it_is()
    {
        VoiceChannelHosts hosts = new();
        hosts.Register(new FakeHost("discord.main",
            Channel("discord.main", "1", "General", "The Wired", 2),
            Channel("discord.main", "2", "AFK", "The Wired")));

        string text = (await Invoke(new ListVoiceChannelsTool(hosts), new { })).Text;

        Assert.Contains("1 — General in The Wired; 2 people", text);
        Assert.Contains("2 — AFK in The Wired; empty", text);
    }

    [Fact]
    public async Task With_no_surface_offering_voice_there_is_nothing_to_join()
    {
        VoiceChannelHosts hosts = new();

        Assert.Equal("(no voice channels you can join)", (await Invoke(new ListVoiceChannelsTool(hosts), new { })).Text);

        ToolResult result = await Invoke(new JoinVoiceChannelTool(hosts), new { channel = "General" });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_channel_can_be_joined_by_id_or_by_name()
    {
        VoiceChannelHosts hosts = new();
        FakeHost host = new("discord.main", Channel("discord.main", "1", "General", "The Wired"));
        hosts.Register(host);

        Assert.False((await Invoke(new JoinVoiceChannelTool(hosts), new { channel = "1" })).IsError);
        Assert.False((await Invoke(new JoinVoiceChannelTool(hosts), new { channel = "general" })).IsError);

        Assert.Equal(["1", "1"], host.Joined);
    }

    [Fact]
    public async Task A_name_two_servers_share_has_to_be_settled_by_id()
    {
        VoiceChannelHosts hosts = new();
        FakeHost host = new("discord.main",
            Channel("discord.main", "1", "General", "The Wired"),
            Channel("discord.main", "2", "General", "Elsewhere"));
        hosts.Register(host);

        ToolResult result = await Invoke(new JoinVoiceChannelTool(hosts), new { channel = "General" });

        Assert.True(result.IsError);
        Assert.Contains("Elsewhere", result.Text);
        Assert.Empty(host.Joined);

        Assert.False((await Invoke(new JoinVoiceChannelTool(hosts), new { channel = "2" })).IsError);
        Assert.Equal(["2"], host.Joined);
    }

    [Fact]
    public async Task Each_bot_is_asked_for_its_own_channels_and_joins_its_own()
    {
        VoiceChannelHosts hosts = new();
        FakeHost first  = new("discord.main", Channel("discord.main", "1", "General", "The Wired"));
        FakeHost second = new("discord.alt",  Channel("discord.alt", "2", "Lounge", "Elsewhere"));

        hosts.Register(first);
        hosts.Register(second);

        Assert.False((await Invoke(new JoinVoiceChannelTool(hosts), new { channel = "Lounge" })).IsError);

        Assert.Empty(first.Joined);
        Assert.Equal(["2"], second.Joined);
    }

    [Fact]
    public async Task A_surface_that_stops_stops_being_offered()
    {
        VoiceChannelHosts hosts = new();
        IDisposable registration = hosts.Register(new FakeHost("discord.main",
            Channel("discord.main", "1", "General", "The Wired")));

        registration.Dispose();

        Assert.Equal("(no voice channels you can join)", (await Invoke(new ListVoiceChannelsTool(hosts), new { })).Text);
    }

    [Fact]
    public async Task A_refusal_from_the_surface_reaches_her_as_an_error()
    {
        VoiceChannelHosts hosts = new();
        hosts.Register(new FakeHost("discord.main", Channel("discord.main", "1", "General", "The Wired"))
        {
            Refuse = true
        });

        ToolResult result = await Invoke(new JoinVoiceChannelTool(hosts), new { channel = "1" });

        Assert.True(result.IsError);
        Assert.Equal("no audio here", result.Text);
    }

    [Fact]
    public async Task One_host_failing_does_not_hide_the_others()
    {
        VoiceChannelHosts hosts = new();
        hosts.Register(new ThrowingHost());
        hosts.Register(new FakeHost("discord.alt", Channel("discord.alt", "2", "Lounge", "Elsewhere")));

        Assert.Contains("Lounge", (await Invoke(new ListVoiceChannelsTool(hosts), new { })).Text);
    }

    private sealed class ThrowingHost : IVoiceChannelHost
    {
        public SurfaceId Surface => new("discord.broken");

        public ValueTask<IReadOnlyList<VoiceChannelSummary>> ListChannelsAsync(CancellationToken ct) =>
            throw new InvalidOperationException("gateway is down");

        public ValueTask<VoiceJoinResult> JoinAsync(string channelId, CancellationToken ct) =>
            throw new InvalidOperationException("gateway is down");
    }
}
