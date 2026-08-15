using System.Text.Json;
using System.Text.RegularExpressions;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Memory.Sqlite;
using Lane.Tools.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Linking two accounts is the one thing that can merge two people's memories, so the tool
/// is built so a link is <em>proved</em> — the same person repeats a code on their other
/// account — rather than asserted by a model that believes two names match.
/// </summary>
public sealed class IdentityLinkTests
{
    private static readonly SurfaceId Discord  = new("discord.main");
    private static readonly SurfaceId Terminal = new("terminal");

    private readonly LaneDatabase _database = new(
        new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance);

    private IdentityDirectory Links() =>
        new(new SqliteKeyValueStore(_database), NullLogger<IdentityDirectory>.Instance);

    private static IdentityResolver Resolver(IIdentityDirectory links, params (string Person, string[] Accounts)[] configured) =>
        new(configured.ToDictionary(c => c.Person, c => (IReadOnlyList<string>)c.Accounts), null, links);

    private static LinkIdentityTool Tool(IIdentityResolver resolver, IIdentityDirectory links, TimeProvider? time = null) =>
        new(resolver, links, time ?? TimeProvider.System, NullLogger<LinkIdentityTool>.Instance);

    private static Participant Person(SurfaceId surface, string local, string name, IIdentityResolver resolver) =>
        resolver.Resolve(new ParticipantId(surface, local), name);

    private static ToolContext Context(Participant requester, bool direct = true) => new()
    {
        Services   = new ServiceCollection().BuildServiceProvider(),
        Requester  = requester,
        Descriptor = new SessionDescriptor
        {
            Id          = new SessionId(requester.Id.Surface, SessionKind.Text, "c1"),
            DisplayName = "a conversation",
            MemoryGroup = "g1",
            IsDirect    = direct
        }
    };

    private static ValueTask<ToolResult> Invoke(ITool tool, object args, ToolContext context) =>
        tool.InvokeAsync(new ToolInvocation("c1", JsonSerializer.SerializeToElement(args), context), default);

    private static string CodeIn(string text)
    {
        Match match = Regex.Match(text, "[A-Z0-9]{4}-[A-Z0-9]{4}");

        Assert.True(match.Success, $"No code in: {text}");

        return match.Value;
    }

    // ---- the handshake -----------------------------------------------------

    [Fact]
    public async Task Two_accounts_become_one_person_once_the_code_is_repeated_on_the_other()
    {
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links);
        LinkIdentityTool  tool = Tool(resolver, links);

        Participant onDiscord  = Person(Discord, "2313", "Jahan", resolver);
        Participant onTerminal = Person(Terminal, "jahan", "jahan", resolver);

        Assert.Null(onDiscord.GlobalUserId);

        ToolResult issued = await Invoke(tool, new { }, Context(onDiscord));

        Assert.False(issued.IsError);

        ToolResult claimed = await Invoke(tool, new { code = CodeIn(issued.Text) }, Context(onTerminal));

        Assert.False(claimed.IsError);

        // The link is what makes User-scoped memory follow one person between surfaces.
        Participant linkedDiscord  = Person(Discord, "2313", "Jahan", resolver);
        Participant linkedTerminal = Person(Terminal, "jahan", "jahan", resolver);

