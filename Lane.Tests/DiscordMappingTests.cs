using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Surfaces.Discord;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The text and identity work the Discord surface does before anything reaches the kernel.
/// Kept testable without a gateway, because these are the parts that fail quietly.
/// </summary>
public sealed class DiscordMappingTests
{
    private static readonly SurfaceId Main = new("discord.main");

    // ---- session mapping ---------------------------------------------------

    [Fact]
    public void A_guild_channel_becomes_a_named_session_in_its_own_memory_group()
    {
        SessionDescriptor descriptor = DiscordMapper.Describe(
            Main, channelId: 222, guildId: 111, "general", "{surface}/{guild}/{channel}", []);

        Assert.Equal("discord.main/Text/222", descriptor.Id.Value);
        Assert.Equal("#general", descriptor.DisplayName);
        Assert.Equal("discord.main/111/222", descriptor.MemoryGroup);
        Assert.False(descriptor.IsDirect);
    }

    [Fact]
    public void A_direct_message_is_marked_direct_so_private_memory_may_surface_there()
    {
        SessionDescriptor descriptor = DiscordMapper.Describe(
            Main, channelId: 999, guildId: null, "alice", "{surface}/{guild}/{channel}", []);

        Assert.True(descriptor.IsDirect);
        Assert.Equal("DM: alice", descriptor.DisplayName);
        Assert.Equal("discord.main/dm/999", descriptor.MemoryGroup);
    }

    [Fact]
    public void Two_instances_of_the_surface_never_share_a_session_or_a_memory_group()
    {
        // The whole point of instance ids: two bots in the same channel are two Lanes,
        // not one with a split brain.
        SessionDescriptor first = DiscordMapper.Describe(
            Main, 222, 111, "general", "{surface}/{guild}/{channel}", []);

        SessionDescriptor second = DiscordMapper.Describe(
            new SurfaceId("discord.alt"), 222, 111, "general", "{surface}/{guild}/{channel}", []);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.MemoryGroup, second.MemoryGroup);
    }

    [Fact]
    public void A_template_can_deliberately_group_channels_together()
    {
        // Point two channels at one group and Lane carries what was said between them.
        string a = DiscordMapper.BuildMemoryGroup("{surface}/{guild}", "discord.main", 111, 222);
        string b = DiscordMapper.BuildMemoryGroup("{surface}/{guild}", "discord.main", 111, 333);

        Assert.Equal(a, b);
    }

    // ---- mentions ----------------------------------------------------------

    [Fact]
    public void User_mentions_become_readable_names()
    {
        // Left raw, the model sees <@2313…> and cannot tell it was addressed, or by whom.
        string resolved = DiscordMapper.ResolveMentions(
            "hey <@111> and <@!222>, look at this",
            new Dictionary<ulong, string> { [111] = "alice", [222] = "bob" },
            new Dictionary<ulong, string>());

        Assert.Equal("hey @alice and @bob, look at this", resolved);
    }

    [Fact]
    public void Role_mentions_resolve_too()
    {
        string resolved = DiscordMapper.ResolveMentions(
            "<@&555> heads up",
            new Dictionary<ulong, string>(),
            new Dictionary<ulong, string> { [555] = "moderators" });

        Assert.Equal("@moderators heads up", resolved);
    }

    [Fact]
    public void An_unknown_mention_is_left_alone_rather_than_mangled()
    {
        string resolved = DiscordMapper.ResolveMentions(
            "who is <@999>?", new Dictionary<ulong, string>(), new Dictionary<ulong, string>());

        Assert.Equal("who is <@999>?", resolved);
    }

    // ---- replies -----------------------------------------------------------

    [Fact]
    public void Replying_to_a_message_carries_what_was_replied_to()
    {
        string content = DiscordMapper.AppendReplyContext("this", "alice", "the original thing");

        Assert.Contains("this", content);
        Assert.Contains("replying to alice", content);
        Assert.Contains("the original thing", content);
    }

    [Fact]
    public void A_long_quoted_message_is_trimmed()
    {
        string content = DiscordMapper.AppendReplyContext("ok", "alice", new string('x', 900));

        Assert.True(content.Length < 500);
        Assert.Contains("…", content);
    }

    [Fact]
    public void A_message_that_is_not_a_reply_is_untouched()
    {
        Assert.Equal("plain", DiscordMapper.AppendReplyContext("plain", null, null));
        Assert.Equal("plain", DiscordMapper.AppendReplyContext("plain", "alice", null));
    }

    // ---- splitting ---------------------------------------------------------

    [Fact]
    public void A_short_reply_is_sent_as_one_message()
    {
        Assert.Equal(["hello"], DiscordMapper.Split("hello"));
        Assert.Empty(DiscordMapper.Split(""));
    }

    [Fact]
    public void A_reply_over_the_limit_is_split_rather_than_rejected()
    {
        // Discord refuses anything past 2000 characters, which would lose the whole turn.
        string text = string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"Paragraph {i} " + new string('x', 60)));

        IReadOnlyList<string> parts = DiscordMapper.Split(text);

        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Length <= DiscordMapper.MessageLimit, $"part was {p.Length}"));

        // Nothing is lost in the seams.
        string rejoined = string.Concat(parts.Select(p => p.Replace("\n", "").Replace(" ", "")));
        string original = text.Replace("\n", "").Replace(" ", "");
        Assert.Equal(original, rejoined);
    }

    [Fact]
    public void Splitting_prefers_paragraph_boundaries()
    {
        string first  = new('a', 1500);
        string second = new('b', 900);

        IReadOnlyList<string> parts = DiscordMapper.Split($"{first}\n\n{second}");

        Assert.Equal(2, parts.Count);
        Assert.Equal(first, parts[0]);
        Assert.Equal(second, parts[1]);
    }

    [Fact]
    public void An_unbroken_run_longer_than_the_limit_is_still_cut()
    {
        // A pasted URL or a wall of base64 has no boundary to prefer.
        IReadOnlyList<string> parts = DiscordMapper.Split(new string('z', 5000));

        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.True(p.Length <= DiscordMapper.MessageLimit));
    }

    // ---- names -------------------------------------------------------------

    [Theory]
    [InlineData("Jahan", "jahan_r", "Jahan")]
    [InlineData(null, "jahan_r", "jahan_r")]
    [InlineData("", "jahan_r", "jahan_r")]
    [InlineData("   ", "jahan_r", "jahan_r")]
    public void People_are_known_by_their_chosen_name_where_they_have_one(
        string? globalName, string username, string expected) =>
        Assert.Equal(expected, DiscordMapper.DisplayNameOf(globalName, username));

    // ---- channel filtering -------------------------------------------------

    [Fact]
    public void An_empty_include_list_means_every_channel()
    {
        ChannelFilterOptions filter = new();

        Assert.True(filter.Allows(1));
        Assert.True(filter.Allows(2));
    }

    [Fact]
    public void An_include_list_confines_lane_to_those_channels()
    {
        // v2 expressed this as a global "exclusive to channel" flag; here it is one
        // instance's own filter, so a second bot can watch somewhere else entirely.
        ChannelFilterOptions filter = new() { Include = [111] };

        Assert.True(filter.Allows(111));
        Assert.False(filter.Allows(222));
    }

    [Fact]
    public void Exclusions_win_over_inclusions()
    {
        ChannelFilterOptions filter = new() { Include = [111, 222], Exclude = [222] };

        Assert.True(filter.Allows(111));
        Assert.False(filter.Allows(222));
    }
}

