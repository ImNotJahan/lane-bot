using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Sessions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Scope keys decide which conversations share memory. Getting one wrong either isolates
/// Lane from her own history or leaks one conversation into another, and neither failure
/// announces itself.
/// </summary>
public sealed class MemoryScopeTests
{
    private static SessionDescriptor Descriptor(
        string surface, string key, string group, bool direct = false) =>
        new()
        {
            Id          = new SessionId(new SurfaceId(surface), SessionKind.Text, key),
            DisplayName = key,
            MemoryGroup = group,
            IsDirect    = direct
        };

    [Fact]
    public void Global_scope_is_one_key_for_everything()
    {
        MemoryContext a = new() { Session = Descriptor("discord.main", "general", "g1") };
        MemoryContext b = new() { Session = Descriptor("terminal", "local", "t1") };

        Assert.Equal(ScopeKeys.Derive(MemoryScope.Global, a), ScopeKeys.Derive(MemoryScope.Global, b));
    }

    [Fact]
    public void Surface_scope_separates_instances_of_the_same_type()
    {
        // Two Discord bots must not share memory just because they are both Discord.
        ScopeKey main = ScopeKeys.Derive(MemoryScope.Surface,
            new MemoryContext { Session = Descriptor("discord.main", "general", "g1") });

        ScopeKey alt = ScopeKeys.Derive(MemoryScope.Surface,
            new MemoryContext { Session = Descriptor("discord.alt", "general", "g2") });

        Assert.NotEqual(main, alt);
    }

    [Fact]
    public void Session_scope_keys_on_the_memory_group_not_the_session_id()
    {
        // A text channel and the voice channel beside it are two sessions but one
        // conversation, and Lane should carry what was said across the boundary.
        ScopeKey text = ScopeKeys.Derive(MemoryScope.Session, new MemoryContext
        {
            Session = new SessionDescriptor
            {
                Id          = new SessionId(new SurfaceId("discord.main"), SessionKind.Text, "111"),
                DisplayName = "#lounge",
                MemoryGroup = "discord.main/guild/lounge"
            }
        });

        ScopeKey voice = ScopeKeys.Derive(MemoryScope.Session, new MemoryContext
        {
            Session = new SessionDescriptor
            {
                Id          = new SessionId(new SurfaceId("discord.main"), SessionKind.Voice, "222"),
                DisplayName = "VC: lounge",
                MemoryGroup = "discord.main/guild/lounge"
            }
        });

        Assert.Equal(text, voice);
    }

    [Fact]
    public void Different_groups_get_different_session_keys()
    {
        ScopeKey a = ScopeKeys.Derive(MemoryScope.Session,
            new MemoryContext { Session = Descriptor("discord.main", "general", "g/general") });

        ScopeKey b = ScopeKeys.Derive(MemoryScope.Session,
            new MemoryContext { Session = Descriptor("discord.main", "offtopic", "g/offtopic") });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void User_scope_follows_the_global_link_across_surfaces()
    {
        // The same person on Discord and through the API is one user's memory.
        Participant onDiscord = new(new ParticipantId(new SurfaceId("discord.main"), "2313"), "jahan", "jahan");
        Participant onApi     = new(new ParticipantId(new SurfaceId("api"), "client-7"), "jahan", "jahan");

        Assert.Equal(
            ScopeKeys.Derive(MemoryScope.User, new MemoryContext { Focus = onDiscord }),
            ScopeKeys.Derive(MemoryScope.User, new MemoryContext { Focus = onApi }));
    }

    [Fact]
    public void Unlinked_users_still_get_their_own_memory()
    {
        // Falling back to a shared "anonymous" bucket would put two strangers' memories
        // in the same place, which is worse than having none.
        Participant alice = new(new ParticipantId(new SurfaceId("discord.main"), "1"), "alice");
        Participant bob   = new(new ParticipantId(new SurfaceId("discord.main"), "2"), "bob");

        Assert.NotEqual(
            ScopeKeys.Derive(MemoryScope.User, new MemoryContext { Focus = alice }),
            ScopeKeys.Derive(MemoryScope.User, new MemoryContext { Focus = bob }));
    }

    [Fact]
    public void A_monologue_has_no_session_and_still_resolves()
    {
        Assert.Equal(new ScopeKey("global"), ScopeKeys.Derive(MemoryScope.Global, MemoryContext.Monologue));
        Assert.Equal(new ScopeKey("session:detached"), ScopeKeys.Derive(MemoryScope.Session, MemoryContext.Monologue));
    }
}
