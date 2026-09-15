using System.Text.Json;
using Lane.Core.Identity;
using Lane.Core.Pipeline.Stages;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Memory.Sqlite;
using Lane.Tools.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lane.Tests;

public sealed class SessionThresholdTests
{
    private readonly LaneDatabase _database = new(
        new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance);

    private SessionThresholds Thresholds() =>
        new(new SqliteKeyValueStore(_database), NullLogger<SessionThresholds>.Instance);

    private static SetResponseThresholdTool Tool(ISessionThresholds thresholds) =>
        new(thresholds,
            Options.Create(new ResponsePolicyOptions { DefaultThreshold = 0.2f }),
            NullLogger<SetResponseThresholdTool>.Instance);

    private static SessionDescriptor Session(
        string localKey = "general", string group = "g1", SessionKind kind = SessionKind.Text) =>
        new()
        {
            Id          = new SessionId(new SurfaceId("discord.main"), kind, localKey),
            DisplayName = $"#{localKey}",
            MemoryGroup = group
        };

    private static ToolContext Context(SessionDescriptor? session) => new()
    {
        Services   = new ServiceCollection().BuildServiceProvider(),
        Session    = session?.Id,
        Descriptor = session
    };

    private static ValueTask<ToolResult> Invoke(ITool tool, object args, ToolContext context) =>
        tool.InvokeAsync(new ToolInvocation("c1", JsonSerializer.SerializeToElement(args), context), default);

    [Fact]
    public async Task A_threshold_is_kept_for_the_conversation_it_was_set_in()
    {
        SessionThresholds thresholds = Thresholds();

        Assert.False((await Invoke(Tool(thresholds), new { threshold = 0.6f }, Context(Session()))).IsError);

        Assert.Equal(0.6f, thresholds.For(Session()));
        Assert.Null(thresholds.For(Session("offtopic", "g2")));
    }

    [Fact]
    public async Task Leaving_it_out_resets_to_the_default()
    {
        SessionThresholds thresholds = Thresholds();
        SetResponseThresholdTool tool = Tool(thresholds);

        await Invoke(tool, new { threshold = 0.6f }, Context(Session()));
        await Invoke(tool, new { }, Context(Session()));

        Assert.Null(thresholds.For(Session()));
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public async Task Values_outside_zero_to_one_are_refused(float threshold)
    {
        SessionThresholds thresholds = Thresholds();

        ToolResult result = await Invoke(Tool(thresholds), new { threshold }, Context(Session()));

        Assert.True(result.IsError);
        Assert.Null(thresholds.For(Session()));
    }

    [Fact]
    public async Task A_threshold_survives_a_restart()
    {
        await Invoke(Tool(Thresholds()), new { threshold = 0.45f }, Context(Session()));

        SessionThresholds reloaded = Thresholds();
        await reloaded.StartAsync(default);

        Assert.Equal(0.45f, reloaded.For(Session()));
    }

    [Fact]
    public async Task With_nothing_durable_behind_it_the_tool_refuses()
    {
        ToolResult result = await Invoke(Tool(NullSessionThresholds.Instance),
            new { threshold = 0.5f }, Context(Session()));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task There_is_nothing_to_set_without_a_conversation()
    {
        ToolResult result = await Invoke(Tool(Thresholds()), new { threshold = 0.5f }, Context(null));

        Assert.True(result.IsError);
    }
}