        Assert.NotNull(linkedDiscord.GlobalUserId);
        Assert.Equal(linkedDiscord.StableKey, linkedTerminal.StableKey);
    }

    [Fact]
    public async Task A_code_only_links_whoever_repeats_it_and_never_the_account_it_came_from()
    {
        // The property that makes the tool safe: neither half names an account, so a model
        // cannot link somebody who is not in front of it, however sure it is they are one
        // person. Claiming on the issuing account would be that hole.
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links);
        LinkIdentityTool  tool = Tool(resolver, links);

        Participant onDiscord = Person(Discord, "2313", "Jahan", resolver);

        ToolResult issued = await Invoke(tool, new { }, Context(onDiscord));
        ToolResult claimed = await Invoke(tool, new { code = CodeIn(issued.Text) }, Context(onDiscord));

        Assert.True(claimed.IsError);
        Assert.Null(Person(Discord, "2313", "Jahan", resolver).GlobalUserId);
    }

    [Fact]
    public async Task A_code_works_once()
    {
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links);
        LinkIdentityTool  tool = Tool(resolver, links);

        ToolResult issued = await Invoke(tool, new { }, Context(Person(Discord, "2313", "Jahan", resolver)));
        string code = CodeIn(issued.Text);

        Assert.False((await Invoke(tool, new { code }, Context(Person(Terminal, "jahan", "jahan", resolver)))).IsError);

        // A third account cannot walk in behind the first two.
        ToolResult again = await Invoke(tool, new { code }, Context(Person(Terminal, "someone", "someone", resolver)));

        Assert.True(again.IsError);
        Assert.Null(Person(Terminal, "someone", "someone", resolver).GlobalUserId);
    }

    [Fact]
    public async Task A_code_nobody_issued_is_refused()
    {
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links);

        ToolResult result = await Invoke(Tool(resolver, links), new { code = "AAAA-2222" },
            Context(Person(Terminal, "jahan", "jahan", resolver)));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_code_expires()
    {
        // Ten minutes is long enough to pick up the other device and short enough that a
        // code left on screen is not a standing invitation.
        TestClock clock = new(DateTimeOffset.UnixEpoch);

        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links);
        LinkIdentityTool  tool = Tool(resolver, links, clock);

        ToolResult issued = await Invoke(tool, new { }, Context(Person(Discord, "2313", "Jahan", resolver)));

        clock.Advance(TimeSpan.FromMinutes(11));

        ToolResult claimed = await Invoke(tool, new { code = CodeIn(issued.Text) },
            Context(Person(Terminal, "jahan", "jahan", resolver)));

        Assert.True(claimed.IsError);
    }

    [Fact]
    public async Task A_code_is_read_back_however_it_was_retyped()
    {
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links);
        LinkIdentityTool  tool = Tool(resolver, links);

        ToolResult issued = await Invoke(tool, new { }, Context(Person(Discord, "2313", "Jahan", resolver)));

        string mangled = CodeIn(issued.Text).Replace("-", " ").ToLowerInvariant();

        Assert.False((await Invoke(tool, new { code = mangled },
            Context(Person(Terminal, "jahan", "jahan", resolver)))).IsError);
    }

    // ---- where it is allowed to happen --------------------------------------

    [Fact]
    public async Task A_code_is_never_issued_where_a_bystander_could_read_it()
    {
        // A code in a channel is a code anyone in that channel can claim on their own
        // account, which would attach a stranger to somebody else's memory.
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links);

        ToolResult result = await Invoke(Tool(resolver, links), new { },
            Context(Person(Discord, "2313", "Jahan", resolver), direct: false));

        Assert.True(result.IsError);

        // Refused means no code was handed out, not a code plus an apology.
        Assert.DoesNotMatch("[A-Z0-9]{4}-[A-Z0-9]{4}", result.Text);
    }

    [Fact]
    public void Linking_is_a_conversation_thing_rather_than_a_monologue_one()
    {
        // Nobody is there to hold the other account during the monologue, and the gating is
        // what stops it being offered while she is thinking alone.
        ToolDescriptor descriptor = Tool(IdentityResolver.Empty, NullIdentityDirectory.Instance).Descriptor;

        Assert.Equal(TurnKind.Respond, descriptor.Availability.AllowedTurns);
        Assert.True(descriptor.Availability.RequiresSession);
        Assert.Equal(ToolSafety.Mutating, descriptor.Safety);
    }

    [Fact]
    public async Task With_nothing_durable_behind_it_the_tool_refuses_rather_than_pretending()
    {
        // A link that is forgotten at the next restart splits the memory it was made to join.
        IdentityResolver resolver = Resolver(NullIdentityDirectory.Instance);

        ToolResult result = await Invoke(Tool(resolver, NullIdentityDirectory.Instance), new { },
            Context(Person(Terminal, "jahan", "jahan", resolver)));

        Assert.True(result.IsError);
    }

    // ---- what a link means afterwards ---------------------------------------

    [Fact]
    public async Task A_new_account_joins_the_person_configuration_already_names()
    {
        // The configured id is adopted rather than a new one minted, so an operator's map
        // stays the name for that person everywhere.
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links, ("jahan", ["terminal:jahan"]));
        LinkIdentityTool  tool = Tool(resolver, links);

        ToolResult issued = await Invoke(tool, new { }, Context(Person(Terminal, "jahan", "jahan", resolver)));

        await Invoke(tool, new { code = CodeIn(issued.Text) }, Context(Person(Discord, "2313", "Jahan", resolver)));

        Assert.Equal("jahan", Person(Discord, "2313", "Jahan", resolver).GlobalUserId);
    }

    [Fact]
    public async Task Two_people_who_each_already_exist_are_not_merged()
    {
        // Both have memory written under their own id by now, and nothing here could unpick
        // which half belonged to whom afterwards. That is an operator's decision.
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links,
            ("jahan",   ["terminal:jahan"]),
            ("someone", ["discord.main:999"]));

        LinkIdentityTool tool = Tool(resolver, links);

        ToolResult issued = await Invoke(tool, new { }, Context(Person(Terminal, "jahan", "jahan", resolver)));

        ToolResult claimed = await Invoke(tool, new { code = CodeIn(issued.Text) },
            Context(Person(Discord, "999", "someone", resolver)));

        Assert.True(claimed.IsError);
        Assert.Equal("someone", Person(Discord, "999", "someone", resolver).GlobalUserId);
    }

    [Fact]
    public async Task Configuration_wins_over_anything_agreed_in_conversation()
    {
        // An operator's map is not something a conversation can edit.
        IdentityDirectory links = Links();

        await links.LinkAsync("linked-1234", [new ParticipantId(Terminal, "jahan")], default);

        IdentityResolver resolver = Resolver(links, ("jahan", ["terminal:jahan"]));

        Assert.Equal("jahan", Person(Terminal, "jahan", "jahan", resolver).GlobalUserId);
    }

    [Fact]
    public async Task A_link_survives_a_restart()
    {
        // The point of linking is memory that follows someone, so a link that evaporates
        // overnight would fragment exactly what it was made to join.
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links);
        LinkIdentityTool  tool = Tool(resolver, links);

        ToolResult issued = await Invoke(tool, new { }, Context(Person(Discord, "2313", "Jahan", resolver)));

        await Invoke(tool, new { code = CodeIn(issued.Text) }, Context(Person(Terminal, "jahan", "jahan", resolver)));

        string? before = Person(Discord, "2313", "Jahan", resolver).GlobalUserId;

        IdentityDirectory reloaded = Links();
        await reloaded.StartAsync(default);

        IdentityResolver afterRestart = Resolver(reloaded);

        Assert.Equal(before, Person(Discord, "2313", "Jahan", afterRestart).GlobalUserId);
        Assert.Equal(before, Person(Terminal, "jahan", "jahan", afterRestart).GlobalUserId);
    }

    [Fact]
    public async Task A_minted_id_is_readable_and_is_nobody_else()
    {
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links);
        LinkIdentityTool  tool = Tool(resolver, links);

        ToolResult issued = await Invoke(tool, new { }, Context(Person(Discord, "2313", "Jahan", resolver)));

        await Invoke(tool, new { code = CodeIn(issued.Text) },
            Context(Person(Terminal, "jahan", "Jahan 🐙", resolver)));

        string? minted = Person(Discord, "2313", "Jahan", resolver).GlobalUserId;

        Assert.StartsWith("jahan-", minted);
        Assert.True(resolver.IsPerson(minted!));
    }

    [Fact]
    public async Task User_scoped_memory_converges_on_the_two_surfaces_after_the_link()
    {
        // The consequence the tool exists for, at the seam where it happens: User-scoped
        // handlers key on StableKey, so the profile Lane keeps of someone on Discord and
        // the one she keeps of them in the terminal become one instance, not two.
        IdentityDirectory links = Links();
        IdentityResolver  resolver = Resolver(links);
        LinkIdentityTool  tool = Tool(resolver, links);

        static ScopeKey KeyFor(Participant person) =>
            ScopeKeys.Derive(MemoryScope.User, new MemoryContext { Focus = person });

        Assert.NotEqual(
            KeyFor(Person(Discord, "2313", "Jahan", resolver)),
            KeyFor(Person(Terminal, "jahan", "jahan", resolver)));

        ToolResult issued = await Invoke(tool, new { }, Context(Person(Discord, "2313", "Jahan", resolver)));

        await Invoke(tool, new { code = CodeIn(issued.Text) }, Context(Person(Terminal, "jahan", "jahan", resolver)));

        Assert.Equal(
            KeyFor(Person(Discord, "2313", "Jahan", resolver)),
            KeyFor(Person(Terminal, "jahan", "jahan", resolver)));
    }

    [Fact]
    public void An_unlinked_account_is_still_nobody_in_particular()
    {
        IdentityResolver resolver = Resolver(Links());

        Assert.Null(Person(Discord, "999", "Jahan", resolver).GlobalUserId);
        Assert.False(resolver.IsPerson("jahan"));
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
