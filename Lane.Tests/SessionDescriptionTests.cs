using System.Text.Json;
using Lane.Core.Identity;
using Lane.Core.Models;
using Lane.Core.Pipeline.Stages;
using Lane.Core.Prompts;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Memory.Sqlite;
using Lane.Testing;
using Lane.Tools.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// What Lane has written down about a conversation. Unlike a note on her scratchpad, this
/// is carried in the prompt of every turn in that conversation — which is the whole point,
/// and also why it is capped, cleaned, and kept to one per conversation.
/// </summary>
public sealed class SessionDescriptionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly LaneDatabase _database = new(
        new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance);

    private SessionDescriptions Descriptions() =>
        new(new SqliteKeyValueStore(_database), NullLogger<SessionDescriptions>.Instance);

    private static SetSessionDescriptionTool Tool(ISessionDescriptions descriptions) =>
        new(descriptions, NullLogger<SetSessionDescriptionTool>.Instance);

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

    // ---- writing one down --------------------------------------------------

    [Fact]
    public async Task A_description_is_kept_for_the_conversation_it_was_written_in()
    {
        SessionDescriptions descriptions = Descriptions();

        Assert.False((await Invoke(Tool(descriptions),
            new { description = "The D&D game. I am running it." }, Context(Session()))).IsError);

        Assert.Equal("The D&D game. I am running it.", descriptions.For(Session()));

        // Another conversation is another description, and this one has none.
        Assert.Null(descriptions.For(Session("offtopic", "g2")));
    }

    [Fact]
    public async Task Writing_a_second_one_replaces_the_first()
    {
        SessionDescriptions descriptions = Descriptions();
        SetSessionDescriptionTool tool = Tool(descriptions);

        await Invoke(tool, new { description = "the D&D game" }, Context(Session()));

        ToolResult result = await Invoke(tool, new { description = "the book club now" }, Context(Session()));

        Assert.False(result.IsError);
        Assert.Contains("Replaced", result.Text);
        Assert.Equal("the book club now", descriptions.For(Session()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Asking_for_nothing_erases_it(string description)
    {
        // Blank is a request, not a mistake: spaces are what a model sends when it means to
        // send nothing at all.
        SessionDescriptions descriptions = Descriptions();
        SetSessionDescriptionTool tool = Tool(descriptions);

        await Invoke(tool, new { description = "the D&D game" }, Context(Session()));
        await Invoke(tool, new { description }, Context(Session()));

        Assert.Null(descriptions.For(Session()));
    }

    [Fact]
    public async Task A_description_survives_a_restart()
    {
        await Invoke(Tool(Descriptions()), new { description = "everyone here speaks German" },
            Context(Session()));

        SessionDescriptions reloaded = Descriptions();
        await reloaded.StartAsync(default);

        Assert.Equal("everyone here speaks German", reloaded.For(Session()));
    }

    [Fact]
    public async Task Sessions_sharing_a_memory_group_share_the_description()
    {
        // The text channel and the voice channel beside it are two sessions and one
        // conversation, which is the rule session-scoped memory already follows.
        SessionDescriptions descriptions = Descriptions();

        await Invoke(Tool(descriptions), new { description = "the D&D game" }, Context(Session()));

        Assert.Equal("the D&D game", descriptions.For(Session("general-voice", "g1", SessionKind.Voice)));
    }

    // ---- what it refuses ---------------------------------------------------

    [Fact]
    public async Task An_overlong_description_is_refused_rather_than_truncated()
    {
        // Truncation would leave her reading half a sentence back every turn and acting on
        // it as though it were whole.
        SessionDescriptions descriptions = Descriptions();

        ToolResult result = await Invoke(Tool(descriptions),
            new { description = new string('x', 501) }, Context(Session()));

        Assert.True(result.IsError);
        Assert.Null(descriptions.For(Session()));
    }

    [Fact]
    public async Task Text_that_cleans_away_to_nothing_is_refused_rather_than_erasing()
    {
        // Distinct from asking for nothing: one is a request to erase, the other is text
        // there is no way to write down.
        SessionDescriptions descriptions = Descriptions();
        SetSessionDescriptionTool tool = Tool(descriptions);

        await Invoke(tool, new { description = "the D&D game" }, Context(Session()));

        ToolResult result = await Invoke(tool, new { description = "​​" }, Context(Session()));

        Assert.True(result.IsError);
        Assert.Equal("the D&D game", descriptions.For(Session()));
    }

    [Fact]
    public async Task Control_and_format_characters_come_out()
    {
        SessionDescriptions descriptions = Descriptions();

        await Invoke(Tool(descriptions),
            new { description = "the D&D‮ game\r\n\r\n\r\n\r\nI run it\ttwice" },
            Context(Session()));

        // Newlines stay — this is a paragraph, not a name — but a run of blank lines that
        // could push text far enough down to read as its own section does not.
        Assert.Equal("the D&D game\n\nI run it twice", descriptions.For(Session()));
    }

    [Fact]
    public async Task With_nothing_durable_behind_it_the_tool_refuses_rather_than_pretending()
    {
        // A description that vanishes at the next restart is worse than one never offered:
        // she would answer as though it still stood.
        ToolResult result = await Invoke(Tool(NullSessionDescriptions.Instance),
            new { description = "the D&D game" }, Context(Session()));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task There_is_nothing_to_describe_without_a_conversation()
    {
        ToolResult result = await Invoke(Tool(Descriptions()),
            new { description = "the D&D game" }, Context(null));

        Assert.True(result.IsError);
    }

    [Fact]
    public void The_tool_is_not_offered_where_there_is_no_session()
    {
        // Structural, but it is the property the availability exists for: the monologue can
        // see every session and must not be able to rewrite what any of them are.
        ToolAvailability availability = Tool(Descriptions()).Descriptor.Availability;

        Assert.True(availability.RequiresSession);
        Assert.False(availability.AllowedTurns.HasFlag(TurnKind.Monologue));
    }

    // ---- and where it ends up ----------------------------------------------

    [Fact]
    public async Task What_she_wrote_is_in_her_prompt_every_time_she_speaks_there()
    {
        SessionDescriptions descriptions = Descriptions();

        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Echoing("ok"),
            services => services.AddSingleton<ISessionDescriptions>(descriptions));

        RecordingChannel described = harness.OpenSession("discord.main", "general", memoryGroup: "g1");
        RecordingChannel other     = harness.OpenSession("discord.main", "offtopic", memoryGroup: "g2");

        await Invoke(Tool(descriptions), new { description = "everyone here speaks German" },
            Context(harness.Sessions.Active.First(s => s.Descriptor.MemoryGroup == "g1").Descriptor));

        await harness.SendAsync(described, "alice", "hallo");
        await described.WaitForAsync(1, Timeout);

        Assert.Contains("everyone here speaks German", System(harness.Model.Requests[^1]));

        // Only there: a description is the one thing about a conversation that must not
        // follow her into the next one.
        await harness.SendAsync(other, "bob", "hello");
        await other.WaitForAsync(1, Timeout);

        Assert.DoesNotContain("everyone here speaks German", System(harness.Model.Requests[^1]));
    }

    [Fact]
    public async Task The_gate_that_decides_whether_she_replies_at_all_sees_it_too()
    {
        // The one part of the turn that could not see any of this was the part deciding
        // whether the rest of it happens.
        SessionDescriptions descriptions = Descriptions();

        ScriptedLanguageModel model = new((request, _) =>
            request.ToolChoice.Mode == ToolChoiceMode.Specific
                ? ScriptedLanguageModel.ToolCall("assess", new { enthusiasm = 0.9f, emoticon = ":3" })
                : ScriptedLanguageModel.Text("ja"));

        await using LaneHarness harness = LaneHarness.Create(model, services =>
        {
            services.AddSingleton<ISessionDescriptions>(descriptions);
            services.Configure<ResponsePolicyOptions>(o =>
            {
                o.Enabled              = true;
                o.SkipInDirectSessions = false;
            });
        });

        RecordingChannel channel = harness.OpenSession("discord.main", "general", memoryGroup: "g1");

        await Invoke(Tool(descriptions), new { description = "everyone here speaks German" },
            Context(harness.Sessions.Active.First().Descriptor));

        await harness.SendAsync(channel, "alice", "hallo");
        await channel.WaitForAsync(1, Timeout);

        ModelRequest classifier = harness.Model.Requests.First(r => r.ToolChoice.Mode == ToolChoiceMode.Specific);

        Assert.Contains("everyone here speaks German", System(classifier));

        // Written about her rather than in her voice: this prompt talks about Lane to a
        // small classifier, and a note in the first person would read as instructions to it.
        Assert.Contains("Lane has written this down", System(classifier));
    }

    [Fact]
    public void The_shipped_routing_template_has_somewhere_to_put_it()
    {
        // Templates are filled by name and throw on a slot with no value, so the failure
        // this guards against is the other direction: a template that quietly stopped
        // carrying the description, which nothing else here would notice.
        FilePromptLibrary library = new(new PromptOptions(), NullLogger<FilePromptLibrary>.Instance);

        Assert.True(library.Has("Routing"));

        string rendered = library.Render("Routing",
            ("session", "#general"),
            ("description", "\n\nLane has written this down about #general, for herself:\n\neveryone here speaks German"),
            ("transcript", "alice: hallo"));

        Assert.Contains("everyone here speaks German", rendered);
    }

    private static string System(ModelRequest request) =>
        string.Join("\n", request.System.Select(block => block.Text));
}
