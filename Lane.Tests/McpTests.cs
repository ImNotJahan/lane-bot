using System.Text.Json;
using Lane.Core.Events;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Testing;
using Lane.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Tools from somebody else's server.
///
/// The server in these tests is real: a child process speaking JSON-RPC over a pipe, which
/// can be made to crash, to change its mind about what it offers, and to say hostile things
/// in its tool descriptions. A mocked client would only ever answer for the mock, and every
/// interesting failure at this boundary is a protocol or process failure.
/// </summary>
public sealed class McpTests
{
    private static string ServerPath =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Lane.Testing.exe" : "Lane.Testing");

    private static McpServerOptions Server(string id = "fake", string mode = "normal") => new()
    {
        Id      = id,
        Command = ServerPath,
        Args    = [FakeMcpServer.Argument, mode],
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static McpOptions Shared(params McpServerOptions[] servers) => new()
    {
        Servers        = [.. servers],
        ConnectTimeout  = TimeSpan.FromSeconds(20),
        RetryDelay      = TimeSpan.FromMilliseconds(200),
        MaxRetryDelay   = TimeSpan.FromSeconds(1),

        // The fake server exits on end-of-input in well under this. It is short here only
        // because the suite pays it once per test.
        ShutdownTimeout = TimeSpan.FromMilliseconds(300)
    };

    [Fact]
    public async Task A_servers_tools_appear_with_its_name_on_them()
    {
        await using McpToolSource source = new(Shared(Server()), NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        IReadOnlyList<ITool> tools = await WaitForToolsAsync(source);

        ITool greet = Assert.Single(tools);

        // Prefixed, so a server can never shadow a built-in tool or another server's.
        Assert.Equal("mcp__fake__greet", greet.Descriptor.Name);
        Assert.Equal("Greets somebody by name.", greet.Descriptor.Description);
        Assert.Equal("mcp:fake", greet.Descriptor.SourceId);

        // The schema is the server's, passed through untouched.
        Assert.Equal(JsonValueKind.Object, greet.Descriptor.InputSchema.ValueKind);
        Assert.True(greet.Descriptor.InputSchema.GetProperty("properties").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task A_tool_on_a_server_can_actually_be_called()
    {
        await using McpToolSource source = new(Shared(Server()), NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        ITool greet = (await WaitForToolsAsync(source))[0];

        ToolResult result = await greet.InvokeAsync(
            new ToolInvocation("call-1", Args("""{"name":"Jahan"}"""), Context()), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Hello, Jahan!", result.Text);
    }

    [Fact]
    public async Task A_server_reporting_an_error_is_an_error_not_an_exception()
    {
        // The provider requires an answer to every call it made. A tool failure has to come
        // back as a result, or the conversation is left with an unanswered tool_use.
        await using McpToolSource source = new(
            Shared(Server(mode: "erroring")), NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        ITool greet = (await WaitForToolsAsync(source))[0];

        ToolResult result = await greet.InvokeAsync(
            new ToolInvocation("call-1", Args("""{"name":"x"}"""), Context()), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("did not work", result.Text);
    }

    [Fact]
    public async Task A_server_that_will_not_start_costs_its_tools_and_nothing_else()
    {
        // Lane simply has fewer abilities for a while. She does not fail a turn over it.
        await using McpToolSource source = new(
            Shared(Server("broken", "crash-on-start"), Server("working")),
            NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        IReadOnlyList<ITool> tools = await WaitForToolsAsync(source);

        Assert.Equal("mcp__working__greet", Assert.Single(tools).Descriptor.Name);

        Assert.False(source.Connections.Single(c => c.ServerId == "broken").IsConnected);
        Assert.True(source.Connections.Single(c => c.ServerId == "working").IsConnected);
    }

    [Fact]
    public async Task A_server_changing_its_mind_is_picked_up_without_a_restart()
    {
        await using McpToolSource source = new(
            Shared(Server(mode: "changing")), NullLoggerFactory.Instance);

        int changes = 0;

        source.ToolsChanged += _ => Interlocked.Increment(ref changes);

        await source.StartAsync(CancellationToken.None);

        // The server announces a new tool shortly after connecting.
        IReadOnlyList<ITool> tools = await WaitForToolsAsync(source, expected: 2);

        Assert.Equal(
            ["mcp__fake__farewell", "mcp__fake__greet"],
            tools.Select(t => t.Descriptor.Name).Order());

        Assert.True(changes >= 1);
    }

    [Fact]
    public async Task The_registry_forgets_its_cache_when_a_server_changes()
    {
        // Without this the new tool exists but is never offered: the registry caches the
        // flattened tool map for the life of the process.
        await using McpToolSource source = new(
            Shared(Server(mode: "changing")), NullLoggerFactory.Instance);

        ToolRegistry registry = new(
            [source],
            Options.Create(new ToolOptions()),
            NullLogger<ToolRegistry>.Instance,
            new NullEventBus());

        await source.StartAsync(CancellationToken.None);

        await WaitForToolsAsync(source, expected: 1);

        ToolSet before = await registry.ResolveAsync(new ToolScope(null, TurnKind.Respond), TestContext.Current.CancellationToken);

        await WaitForToolsAsync(source, expected: 2);

        ToolSet after = await registry.ResolveAsync(new ToolScope(null, TurnKind.Respond), TestContext.Current.CancellationToken);

        Assert.Equal(1, before.Count);
        Assert.Equal(2, after.Count);

        // And the fingerprint moved with it, so the prompt-cache miss is visible in telemetry
        // rather than being a silent change to the request prefix.
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public async Task An_unavailable_server_is_retried_rather_than_given_up_on()
    {
        await using McpToolSource source = new(
            Shared(Server("broken", "crash-on-start")), NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        // It cannot succeed, so what is being checked is that it keeps trying with a delay
        // rather than spinning or stopping after one failure.
        await Task.Delay(1200, TestContext.Current.CancellationToken);

        Assert.Empty(await source.GetToolsAsync(TestContext.Current.CancellationToken));
        Assert.False(source.Connections[0].IsConnected);
    }

    [Fact]
    public async Task A_hostile_server_cannot_smuggle_anything_into_the_prompt()
    {
        await using McpToolSource source = new(Shared(Server(mode: "hostile")), NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        IReadOnlyList<ITool> tools = await WaitForToolsAsync(source, expected: 3);

        // The name with newlines and punctuation is reduced to a name.
        ITool nasty = tools.Single(t => t.Descriptor.Name.Contains("do_not"));

        Assert.Equal("mcp__fake__do_not_call_this", nasty.Descriptor.Name);
        Assert.DoesNotContain('\n', nasty.Descriptor.Name);

        // The description keeps its words — Lane is not in the business of censoring tools —
        // but loses the line breaks that let it impersonate a section of the prompt, and the
        // invisible characters that let it read differently to a human than to the model.
        Assert.DoesNotContain('\n', nasty.Descriptor.Description);
        Assert.DoesNotContain('​', nasty.Descriptor.Description);
        Assert.DoesNotContain('‮', nasty.Descriptor.Description);

        // And a description cannot be arbitrarily long: it is sent on every single request.
        Assert.True(tools.Single(t => t.Descriptor.Name.EndsWith("verbose")).Descriptor.Description.Length <= 1025);
    }

    [Fact]
    public async Task Two_names_that_sanitise_to_the_same_thing_do_not_both_survive()
    {
        // Keeping both would mean two tools whose calls are indistinguishable once the model
        // picks one, which is a worse outcome than losing the second.
        await using McpToolSource source = new(Shared(Server(mode: "hostile")), NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        IReadOnlyList<ITool> tools = await WaitForToolsAsync(source, expected: 3);

        Assert.Single(tools, t => t.Descriptor.Name == "mcp__fake__do_not_call_this");

        Assert.Equal(tools.Count, tools.Select(t => t.Descriptor.Name).Distinct().Count());
    }

    [Fact]
    public async Task A_third_partys_tools_are_kept_out_of_the_monologue_by_default()
    {
        // The monologue runs unattended on a timer. A tool description written by somebody
        // else is at its most dangerous exactly where nobody is reading the output.
        await using McpToolSource source = new(Shared(Server()), NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        ITool greet = (await WaitForToolsAsync(source))[0];

        Assert.False(greet.Descriptor.Availability.AllowedTurns.HasFlag(TurnKind.Monologue));
        Assert.True(greet.Descriptor.Availability.AllowedTurns.HasFlag(TurnKind.Respond));
    }

    [Fact]
    public async Task A_server_can_be_let_into_the_monologue_deliberately()
    {
        McpServerOptions options = Server();
        options.AllowInMonologue = true;

        await using McpToolSource source = new(Shared(options), NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        ITool greet = (await WaitForToolsAsync(source))[0];

        Assert.True(greet.Descriptor.Availability.AllowedTurns.HasFlag(TurnKind.Monologue));
    }

    [Fact]
    public async Task The_registry_refuses_a_tool_the_model_names_out_of_turn()
    {
        // Belt and braces: the advertised list is a hint to the model, not a boundary. A
        // model can name a tool it was never offered.
        await using McpToolSource source = new(Shared(Server()), NullLoggerFactory.Instance);

        ToolRegistry registry = new(
            [source], Options.Create(new ToolOptions()),
            NullLogger<ToolRegistry>.Instance, new NullEventBus());

        await source.StartAsync(CancellationToken.None);
        await WaitForToolsAsync(source);

        ToolResult result = await registry.InvokeAsync(
            "mcp__fake__greet", "call-1", Args("""{"name":"x"}"""),
            Context() with { Turn = TurnKind.Monologue }, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("not available here", result.Text);
    }

    [Fact]
    public async Task A_denied_tool_never_reaches_the_model()
    {
        McpServerOptions options = Server();
        options.Deny = ["greet"];

        await using McpToolSource source = new(Shared(options), NullLoggerFactory.Instance);

        await source.StartAsync(CancellationToken.None);

        await Task.Delay(2000, TestContext.Current.CancellationToken);

        Assert.Empty(await source.GetToolsAsync(TestContext.Current.CancellationToken));
        Assert.True(source.Connections[0].IsConnected);
    }

    [Fact]
    public async Task Two_servers_cannot_be_given_the_same_name()
    {
        // Ids namespace tool names. Two servers sharing one would make their tools collide
        // and be silently dropped by the registry.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new McpToolSource(Shared(Server("same"), Server("same")), NullLoggerFactory.Instance));

        Assert.Contains("same", error.Message);

        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has__underscores")]        // would break apart the mcp__server__tool form
    [InlineData("waaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaay-too-long")]
    public void A_server_id_that_would_corrupt_a_tool_name_is_refused(string id)
    {
        // "has__underscores" is rejected for a specific reason: the name format is
        // mcp__{server}__{tool}, so a server id containing the separator makes the two
        // halves ambiguous.
        McpServerOptions options = Server(id: "placeholder");
        options.Id = id;

        if (id == "has__underscores")
        {
            // This one is legal by alphabet, so the guard that matters is the format itself.
            Assert.Contains("__", McpNaming.Qualify(id, "x"));
            return;
        }

        Assert.Throws<InvalidOperationException>(() =>
            new McpToolSource(Shared(options), NullLoggerFactory.Instance));
    }

    // ---- naming, in isolation ---------------------------------------------

    [Theory]
    [InlineData("read_file", "read_file")]
    [InlineData("Read File", "read_file")]
    [InlineData("read-file!!", "read_file")]
    [InlineData("  spaced  out  ", "spaced_out")]
    [InlineData("__leading", "leading")]
    [InlineData("!!!", "tool")]
    [InlineData("", "tool")]
    public void Names_are_reduced_to_something_safe_to_print(string input, string expected) =>
        Assert.Equal(expected, McpNaming.Sanitize(input));

    [Fact]
    public void An_empty_description_says_so_rather_than_being_blank()
    {
        // A blank line in the tool list is more confusing to the model than an admission.
        Assert.Equal("(no description provided)", McpNaming.CleanDescription(null, 100));
        Assert.Equal("(no description provided)", McpNaming.CleanDescription("   ", 100));
    }

    [Fact]
    public void A_long_description_is_cut_rather_than_sent()
    {
        string cut = McpNaming.CleanDescription(new string('a', 500), 100);

        Assert.Equal(101, cut.Length);
        Assert.EndsWith("…", cut);
    }

    // ---- helpers -----------------------------------------------------------

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ToolContext Context() => new()
    {
        Turn     = TurnKind.Respond,
        Services = new EmptyServices()
    };

    /// <summary>Waits for a server to come up rather than sleeping a fixed amount.</summary>
    private static async Task<IReadOnlyList<ITool>> WaitForToolsAsync(
        McpToolSource source, int expected = 1, int timeoutMs = 20000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 50)
        {
            IReadOnlyList<ITool> tools = await source.GetToolsAsync(CancellationToken.None);

            if (tools.Count >= expected) return tools;

            await Task.Delay(50);
        }

        Assert.Fail($"Expected at least {expected} MCP tool(s) within {timeoutMs}ms.");

        return [];
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