public sealed class IdentityResolverTests
{
    [Fact]
    public void Linked_accounts_resolve_to_one_person()
    {
        // This is what makes User-scoped memory follow someone between surfaces.
        IdentityResolver resolver = new(new Dictionary<string, IReadOnlyList<string>>
        {
            ["jahan"] = ["discord.main:2313", "terminal:jahan"]
        });

        Participant onDiscord = resolver.Resolve(new ParticipantId(new SurfaceId("discord.main"), "2313"), "Jahan");
        Participant onTerminal = resolver.Resolve(new ParticipantId(new SurfaceId("terminal"), "jahan"), "jahan");

        Assert.Equal("jahan", onDiscord.GlobalUserId);
        Assert.Equal(onDiscord.StableKey, onTerminal.StableKey);
    }

    [Fact]
    public void An_unlisted_account_stays_its_own_person()
    {
        // Never guessed from a matching display name: merging two strangers' memories is a
        // worse failure than not linking them at all.
        IdentityResolver resolver = new(new Dictionary<string, IReadOnlyList<string>>
        {
            ["jahan"] = ["terminal:jahan"]
        });

        Participant stranger = resolver.Resolve(new ParticipantId(new SurfaceId("discord.main"), "999"), "jahan");

        Assert.Null(stranger.GlobalUserId);
        Assert.Equal("discord.main:999", stranger.StableKey);
    }

    [Fact]
    public void One_account_claimed_by_two_people_is_refused_at_startup()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new IdentityResolver(new Dictionary<string, IReadOnlyList<string>>
            {
                ["jahan"] = ["discord.main:2313"],
                ["someone"] = ["discord.main:2313"]
            }));

        Assert.Contains("discord.main:2313", error.Message);
    }

    [Fact]
    public void With_no_map_configured_everyone_is_simply_themselves()
    {
        Participant person = IdentityResolver.Empty.Resolve(
            new ParticipantId(new SurfaceId("discord.main"), "1"), "alice");

        Assert.Null(person.GlobalUserId);
        Assert.Equal("alice", person.DisplayName);
    }
}
