using System.Text.Json;
using System.Text.RegularExpressions;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Memory.Sqlite;
using Lane.Tools.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Attaching a voice to a person is the one place a measurement could turn into an identity,
/// so it is <em>proved</em> — Lane hands a phrase to somebody on an account she already knows,
/// and the same person says it out loud — rather than inferred from a voiceprint being close
/// enough. These tests are mostly about what the tool refuses.
/// </summary>
public sealed class VoiceClaimTests
{
    private static readonly SurfaceId Discord = new("discord.main");
    private static readonly SurfaceId Room    = new("api.local");

    private readonly LaneDatabase _database = new(
        new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<VoiceprintDirectory> VoicesAsync()
    {
        VoiceprintDirectory voices = new(
            new SqliteKeyValueStore(_database), TimeProvider.System,
            NullLogger<VoiceprintDirectory>.Instance);

        await voices.StartAsync(Ct);

        return voices;
    }

    private IdentityDirectory Identities() =>
        new(new SqliteKeyValueStore(_database), NullLogger<IdentityDirectory>.Instance);

    private static ClaimVoiceTool Tool(
        IVoiceprintDirectory voices, IIdentityDirectory identities, bool proof = true) =>
        new(voices, identities, new IdentityToolOptions { RequireProof = proof },
            TimeProvider.System, NullLogger<ClaimVoiceTool>.Instance);

    private static float[] Voice(int axis)
    {
        float[] v = new float[16];
        v[axis] = 1f;
        return v;
    }

    /// <summary>Somebody with an ordinary account, on a surface Lane already knows them on.</summary>
    private static Participant Account(string local, string name) =>
        new(new ParticipantId(Discord, local), name);

    /// <summary>A voice in a room, as the attributor would have resolved it.</summary>
    private static Participant Speaking(string voiceprintId, string name = "Voice 1") =>
        new(VoiceAccounts.For(Room, voiceprintId), name);

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

    private static string PhraseIn(ToolResult result)
    {
        string text = TextOf(result);

        Match match = Regex.Match(text, @"you: ([a-z]+ [a-z]+ [a-z]+)\.");

        Assert.True(match.Success, $"No phrase in: {text}");

        return match.Groups[1].Value;
    }

    private static string TextOf(ToolResult result) =>
        string.Join(" ", result.Content.OfType<TextPart>().Select(p => p.Text));

    // ---- taking people at their word ---------------------------------------

    [Fact]
    public async Task With_proof_off_a_voice_is_taken_at_its_word()
    {
        // The trust-based setting. The phrase goes; what stays is that the issuing half must
        // come from an account Lane already knows and the claiming half can only ever be the
        // voice actually talking — so this is still two acts on two channels rather than the
        // model asserting who somebody is.
        VoiceprintDirectory voices = await VoicesAsync();

        string id = await voices.EnrolAsync(Voice(3), Ct);

        ClaimVoiceTool tool = Tool(voices, Identities(), proof: false);

        ToolResult issued = await Invoke(tool, new { }, Context(Account("2313", "Jahan")));

        Assert.False(issued.IsError);
        Assert.DoesNotMatch("you: [a-z]+ [a-z]+ [a-z]+", TextOf(issued));

        ToolResult claimed = await Invoke(tool, new { }, Context(Speaking(id)));

        Assert.False(claimed.IsError, TextOf(claimed));
        Assert.Equal("discord.main:2313", voices.Get(id)!.BoundTo);
    }

    [Fact]
    public async Task With_proof_off_two_people_waiting_at_once_are_refused_rather_than_guessed()
    {
        // The one thing trusting people does not make acceptable. Two people have asked, a
        // voice speaks, and nothing distinguishes them — binding to whichever was staged
        // first would attach one person's voice to the other's memory, permanently.
        VoiceprintDirectory voices = await VoicesAsync();

        string id = await voices.EnrolAsync(Voice(3), Ct);

        ClaimVoiceTool tool = Tool(voices, Identities(), proof: false);

        await Invoke(tool, new { }, Context(Account("2313", "Jahan")));
        await Invoke(tool, new { }, Context(Account("9999", "Someone Else")));

        ToolResult claimed = await Invoke(tool, new { }, Context(Speaking(id)));

        Assert.True(claimed.IsError);
        Assert.Null(voices.Get(id)!.BoundTo);
    }

    [Fact]
    public async Task With_proof_off_a_voice_nobody_is_waiting_for_binds_nothing()
    {
        // Being at the microphone is not on its own a claim to be anybody. Without somebody
        // having asked from an account, there is no person to bind to.
        VoiceprintDirectory voices = await VoicesAsync();

        string id = await voices.EnrolAsync(Voice(3), Ct);

        ToolResult claimed = await Invoke(
            Tool(voices, Identities(), proof: false), new { }, Context(Speaking(id)));

        Assert.True(claimed.IsError);
        Assert.Null(voices.Get(id)!.BoundTo);
    }

    [Fact]
    public async Task With_proof_off_a_phrase_already_issued_still_works()
    {
        // The setting can change under a phrase somebody is holding, and having it quietly
        // stop working would be a puzzle nobody could diagnose.
        VoiceprintDirectory voices = await VoicesAsync();

        string id = await voices.EnrolAsync(Voice(3), Ct);

        ClaimVoiceTool issuing = Tool(voices, Identities());

        string phrase = PhraseIn(await Invoke(issuing, new { }, Context(Account("2313", "Jahan"))));

        ToolResult claimed = await Invoke(issuing, new { Phrase = phrase }, Context(Speaking(id)));

        Assert.False(claimed.IsError, TextOf(claimed));
    }

    [Fact]
    public async Task A_voice_said_the_words_out_loud_and_is_that_person_from_then_on()
    {
        // The happy path, and the shape of the proof: the identity comes from the account
        // the phrase was issued to, and the voice comes from whoever actually spoke it.
        VoiceprintDirectory voices = await VoicesAsync();

        string id = await voices.EnrolAsync(Voice(3), Ct);

        ClaimVoiceTool tool = Tool(voices, Identities());

        ToolResult issued = await Invoke(tool, new { }, Context(Account("2313", "Jahan")));

        ToolResult claimed = await Invoke(
            tool, new { Phrase = PhraseIn(issued) }, Context(Speaking(id)));

        Assert.False(claimed.IsError);
        Assert.Equal("discord.main:2313", voices.Get(id)!.BoundTo);
    }

    [Fact]
    public async Task A_phrase_typed_rather_than_spoken_binds_nothing()
    {
        // The whole safeguard. If the claiming half accepted a phrase from a text account it
        // would be binding a voice nobody in the exchange had actually used.
        VoiceprintDirectory voices = await VoicesAsync();

        string id = await voices.EnrolAsync(Voice(3), Ct);

        ClaimVoiceTool tool = Tool(voices, Identities());

        ToolResult issued = await Invoke(tool, new { }, Context(Account("2313", "Jahan")));

        ToolResult claimed = await Invoke(
            tool, new { Phrase = PhraseIn(issued) }, Context(Account("9999", "Someone Else")));

        Assert.True(claimed.IsError);
        Assert.Null(voices.Get(id)!.BoundTo);
    }

    [Fact]
    public async Task A_phrase_asked_for_in_a_channel_is_refused()
    {
        // Anyone reading the channel could walk over and say it into the microphone, and
        // the voice bound would be theirs.
        VoiceprintDirectory voices = await VoicesAsync();

        ToolResult result = await Invoke(
            Tool(voices, Identities()), new { }, Context(Account("2313", "Jahan"), direct: false));

        Assert.True(result.IsError);
        Assert.Contains("one to one", TextOf(result));
    }

    [Fact]
    public async Task A_voice_cannot_ask_for_its_own_phrase()
    {
        // Circular: the phrase exists to carry an identity from somewhere it is established
        // to somewhere it is not.
        VoiceprintDirectory voices = await VoicesAsync();

        string id = await voices.EnrolAsync(Voice(3), Ct);

        ToolResult result = await Invoke(Tool(voices, Identities()), new { }, Context(Speaking(id)));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_phrase_is_single_use_even_when_the_first_attempt_failed()
    {
        VoiceprintDirectory voices = await VoicesAsync();

        string first  = await voices.EnrolAsync(Voice(3), Ct);
        string second = await voices.EnrolAsync(Voice(9), Ct);

        ClaimVoiceTool tool = Tool(voices, Identities());

        string phrase = PhraseIn(await Invoke(tool, new { }, Context(Account("2313", "Jahan"))));

        // Fails because it was typed, not spoken — but the phrase is spent regardless.
        await Invoke(tool, new { Phrase = phrase }, Context(Account("9999", "Someone Else")));

        ToolResult retried = await Invoke(tool, new { Phrase = phrase }, Context(Speaking(first)));

        Assert.True(retried.IsError);
        Assert.Null(voices.Get(first)!.BoundTo);
        Assert.Null(voices.Get(second)!.BoundTo);
    }

    [Fact]
    public async Task A_voice_that_is_already_somebody_else_is_not_quietly_reassigned()
    {
        // Whatever has been said under that voice is already filed under the first person,
        // and nothing here could unpick which half belonged to whom.
        VoiceprintDirectory voices = await VoicesAsync();

        string id = await voices.EnrolAsync(Voice(3), Ct);

        await voices.BindAsync(id, "discord.main:1111", Ct);

        ClaimVoiceTool tool = Tool(voices, Identities());

        string phrase = PhraseIn(await Invoke(tool, new { }, Context(Account("2313", "Jahan"))));

        ToolResult result = await Invoke(tool, new { Phrase = phrase }, Context(Speaking(id)));

        Assert.True(result.IsError);
        Assert.Equal("discord.main:1111", voices.Get(id)!.BoundTo);
    }

    [Fact]
    public async Task Claiming_a_voice_that_is_already_yours_says_so_rather_than_failing()
    {
        VoiceprintDirectory voices = await VoicesAsync();

        string id = await voices.EnrolAsync(Voice(3), Ct);

        await voices.BindAsync(id, "discord.main:2313", Ct);

        ClaimVoiceTool tool = Tool(voices, Identities());

        string phrase = PhraseIn(await Invoke(tool, new { }, Context(Account("2313", "Jahan"))));

        ToolResult result = await Invoke(tool, new { Phrase = phrase }, Context(Speaking(id)));

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task A_name_chosen_while_a_stranger_follows_them_onto_the_account()
    {
        // "Call me Jax" said into a microphone was stored against the voice. Being
        // recognised should not quietly undo it.
        VoiceprintDirectory voices = await VoicesAsync();

        IdentityDirectory identities = Identities();

        await identities.StartAsync(Ct);

        string id = await voices.EnrolAsync(Voice(3), Ct);

        await identities.SetNameAsync(VoiceAccounts.For(Room, id).ToString(), "Jax", Ct);

        ClaimVoiceTool tool = Tool(voices, identities);

        string phrase = PhraseIn(await Invoke(tool, new { }, Context(Account("2313", "Jahan"))));

        await Invoke(tool, new { Phrase = phrase }, Context(Speaking(id)));

        Assert.Equal("Jax", identities.NameFor("discord.main:2313"));
    }

    [Fact]
    public async Task Nothing_is_promised_when_nothing_would_remember_it()
    {
        // The same rule the identity tools follow. A voice learned into memory that a
        // restart erases invites somebody to claim a voice that will be a stranger tomorrow.
        ToolResult result = await Invoke(
            Tool(NullVoiceprintDirectory.Instance, NullIdentityDirectory.Instance),
            new { }, Context(Account("2313", "Jahan")));

        Assert.True(result.IsError);
        Assert.Contains("past a restart", TextOf(result));
    }

    [Fact]
    public async Task A_person_can_make_her_forget_their_voice()
    {
        // Biometric data about a person. Being able to withdraw it is the condition on
        // which keeping it is reasonable.
        VoiceprintDirectory voices = await VoicesAsync();

        string id = await voices.EnrolAsync(Voice(3), Ct);

        await voices.BindAsync(id, "discord.main:2313", Ct);

        ForgetVoiceTool tool = new(voices, NullLogger<ForgetVoiceTool>.Instance);

        // From the account the voice was claimed by, rather than from the room.
        ToolResult result = await Invoke(tool, new { }, Context(Account("2313", "Jahan")));

        Assert.False(result.IsError);
        Assert.Null(voices.Get(id));
    }

    [Fact]
    public async Task Forgetting_acts_on_the_voice_speaking_and_nobody_else()
    {
        VoiceprintDirectory voices = await VoicesAsync();

        string mine     = await voices.EnrolAsync(Voice(3), Ct);
        string somebody = await voices.EnrolAsync(Voice(9), Ct);

        ForgetVoiceTool tool = new(voices, NullLogger<ForgetVoiceTool>.Instance);

        await Invoke(tool, new { }, Context(Speaking(mine)));

        Assert.Null(voices.Get(mine));
        Assert.NotNull(voices.Get(somebody));
    }
}
