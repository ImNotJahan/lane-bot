using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lane.Core.Credits;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Nodes;
using Lane.Memory.Sqlite;
using Lane.Node.Sdk;
using Lane.Nodes;
using Lane.Nodes.Portal;
using Lane.Nodes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class CreditLedgerTests
{
    private readonly SqliteCreditLedger _ledger = new(
        new LaneDatabase(new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance));

    [Fact]
    public async Task Earning_transferring_and_spending_move_balances_and_leave_history()
    {
        Assert.Equal(0, await _ledger.BalanceAsync("a", CancellationToken.None));

        Assert.Equal(5, await _ledger.EarnAsync("a", 5, "work", CancellationToken.None));
        Assert.Equal(3, await _ledger.TransferAsync("a", "b", 2, "thanks", CancellationToken.None));
        Assert.Equal(1, await _ledger.SpendAsync("b", 1, "something", CancellationToken.None));

        Assert.Equal(3, await _ledger.BalanceAsync("a", CancellationToken.None));
        Assert.Equal(1, await _ledger.BalanceAsync("b", CancellationToken.None));

        IReadOnlyList<CreditEntry> history = await _ledger.HistoryAsync("b", 10, CancellationToken.None);

        Assert.Collection(history,
            spent =>
            {
                Assert.Equal(CreditEntryKind.Spent, spent.Kind);
                Assert.Equal(-1, spent.Amount);
            },
            received =>
            {
                Assert.Equal(CreditEntryKind.Received, received.Kind);
                Assert.Equal(2, received.Amount);
                Assert.Equal("a", received.Counterparty);
                Assert.Equal("thanks", received.Memo);
            });
    }

    [Fact]
    public async Task Overdrawing_changes_nothing()
    {
        await _ledger.EarnAsync("a", 2, null, CancellationToken.None);

        InsufficientCreditsException ex = await Assert.ThrowsAsync<InsufficientCreditsException>(async () =>
            await _ledger.TransferAsync("a", "b", 3, null, CancellationToken.None));

        Assert.Equal(2, ex.Balance);
        await Assert.ThrowsAsync<InsufficientCreditsException>(async () =>
            await _ledger.SpendAsync("nobody", 1, null, CancellationToken.None));

        Assert.Equal(2, await _ledger.BalanceAsync("a", CancellationToken.None));
        Assert.Equal(0, await _ledger.BalanceAsync("b", CancellationToken.None));
        Assert.Single(await _ledger.HistoryAsync("a", 10, CancellationToken.None));
        Assert.Empty(await _ledger.HistoryAsync("b", 10, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_self_transfers_and_non_positive_amounts()
    {
        await _ledger.EarnAsync("a", 2, null, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _ledger.TransferAsync("a", "a", 1, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await _ledger.EarnAsync("a", 0, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await _ledger.TransferAsync("a", "b", -1, null, CancellationToken.None));
    }
}

public sealed class NodeDirectoryTests
{
    private readonly SqliteNodeDirectory _directory = new(
        new LaneDatabase(new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance));

    [Fact]
    public async Task Lists_identities_by_responses_and_remembers_node_names_and_credentials()
    {
        using FileNodeKey quiet = FileNodeKey.Generate();
        using FileNodeKey busy  = FileNodeKey.Generate();

        await _directory.SeenAsync(quiet.Identity, "quiet-box", null, CancellationToken.None);
        await _directory.SeenAsync(busy.Identity, "busy-box", "cred-1", CancellationToken.None);
        await _directory.SeenAsync(busy.Identity, null, "cred-1", CancellationToken.None);

        await _directory.RecordResponseAsync(busy.Identity, CancellationToken.None);
        await _directory.RecordResponseAsync(busy.Identity, CancellationToken.None);

        IReadOnlyList<NodeIdentityRecord> all = await _directory.ListAsync(CancellationToken.None);

        Assert.Equal([busy.Identity.KeyId, quiet.Identity.KeyId], all.Select(r => r.KeyId));
        Assert.Equal(2, all[0].Responses);
        Assert.Equal("busy-box", all[0].DisplayName);

        Assert.Equal(["cred-1"], await _directory.CredentialIdsAsync(CancellationToken.None));
        Assert.Equal(busy.Identity.KeyId, Assert.Single(await _directory.FindByCredentialAsync("cred-1", CancellationToken.None)).KeyId);
    }

    [Fact]
    public async Task Nicknames_are_unique_ignoring_case_and_can_be_cleared()
    {
        using FileNodeKey a = FileNodeKey.Generate();
        using FileNodeKey b = FileNodeKey.Generate();

        await _directory.SeenAsync(a.Identity, "a", null, CancellationToken.None);
        await _directory.SeenAsync(b.Identity, "b", null, CancellationToken.None);

        Assert.True(await _directory.SetNicknameAsync(a.Identity.KeyId, "Wizard", CancellationToken.None));
        await Assert.ThrowsAsync<NicknameTakenException>(async () =>
            await _directory.SetNicknameAsync(b.Identity.KeyId, "wizard", CancellationToken.None));

        Assert.Equal("Wizard", (await _directory.FindAsync(a.Identity.KeyId, CancellationToken.None))!.DisplayName);

        Assert.True(await _directory.SetNicknameAsync(a.Identity.KeyId, null, CancellationToken.None));
        Assert.True(await _directory.SetNicknameAsync(b.Identity.KeyId, "wizard", CancellationToken.None));

        Assert.False(await _directory.SetNicknameAsync("unknown", "x", CancellationToken.None));
    }
}

public sealed class NodePortalTests : IAsyncLifetime
{
    private sealed class FixedStatus : ILaneStatusSource
    {
        public LaneStatus Current { get; set; } = new([new SurfaceStatus("terminal", true)], "( ._.)", null);
    }

    private readonly FixedStatus         _status  = new();
    private readonly NodesOptions        _options = new() { Enabled = true, Urls = "http://127.0.0.1:0" };
    private readonly NodePool            _pool    = new();
    private readonly SqliteNodeDirectory _directory;
    private readonly SqliteCreditLedger  _ledger;
    private readonly SqliteSponsorships  _sponsorships;
    private readonly NodeBookkeeper      _bookkeeper;
    private readonly NodeListener        _listener;

    private HttpClient _http = null!;

    public NodePortalTests()
    {
        LaneDatabase database = new(new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance);

        _directory    = new SqliteNodeDirectory(database);
        _ledger       = new SqliteCreditLedger(database);
        _sponsorships = new SqliteSponsorships(database);
        _bookkeeper   = new NodeBookkeeper(_directory, _ledger, _options);
        _listener     = new NodeListener(_pool, _options, NullLoggerFactory.Instance, _bookkeeper,
            new NodePortal(_pool, _directory, _ledger, _sponsorships, new SqliteForum(database), _status, _options));
    }

    [Fact]
    public async Task Forum_posts_can_be_commented_on_and_sorted_by_bump_or_creation()
    {
        using FakeSecurityKey alice = new();
        using FakeSecurityKey bob   = new();

        await SeenAsync(alice, "alice-box");
        await SeenAsync(bob, "bob-box");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _http.PostAsJsonAsync("api/me/forum/posts", new { title = "Hi", description = "there" })).StatusCode);

        await SignInAsync(alice);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsJsonAsync("api/me/forum/posts", new { title = "  ", description = "there" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsJsonAsync("api/me/forum/posts", new { title = "Hi", description = "" })).StatusCode);

        long first  = (await ReadJsonAsync(await _http.PostAsJsonAsync("api/me/forum/posts",
            new { title = "First", description = "one\ntwo" }))).GetProperty("id").GetInt64();
        long second = (await ReadJsonAsync(await _http.PostAsJsonAsync("api/me/forum/posts",
            new { title = "Second", description = "three" }))).GetProperty("id").GetInt64();

        await SignInAsync(bob);

        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.PostAsJsonAsync($"api/me/forum/posts/{second + 100}/comments", new { body = "hello?" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await _http.PostAsJsonAsync($"api/me/forum/posts/{first}/comments", new { body = "Nice" })).StatusCode);

        _http.DefaultRequestHeaders.Authorization = null;

        Assert.Equal([first, second], (await GetJsonAsync("api/forum/posts")).EnumerateArray().Select(p => p.GetProperty("id").GetInt64()));
        Assert.Equal([second, first],
            (await GetJsonAsync("api/forum/posts?sort=newest")).EnumerateArray().Select(p => p.GetProperty("id").GetInt64()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("api/forum/posts?sort=sideways")).StatusCode);

        JsonElement thread = await GetJsonAsync($"api/forum/posts/{first}");
        Assert.Equal("one\ntwo", thread.GetProperty("post").GetProperty("description").GetString());
        Assert.Equal("alice-box", thread.GetProperty("post").GetProperty("author").GetProperty("name").GetString());
        Assert.Equal(1, thread.GetProperty("post").GetProperty("comments").GetInt32());

        JsonElement comment = Assert.Single(thread.GetProperty("comments").EnumerateArray());
        Assert.Equal("Nice", comment.GetProperty("body").GetString());
        Assert.Equal("bob-box", comment.GetProperty("author").GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"api/forum/posts/{second + 100}")).StatusCode);
    }

    [Fact]
    public async Task Operators_can_sponsor_share_limit_and_stop_sponsoring()
    {
        using FakeSecurityKey alice = new();
        using FakeSecurityKey bob   = new();

        NodeIdentity aliceIdentity = await SeenAsync(alice, "alice-box");
        NodeIdentity bobIdentity   = await SeenAsync(bob, "bob-box");

        await _ledger.EarnAsync(bobIdentity.KeyId, 3, null, CancellationToken.None);

        await SignInAsync(alice);

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("api/me/sponsorships",
            new { kind = "DiscordChannel", target = "general" })).StatusCode);

        JsonElement created = await ReadJsonAsync(await _http.PostAsJsonAsync("api/me/sponsorships",
            new { kind = "ApiClient", target = "my-app", name = "My app", dailyLimit = 5 }));

        string key = created.GetProperty("apiKey").GetString()!;
        Assert.StartsWith("lane_", key);
        Assert.Equal("My app", created.GetProperty("target").GetProperty("name").GetString());
        Assert.False(created.GetProperty("target").GetProperty("active").GetBoolean());

        await SignInAsync(bob);

        JsonElement joined = await ReadJsonAsync(await _http.PostAsJsonAsync("api/me/sponsorships",
            new { kind = "ApiClient", target = "MY-APP" }));

        Assert.False(joined.TryGetProperty("apiKey", out _));
        Assert.Equal("my-app", joined.GetProperty("target").GetProperty("target").GetString());
        Assert.Equal(2, joined.GetProperty("target").GetProperty("sponsors").GetArrayLength());
        Assert.True(joined.GetProperty("target").GetProperty("active").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent,
            (await _http.PutAsJsonAsync("api/me/sponsorships/ApiClient/my-app", new { dailyLimit = 0 })).StatusCode);

        JsonElement listed = await GetJsonAsync("api/me/sponsorships");
        Assert.False(listed.GetProperty("targets")[0].GetProperty("active").GetBoolean());

        Assert.Equal("my-app", (await _sponsorships.FindApiClientByKeyAsync(key, CancellationToken.None))!.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync("api/me/sponsorships/ApiClient/my-app")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync("api/me/sponsorships/ApiClient/my-app")).StatusCode);

        Sponsorship remaining = Assert.Single(await _sponsorships.ListAsync(CancellationToken.None));
        Assert.Equal(aliceIdentity.KeyId, remaining.Account);
    }

    public async ValueTask InitializeAsync()
    {
        await _listener.StartAsync(CancellationToken.None);

        _http = new HttpClient { BaseAddress = new Uri(_listener.Addresses[0] + "/") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _listener.StopAsync(CancellationToken.None);
        await _listener.DisposeAsync();
    }

    [Fact]
    public async Task Node_answers_earn_credits_and_its_security_key_can_then_sign_in()
    {
        using FakeSecurityKey securityKey = new();
        using WebAuthnSession session = new();
        using WebAuthnNodeKey key = securityKey.Vouch(session);

        NodeLanguageModel model = new("pool", "default", ModelCapabilities.Tools, _pool, new AcceptAllNodeValidator(), _options,
            NullLogger<NodeLanguageModel>.Instance, _bookkeeper);

        LaneNode node = new(
            new LaneNodeOptions { LaneUrl = _listener.Addresses[0], Model = "scripted", Name = "laptop" },
            key,
            (_, _) => Task.FromResult(new ModelResponse([new TextPart("ok")], StopReason.EndTurn, default)));

        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Connected += _ => connected.TrySetResult();

        using CancellationTokenSource stop = new();
        Task running = node.RunAsync(stop.Token);

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        ModelRequest request = new() { System = [], Messages = [LaneMessage.Thought(Participant.LaneInternal, "hi", DateTimeOffset.UnixEpoch)] };
        await model.CompleteAsync(request, CancellationToken.None);
        await model.CompleteAsync(request, CancellationToken.None);

        JsonElement leader = (await GetJsonAsync("api/leaderboard"))[0];
        Assert.Equal(key.Identity.KeyId, leader.GetProperty("identity").GetProperty("keyId").GetString());
        Assert.Equal("laptop", leader.GetProperty("identity").GetProperty("name").GetString());
        Assert.Equal(2, leader.GetProperty("identity").GetProperty("responses").GetInt64());
        Assert.True(leader.GetProperty("online").GetBoolean());

        Assert.Equal(1, (await GetJsonAsync("api/stats")).GetProperty("nodesOnline").GetInt32());

        await SignInAsync(securityKey);

        JsonElement wallet = await GetJsonAsync("api/me");
        Assert.Equal(2, wallet.GetProperty("balance").GetInt64());
        Assert.Equal("Earned", wallet.GetProperty("history")[0].GetProperty("kind").GetString());

        await stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task Signed_in_identities_can_rename_themselves_and_send_credits()
    {
        using FakeSecurityKey alice = new();
        using FakeSecurityKey bob   = new();

        NodeIdentity aliceIdentity = await SeenAsync(alice, "alice-box");
        NodeIdentity bobIdentity   = await SeenAsync(bob, "bob-box");

        await _ledger.EarnAsync(aliceIdentity.KeyId, 5, null, CancellationToken.None);

        await SignInAsync(bob);
        Assert.Equal(HttpStatusCode.OK, (await _http.PutAsJsonAsync("api/me/nickname", new { nickname = "  Bob " })).StatusCode);

        await SignInAsync(alice);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PutAsJsonAsync("api/me/nickname", new { nickname = "bob" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.PutAsJsonAsync("api/me/nickname", new { nickname = "Alice" })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsJsonAsync("api/me/transfers", new { to = bobIdentity.KeyId, amount = 6 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsJsonAsync("api/me/transfers", new { to = aliceIdentity.KeyId, amount = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.PostAsJsonAsync("api/me/transfers", new { to = "nobody", amount = 1 })).StatusCode);

        HttpResponseMessage sent = await _http.PostAsJsonAsync("api/me/transfers", new { to = bobIdentity.KeyId, amount = 3, memo = "lunch" });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        await SignInAsync(bob);

        JsonElement wallet = await GetJsonAsync("api/me");
        Assert.Equal("Bob", wallet.GetProperty("identity").GetProperty("name").GetString());
        Assert.Equal(3, wallet.GetProperty("balance").GetInt64());

        JsonElement received = wallet.GetProperty("history")[0];
        Assert.Equal("Received", received.GetProperty("kind").GetString());
        Assert.Equal("Alice", received.GetProperty("counterpartyName").GetString());
        Assert.Equal("lunch", received.GetProperty("memo").GetString());

        Assert.Equal(2, await _ledger.BalanceAsync(aliceIdentity.KeyId, CancellationToken.None));
    }

    [Fact]
    public async Task Sign_in_rejects_unknown_keys_and_replayed_assertions()
    {
        using FakeSecurityKey known    = new();
        using FakeSecurityKey impostor = new();

        await SeenAsync(known, "box");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("api/me")).StatusCode);

        byte[] challenge = await ChallengeAsync();
        (byte[] data, byte[] client, byte[] signature) = impostor.Assert(challenge);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAssertionAsync(known.CredentialId, data, client, signature)).StatusCode);

        (data, client, signature) = known.Assert(challenge);

        Assert.Equal(HttpStatusCode.OK, (await PostAssertionAsync(known.CredentialId, data, client, signature)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAssertionAsync(known.CredentialId, data, client, signature)).StatusCode);

        (data, client, signature) = known.Assert(await ChallengeAsync(), userPresent: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAssertionAsync(known.CredentialId, data, client, signature)).StatusCode);
    }

    [Fact]
    public async Task Identity_seen_without_a_credential_id_can_still_sign_in_and_gets_linked()
    {
        using FakeSecurityKey key = new();

        NodeIdentity identity = NodeProtocol.IdentityFor(key.PublicKey, NodeProtocol.WebAuthnEs256);
        await _directory.SeenAsync(identity, "old-node", null, CancellationToken.None);

        Assert.Empty(await _directory.CredentialIdsAsync(CancellationToken.None));

        await SignInAsync(key);

        Assert.Equal(identity.KeyId, (await GetJsonAsync("api/me")).GetProperty("identity").GetProperty("keyId").GetString());
        Assert.Equal([Base64Url.EncodeToString(key.CredentialId)], await _directory.CredentialIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Key_pair_identities_sign_in_by_signing_the_challenge()
    {
        using FileNodeKey key      = FileNodeKey.Generate();
        using FileNodeKey stranger = FileNodeKey.Generate();

        await _directory.SeenAsync(key.Identity, "box", null, CancellationToken.None);

        byte[] challenge = await ChallengeAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostKeySignInAsync(stranger, challenge, stranger.SignSignIn(challenge))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostKeySignInAsync(key, challenge, stranger.SignSignIn(challenge))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostKeySignInAsync(key, challenge, key.Sign(challenge))).StatusCode);

        HttpResponseMessage response = await PostKeySignInAsync(key, challenge, key.SignSignIn(challenge));
        string token = (await ReadJsonAsync(response)).GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostKeySignInAsync(key, challenge, key.SignSignIn(challenge))).StatusCode);

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(key.Identity.KeyId, (await GetJsonAsync("api/me")).GetProperty("identity").GetProperty("keyId").GetString());
    }

    [Fact]
    public async Task Stats_report_surfaces_face_and_energy()
    {
        _status.Current = new LaneStatus(
            [new SurfaceStatus("terminal", true), new SurfaceStatus("discord", false)],
            "(^_^)",
            new EnergyStatus(0.5, "Tired", false, 10, 20),
            ApiClients: 2,
            DiscordChannels: 3);

        JsonElement stats = await GetJsonAsync("api/stats");

        Assert.Equal(2, stats.GetProperty("apiClients").GetInt32());
        Assert.Equal(3, stats.GetProperty("discordChannels").GetInt32());

        Assert.Equal(2, stats.GetProperty("surfaces").GetArrayLength());
        Assert.Equal("(^_^)", stats.GetProperty("face").GetString());
        Assert.Equal(0.5, stats.GetProperty("energy").GetProperty("fraction").GetDouble());
        Assert.Equal(0, stats.GetProperty("nodesOnline").GetInt32());

        HttpResponseMessage page = await _http.GetAsync("");
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
    }

    private async Task<NodeIdentity> SeenAsync(FakeSecurityKey key, string name)
    {
        NodeIdentity identity = NodeProtocol.IdentityFor(key.PublicKey, NodeProtocol.WebAuthnEs256);

        await _directory.SeenAsync(identity, name, Base64Url.EncodeToString(key.CredentialId), CancellationToken.None);

        return identity;
    }

    private async Task SignInAsync(FakeSecurityKey key)
    {
        (byte[] data, byte[] client, byte[] signature) = key.Assert(await ChallengeAsync());

        HttpResponseMessage response = await PostAssertionAsync(key.CredentialId, data, client, signature);
        response.EnsureSuccessStatusCode();

        string token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<byte[]> ChallengeAsync()
    {
        HttpResponseMessage response = await _http.PostAsync("api/sign-in/challenge", null);
        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return Base64Url.DecodeFromChars(body.GetProperty("challenge").GetString());
    }

    private Task<HttpResponseMessage> PostAssertionAsync(byte[] credentialId, byte[] data, byte[] client, byte[] signature) =>
        _http.PostAsJsonAsync("api/sign-in", new
        {
            credentialId      = Base64Url.EncodeToString(credentialId),
            authenticatorData = Base64Url.EncodeToString(data),
            clientDataJson    = Base64Url.EncodeToString(client),
            signature         = Base64Url.EncodeToString(signature)
        });

    private Task<HttpResponseMessage> PostKeySignInAsync(FileNodeKey key, byte[] challenge, byte[] signature) =>
        _http.PostAsJsonAsync("api/sign-in/key", new
        {
            publicKey = Base64Url.EncodeToString(key.ExportPublicKey()),
            challenge = Base64Url.EncodeToString(challenge),
            signature = Base64Url.EncodeToString(signature)
        });

    private async Task<JsonElement> GetJsonAsync(string path) => await ReadJsonAsync(await _http.GetAsync(path));

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

public sealed class SponsorshipTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock              _clock = new();
    private readonly SqliteCreditLedger _ledger;
    private readonly SqliteSponsorships _sponsorships;

    public SponsorshipTests()
    {
        LaneDatabase database = new(new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance);

        _ledger       = new SqliteCreditLedger(database);
        _sponsorships = new SqliteSponsorships(database, _clock);
    }

    [Fact]
    public async Task Charges_rotate_between_sponsors_that_can_pay()
    {
        await _ledger.EarnAsync("a", 10, null, CancellationToken.None);
        await _ledger.EarnAsync("b", 10, null, CancellationToken.None);
        await _ledger.EarnAsync("c", 1, null, CancellationToken.None);

        foreach (string account in new[] { "a", "b", "c" })
        {
            await _sponsorships.SponsorAsync(account, SponsoredKind.DiscordChannel, "42", null, CancellationToken.None);
            _clock.Now += TimeSpan.FromSeconds(1);
        }

        List<string?> charged = [];

        for (int i = 0; i < 6; i++)
            charged.Add(await _sponsorships.ChargeAsync(SponsoredKind.DiscordChannel, "42", 1, CancellationToken.None));

        Assert.Equal(["a", "b", "c", "a", "b", "a"], charged);
        Assert.Equal(0, await _ledger.BalanceAsync("c", CancellationToken.None));
        Assert.Equal(CreditEntryKind.Spent, (await _ledger.HistoryAsync("a", 1, CancellationToken.None))[0].Kind);

        Assert.Null(await _sponsorships.ChargeAsync(SponsoredKind.DiscordChannel, "other", 1, CancellationToken.None));
    }

    [Fact]
    public async Task Daily_limits_stop_charges_until_the_next_utc_day()
    {
        await _ledger.EarnAsync("a", 10, null, CancellationToken.None);
        await _sponsorships.SponsorAsync("a", SponsoredKind.ApiClient, "app", 2, CancellationToken.None);

        Assert.Equal("a", await _sponsorships.ChargeAsync(SponsoredKind.ApiClient, "app", 1, CancellationToken.None));
        Assert.Equal("a", await _sponsorships.ChargeAsync(SponsoredKind.ApiClient, "app", 1, CancellationToken.None));
        Assert.Null(await _sponsorships.ChargeAsync(SponsoredKind.ApiClient, "app", 1, CancellationToken.None));

        Sponsorship today = Assert.Single(await _sponsorships.ListAsync(CancellationToken.None));
        Assert.Equal(2, today.SpentToday);
        Assert.False(today.AllowsToday(1));

        _clock.Now += TimeSpan.FromDays(1);

        Assert.Equal(0, Assert.Single(await _sponsorships.ListAsync(CancellationToken.None)).SpentToday);
        Assert.Equal("a", await _sponsorships.ChargeAsync(SponsoredKind.ApiClient, "app", 1, CancellationToken.None));

        Assert.False(await _sponsorships.SponsorAsync("a", SponsoredKind.ApiClient, "app", null, CancellationToken.None));
        Assert.True(await _sponsorships.WithdrawAsync("a", SponsoredKind.ApiClient, "app", CancellationToken.None));
        Assert.Null(await _sponsorships.ChargeAsync(SponsoredKind.ApiClient, "app", 1, CancellationToken.None));
        Assert.Equal(7, await _ledger.BalanceAsync("a", CancellationToken.None));
    }

    [Fact]
    public async Task Api_client_ids_are_unique_ignoring_case()
    {
        Assert.True(await _sponsorships.CreateApiClientAsync("App", "App", SponsoredApiClient.HashKey("k1"), "a", CancellationToken.None));
        Assert.False(await _sponsorships.CreateApiClientAsync("app", "Other", SponsoredApiClient.HashKey("k2"), "b", CancellationToken.None));

        Assert.Equal("App", (await _sponsorships.FindApiClientAsync("APP", CancellationToken.None))!.Id);
        Assert.Equal("App", (await _sponsorships.FindApiClientByKeyAsync("k1", CancellationToken.None))!.Id);
        Assert.Null(await _sponsorships.FindApiClientByKeyAsync("k2", CancellationToken.None));
    }
}
