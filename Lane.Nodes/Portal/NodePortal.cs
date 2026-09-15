using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lane.Core.Credits;
using Lane.Core.Forum;
using Lane.Core.Models;
using Lane.Core.Nodes;
using Lane.Nodes.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Lane.Nodes.Portal;

public sealed record IdentityView(string KeyId, string Name, string? Nickname, string? NodeName, long Responses);

public sealed record LeaderboardEntry(int Rank, IdentityView Identity, bool Online, DateTimeOffset LastSeen);

public sealed record StatsView(
    IReadOnlyList<SurfaceStatus> Surfaces,
    string                       Face,
    EnergyStatus?                Energy,
    int                          NodesOnline,
    int                          ApiClients,
    int?                         DiscordChannels);

public sealed record ChallengeView(string Challenge, IReadOnlyList<string> CredentialIds);

public sealed record SignInView(string Token, IdentityView Identity);

public sealed record WalletEntryView(
    long Amount, CreditEntryKind Kind, string? Counterparty, string? CounterpartyName, string? Memo, DateTimeOffset At);

public sealed record WalletView(IdentityView Identity, long Balance, IReadOnlyList<WalletEntryView> History);

/// <summary>All fields base64url, straight from <c>PublicKeyCredential</c> and its <c>AuthenticatorAssertionResponse</c>.</summary>
public sealed record SignInRequest(string CredentialId, string AuthenticatorData, string ClientDataJson, string Signature);

/// <param name="PublicKey">Base64url SubjectPublicKeyInfo.</param>
/// <param name="Challenge">Base64url, as issued by the challenge endpoint.</param>
/// <param name="Signature">Base64url IEEE P1363 signature over <c>NodeProtocol.SignInPayload</c>, as WebCrypto produces.</param>
public sealed record KeySignInRequest(string PublicKey, string Challenge, string Signature);

public sealed record NicknameRequest(string? Nickname);

public sealed record TransferRequest(string To, long Amount, string? Memo);

/// <param name="CanPay">Has the balance and daily allowance for the next request.</param>
public sealed record SponsorView(IdentityView Identity, long? DailyLimit, long SpentToday, long SpentTotal, bool CanPay, DateTimeOffset Since);

/// <param name="Active">At least one sponsor can pay, so Lane listens there.</param>
public sealed record SponsoredTargetView(SponsoredKind Kind, string Target, string? Name, bool Active, IReadOnlyList<SponsorView> Sponsors);

public sealed record SponsorshipsView(long Balance, long CreditsPerRequest, IReadOnlyList<SponsoredTargetView> Targets);

/// <param name="ApiKey">Only when this request created the API client; never shown again.</param>
public sealed record SponsoredView(SponsoredTargetView Target, string? ApiKey);

/// <param name="Kind">A <see cref="SponsoredKind"/> name.</param>
/// <param name="Name">Display name for a new API client.</param>
public sealed record SponsorRequest(string Kind, string Target, long? DailyLimit, string? Name);

public sealed record DailyLimitRequest(long? DailyLimit);

public sealed record ForumPostView(
    long Id, IdentityView Author, string Title, string Description, DateTimeOffset CreatedAt, DateTimeOffset BumpedAt, int Comments);

public sealed record ForumCommentView(long Id, IdentityView Author, string Body, DateTimeOffset CreatedAt);

public sealed record ForumThreadView(ForumPostView Post, IReadOnlyList<ForumCommentView> Comments);

public sealed record ForumPostRequest(string? Title, string? Description);

public sealed record ForumCommentRequest(string? Body);

/// <summary>
/// The pages served beside the node listener: leaderboard, nickname, wallet, sponsorships, forum and Lane's statistics.
/// Everything but the leaderboard, statistics and reading the forum requires signing in as an identity some node has
/// connected with: a WebAuthn assertion from its security key, or a signature from its key pair.
/// </summary>
public sealed class NodePortal(
    NodePool          pool,
    INodeDirectory    directory,
    ICreditLedger     ledger,
    ISponsorships     sponsorships,
    IForum            forum,
    ILaneStatusSource status,
    NodesOptions      options,
    TimeProvider?     time = null)
{
    public const int MaxNicknameLength   = 32;
    public const int MaxMemoLength       = 140;
    public const int MaxClientIdLength   = 64;
    public const int MaxClientNameLength = 64;
    public const int MaxForumTitleLength = 120;
    public const int MaxForumTextLength  = 10_000;

    private const int    HistoryLimit = 50;
    private const int    ForumPageSize = 100;
    private const string PageResource = "Lane.Nodes.Portal.portal.html";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter() }
    };

    private readonly PortalSignIns _signIns = new(time ?? TimeProvider.System, options.PortalSignInLifetime);

    /// <summary>The key id a sign-in token was issued to; null if it is unknown, revoked or expired.</summary>
    public string? SignedInKeyId(string? token) => _signIns.Resolve(token);

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Stream(
            typeof(NodePortal).Assembly.GetManifestResourceStream(PageResource)!, "text/html; charset=utf-8"));

        app.MapGet("/api/leaderboard", LeaderboardAsync);
        app.MapGet("/api/stats", Stats);
        app.MapPost("/api/sign-in/challenge", ChallengeAsync);
        app.MapPost("/api/sign-in", SignInAsync);
        app.MapPost("/api/sign-in/key", KeySignInAsync);
        app.MapPost("/api/sign-out", SignOut);
        app.MapGet("/api/me", MeAsync);
        app.MapPut("/api/me/nickname", SetNicknameAsync);
        app.MapPost("/api/me/transfers", TransferAsync);
        app.MapGet("/api/me/sponsorships", SponsorshipsAsync);
        app.MapPost("/api/me/sponsorships", SponsorAsync);
        app.MapPut("/api/me/sponsorships/{kind}/{target}", SetDailyLimitAsync);
        app.MapDelete("/api/me/sponsorships/{kind}/{target}", WithdrawAsync);
        app.MapGet("/api/forum/posts", ForumPostsAsync);
        app.MapGet("/api/forum/posts/{id:long}", ForumThreadAsync);
        app.MapPost("/api/me/forum/posts", CreateForumPostAsync);
        app.MapPost("/api/me/forum/posts/{id:long}/comments", CommentAsync);
    }

    private async Task<IResult> LeaderboardAsync(CancellationToken ct)
    {
        HashSet<string> online = [.. pool.Snapshot().Values.SelectMany(nodes => nodes).Select(node => node.KeyId)];

        IReadOnlyList<NodeIdentityRecord> identities = await directory.ListAsync(ct).ConfigureAwait(false);

        List<LeaderboardEntry> entries = [];

        for (int i = 0; i < identities.Count; i++)
        {
            NodeIdentityRecord identity = identities[i];
            int rank = i > 0 && identities[i - 1].Responses == identity.Responses ? entries[i - 1].Rank : i + 1;

            entries.Add(new LeaderboardEntry(rank, View(identity), online.Contains(identity.KeyId), identity.LastSeen));
        }

        return Results.Json(entries, Json);
    }

    private IResult Stats()
    {
        LaneStatus current = status.Current;

        return Results.Json(
            new StatsView(current.Surfaces, current.Face, current.Energy, pool.Snapshot().Values.Sum(nodes => nodes.Count),
                current.ApiClients, current.DiscordChannels),
            Json);
    }

    private async Task<IResult> ChallengeAsync(CancellationToken ct)
    {
        IReadOnlyList<string> credentials = await directory.CredentialIdsAsync(ct).ConfigureAwait(false);

        return Results.Json(new ChallengeView(Base64Url.EncodeToString(_signIns.NewChallenge()), credentials), Json);
    }

    private async Task<IResult> SignInAsync(SignInRequest body, CancellationToken ct)
    {
        string credentialId;
        byte[] authenticatorData, clientDataJson, signature, challenge;
        string challengeText;

        try
        {
            credentialId      = Base64Url.EncodeToString(Base64Url.DecodeFromChars(body.CredentialId));
            authenticatorData = Base64Url.DecodeFromChars(body.AuthenticatorData);
            clientDataJson    = Base64Url.DecodeFromChars(body.ClientDataJson);
            signature         = Base64Url.DecodeFromChars(body.Signature);

            using JsonDocument clientData = JsonDocument.Parse(clientDataJson);
            challengeText = clientData.RootElement.GetProperty("challenge").GetString() ?? "";
            challenge     = Base64Url.DecodeFromChars(challengeText);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Error(StatusCodes.Status400BadRequest, "The security key's answer is malformed.");
        }

        NodeIdentityRecord? signer = Signer(
            await directory.FindByCredentialAsync(credentialId, ct).ConfigureAwait(false),
            authenticatorData, clientDataJson, signature, challenge);

        if (signer is null)
        {
            signer = Signer(await directory.ListAsync(ct).ConfigureAwait(false), authenticatorData, clientDataJson, signature, challenge);

            if (signer is not null) await directory.LinkCredentialAsync(signer.KeyId, credentialId, ct).ConfigureAwait(false);
        }

        if (signer is null)
            return Error(StatusCodes.Status401Unauthorized, "That security key does not belong to any node identity Lane knows.");

        if (!_signIns.Redeem(challengeText))
            return Error(StatusCodes.Status401Unauthorized, "The sign-in took too long or was already used; try again.");

        return Results.Json(new SignInView(_signIns.Issue(signer.KeyId), View(signer)), Json);
    }

    private async Task<IResult> KeySignInAsync(KeySignInRequest body, CancellationToken ct)
    {
        byte[] publicKey, challenge, signature;

        try
        {
            publicKey = Base64Url.DecodeFromChars(body.PublicKey);
            challenge = Base64Url.DecodeFromChars(body.Challenge);
            signature = Base64Url.DecodeFromChars(body.Signature);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            return Error(StatusCodes.Status400BadRequest, "The key's answer is malformed.");
        }

        NodeIdentity identity = NodeProtocol.IdentityFor(publicKey);

        if (!NodeProtocol.VerifySignIn(identity, challenge, signature))
            return Error(StatusCodes.Status401Unauthorized, "The signature does not match that key.");

        NodeIdentityRecord? signer = await directory.FindAsync(identity.KeyId, ct).ConfigureAwait(false);

        if (signer is null || signer.Algorithm != NodeProtocol.EcdsaP256Sha256 || signer.PublicKey != identity.PublicKey)
            return Error(StatusCodes.Status401Unauthorized, "Lane doesn't know that key yet. Connect a node with it first.");

        if (!_signIns.Redeem(Base64Url.EncodeToString(challenge)))
            return Error(StatusCodes.Status401Unauthorized, "The sign-in took too long or was already used; try again.");

        return Results.Json(new SignInView(_signIns.Issue(signer.KeyId), View(signer)), Json);
    }

    private static NodeIdentityRecord? Signer(
        IEnumerable<NodeIdentityRecord> candidates, byte[] authenticatorData, byte[] clientDataJson, byte[] signature, byte[] challenge) =>
        candidates.FirstOrDefault(candidate =>
            candidate.Algorithm == NodeProtocol.WebAuthnEs256 &&
            NodeProtocol.VerifyAssertion(candidate.PublicKey, authenticatorData, clientDataJson, signature, challenge));

    private IResult SignOut(HttpContext context)
    {
        if (BearerToken(context) is { } token) _signIns.Revoke(token);

        return Results.NoContent();
    }

    private async Task<IResult> MeAsync(HttpContext context, CancellationToken ct)
    {
        if (await SignedInAsync(context, ct).ConfigureAwait(false) is not { } me) return Unauthorised();

        Dictionary<string, string> names = (await directory.ListAsync(ct).ConfigureAwait(false))
            .ToDictionary(identity => identity.KeyId, identity => identity.DisplayName);

        long balance = await ledger.BalanceAsync(me.KeyId, ct).ConfigureAwait(false);
        IReadOnlyList<CreditEntry> history = await ledger.HistoryAsync(me.KeyId, HistoryLimit, ct).ConfigureAwait(false);

        return Results.Json(new WalletView(View(me), balance, [.. history.Select(entry => new WalletEntryView(
            entry.Amount,
            entry.Kind,
            entry.Counterparty,
            entry.Counterparty is null ? null : names.GetValueOrDefault(entry.Counterparty, entry.Counterparty),
            entry.Memo,
            entry.At))]), Json);
    }

    private async Task<IResult> SetNicknameAsync(HttpContext context, NicknameRequest body, CancellationToken ct)
    {
        if (await SignedInAsync(context, ct).ConfigureAwait(false) is not { } me) return Unauthorised();

        string? nickname = string.IsNullOrWhiteSpace(body.Nickname) ? null : body.Nickname.Trim();

        if (nickname is { Length: > MaxNicknameLength })
            return Error(StatusCodes.Status400BadRequest, $"Nicknames can be at most {MaxNicknameLength} characters.");

        if (nickname is not null && nickname.Any(char.IsControl))
            return Error(StatusCodes.Status400BadRequest, "Nicknames cannot contain control characters.");

        try
        {
            await directory.SetNicknameAsync(me.KeyId, nickname, ct).ConfigureAwait(false);
        }
        catch (NicknameTakenException ex)
        {
            return Error(StatusCodes.Status409Conflict, ex.Message);
        }

        return Results.Json(View((await directory.FindAsync(me.KeyId, ct).ConfigureAwait(false))!), Json);
    }

    private async Task<IResult> TransferAsync(HttpContext context, TransferRequest body, CancellationToken ct)
    {
        if (await SignedInAsync(context, ct).ConfigureAwait(false) is not { } me) return Unauthorised();

        if (body.Amount <= 0)
            return Error(StatusCodes.Status400BadRequest, "Send at least one credit.");

        if (body.To == me.KeyId)
            return Error(StatusCodes.Status400BadRequest, "You cannot send credits to yourself.");

        string? memo = string.IsNullOrWhiteSpace(body.Memo) ? null : body.Memo.Trim();

        if (memo is { Length: > MaxMemoLength })
            return Error(StatusCodes.Status400BadRequest, $"Notes can be at most {MaxMemoLength} characters.");

        if (await directory.FindAsync(body.To, ct).ConfigureAwait(false) is not { } recipient)
            return Error(StatusCodes.Status404NotFound, "No identity has that key id.");

        try
        {
            long balance = await ledger.TransferAsync(me.KeyId, recipient.KeyId, body.Amount, memo, ct).ConfigureAwait(false);

            return Results.Json(new { balance }, Json);
        }
        catch (InsufficientCreditsException ex)
        {
            return Error(StatusCodes.Status400BadRequest, $"You only have {ex.Balance} credits.");
        }
    }

    private async Task<IResult> SponsorshipsAsync(HttpContext context, CancellationToken ct)
    {
        if (await SignedInAsync(context, ct).ConfigureAwait(false) is not { } me) return Unauthorised();

        return Results.Json(new SponsorshipsView(
            await ledger.BalanceAsync(me.KeyId, ct).ConfigureAwait(false),
            options.CreditsPerSponsoredRequest,
            await TargetsAsync(ct).ConfigureAwait(false)), Json);
    }

    private async Task<IResult> SponsorAsync(HttpContext context, SponsorRequest body, CancellationToken ct)
    {
        if (await SignedInAsync(context, ct).ConfigureAwait(false) is not { } me) return Unauthorised();

        if (ParseKind(body.Kind) is not { } kind)
            return Error(StatusCodes.Status400BadRequest, "Choose a Discord channel or an API client.");

        if (body.DailyLimit is < 0)
            return Error(StatusCodes.Status400BadRequest, "Daily limits cannot be negative.");

        string  target = body.Target?.Trim() ?? "";
        string? apiKey = null;

        if (kind == SponsoredKind.DiscordChannel)
        {
            if (!ulong.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out ulong channel) || channel == 0)
                return Error(StatusCodes.Status400BadRequest, "Discord channel ids are numbers. Copy one with Developer Mode on.");

            target = channel.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (!IsClientId(target))
                return Error(StatusCodes.Status400BadRequest,
                    $"Client ids are 1 to {MaxClientIdLength} letters, digits, '-', '_' or '.'.");

            string name = string.IsNullOrWhiteSpace(body.Name) ? target : body.Name.Trim();

            if (name.Length > MaxClientNameLength || name.Any(char.IsControl))
                return Error(StatusCodes.Status400BadRequest, $"Client names are at most {MaxClientNameLength} characters.");

            SponsoredApiClient? existing = await sponsorships.FindApiClientAsync(target, ct).ConfigureAwait(false);

            if (existing is null)
            {
                string key = "lane_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

                if (await sponsorships.CreateApiClientAsync(target, name, SponsoredApiClient.HashKey(key), me.KeyId, ct).ConfigureAwait(false))
                    apiKey = key;
                else
                    existing = await sponsorships.FindApiClientAsync(target, ct).ConfigureAwait(false);
            }

            target = existing?.Id ?? target;
        }

        await sponsorships.SponsorAsync(me.KeyId, kind, target, body.DailyLimit, ct).ConfigureAwait(false);

        SponsoredTargetView view = (await TargetsAsync(ct).ConfigureAwait(false)).First(t => t.Kind == kind && t.Target == target);

        return Results.Json(new SponsoredView(view, apiKey), Json);
    }

    private async Task<IResult> SetDailyLimitAsync(HttpContext context, string kind, string target, DailyLimitRequest body, CancellationToken ct)
    {
        if (await SignedInAsync(context, ct).ConfigureAwait(false) is not { } me) return Unauthorised();

        if (body.DailyLimit is < 0)
            return Error(StatusCodes.Status400BadRequest, "Daily limits cannot be negative.");

        if (await MineAsync(me, kind, target, ct).ConfigureAwait(false) is not { } mine) return NotSponsoring();

        await sponsorships.SponsorAsync(me.KeyId, mine.Kind, mine.Target, body.DailyLimit, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    private async Task<IResult> WithdrawAsync(HttpContext context, string kind, string target, CancellationToken ct)
    {
        if (await SignedInAsync(context, ct).ConfigureAwait(false) is not { } me) return Unauthorised();

        if (await MineAsync(me, kind, target, ct).ConfigureAwait(false) is not { } mine) return NotSponsoring();

        await sponsorships.WithdrawAsync(me.KeyId, mine.Kind, mine.Target, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <param name="sort"><c>bumped</c> (the default) or <c>newest</c>.</param>
    private async Task<IResult> ForumPostsAsync(string? sort, CancellationToken ct)
    {
        ForumSort? order = sort?.ToLowerInvariant() switch
        {
            null or "bumped" => ForumSort.Bumped,
            "newest"         => ForumSort.Newest,
            _                => null
        };

        if (order is null) return Error(StatusCodes.Status400BadRequest, "Sort by bumped or newest.");

        IReadOnlyList<ForumPost> posts = await forum.ListPostsAsync(order.Value, ForumPageSize, ct).ConfigureAwait(false);
        Dictionary<string, NodeIdentityRecord> identities = await IdentitiesAsync(ct).ConfigureAwait(false);

        return Results.Json(posts.Select(post => View(post, identities)).ToList(), Json);
    }

    private async Task<IResult> ForumThreadAsync(long id, CancellationToken ct)
    {
        if (await forum.FindPostAsync(id, ct).ConfigureAwait(false) is not { } post) return NoSuchPost();

        IReadOnlyList<ForumComment> comments = await forum.CommentsAsync(id, ct).ConfigureAwait(false);
        Dictionary<string, NodeIdentityRecord> identities = await IdentitiesAsync(ct).ConfigureAwait(false);

        return Results.Json(new ForumThreadView(View(post, identities), [.. comments.Select(comment =>
            new ForumCommentView(comment.Id, Named(identities, comment.Author), comment.Body, comment.CreatedAt))]), Json);
    }

    private async Task<IResult> CreateForumPostAsync(HttpContext context, ForumPostRequest body, CancellationToken ct)
    {
        if (await SignedInAsync(context, ct).ConfigureAwait(false) is not { } me) return Unauthorised();

        string title       = body.Title?.Trim() ?? "";
        string description = body.Description?.Trim() ?? "";

        if (InvalidForumText(title, "The title", MaxForumTitleLength, multiline: false) is { } badTitle) return badTitle;
        if (InvalidForumText(description, "The description", MaxForumTextLength, multiline: true) is { } badDescription)
            return badDescription;

        ForumPost post = await forum.CreatePostAsync(me.KeyId, title, description, ct).ConfigureAwait(false);

        return Results.Json(View(post, new Dictionary<string, NodeIdentityRecord> { [me.KeyId] = me }), Json,
            statusCode: StatusCodes.Status201Created);
    }

    private async Task<IResult> CommentAsync(HttpContext context, long id, ForumCommentRequest body, CancellationToken ct)
    {
        if (await SignedInAsync(context, ct).ConfigureAwait(false) is not { } me) return Unauthorised();

        string text = body.Body?.Trim() ?? "";

        if (InvalidForumText(text, "The comment", MaxForumTextLength, multiline: true) is { } bad) return bad;

        if (await forum.CommentAsync(id, me.KeyId, text, ct).ConfigureAwait(false) is not { } comment) return NoSuchPost();

        return Results.Json(new ForumCommentView(comment.Id, View(me), comment.Body, comment.CreatedAt), Json,
            statusCode: StatusCodes.Status201Created);
    }

    private static IResult? InvalidForumText(string text, string what, int maxLength, bool multiline) =>
        text.Length == 0 ? Error(StatusCodes.Status400BadRequest, $"{what} cannot be empty.")
        : text.Length > maxLength ? Error(StatusCodes.Status400BadRequest, $"{what} can be at most {maxLength} characters.")
        : text.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t'))
            ? Error(StatusCodes.Status400BadRequest, $"{what} cannot contain control characters.")
        : null;

    private static ForumPostView View(ForumPost post, IReadOnlyDictionary<string, NodeIdentityRecord> identities) =>
        new(post.Id, Named(identities, post.Author), post.Title, post.Description, post.CreatedAt, post.BumpedAt, post.Comments);

    private static IResult NoSuchPost() =>
        Error(StatusCodes.Status404NotFound, "That post does not exist.");

    private async Task<Sponsorship?> MineAsync(NodeIdentityRecord me, string kind, string target, CancellationToken ct) =>
        ParseKind(kind) is { } parsed
            ? (await sponsorships.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(s =>
                s.Account == me.KeyId && s.Kind == parsed && string.Equals(s.Target, target, StringComparison.OrdinalIgnoreCase))
            : null;

    private async Task<IReadOnlyList<SponsoredTargetView>> TargetsAsync(CancellationToken ct)
    {
        IReadOnlyList<Sponsorship> all = await sponsorships.ListAsync(ct).ConfigureAwait(false);

        Dictionary<string, NodeIdentityRecord> identities = await IdentitiesAsync(ct).ConfigureAwait(false);

        Dictionary<string, SponsoredApiClient> clients = (await sponsorships.ListApiClientsAsync(ct).ConfigureAwait(false))
            .ToDictionary(client => client.Id, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, long> balances = [];

        foreach (string account in all.Select(s => s.Account).Distinct())
            balances[account] = await ledger.BalanceAsync(account, ct).ConfigureAwait(false);

        long cost = options.CreditsPerSponsoredRequest;

        return [.. all
            .GroupBy(s => (s.Kind, s.Target))
            .OrderBy(group => group.Key.Kind).ThenBy(group => group.Key.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                SponsorView[] sponsors = [.. group.OrderBy(s => s.Since).Select(s => new SponsorView(
                    Named(identities, s.Account),
                    s.DailyLimit, s.SpentToday, s.SpentTotal, balances[s.Account] >= cost && s.AllowsToday(cost), s.Since))];

                return new SponsoredTargetView(
                    group.Key.Kind,
                    group.Key.Target,
                    group.Key.Kind == SponsoredKind.ApiClient ? clients.GetValueOrDefault(group.Key.Target)?.Name : null,
                    sponsors.Any(sponsor => sponsor.CanPay),
                    sponsors);
            })];
    }

    private static SponsoredKind? ParseKind(string? kind) =>
        Enum.TryParse(kind, ignoreCase: true, out SponsoredKind parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private static bool IsClientId(string id) =>
        id.Length is > 0 and <= MaxClientIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static IResult NotSponsoring() =>
        Error(StatusCodes.Status404NotFound, "You are not sponsoring that.");

    private async Task<NodeIdentityRecord?> SignedInAsync(HttpContext context, CancellationToken ct) =>
        _signIns.Resolve(BearerToken(context)) is { } keyId
            ? await directory.FindAsync(keyId, ct).ConfigureAwait(false)
            : null;

    private static string? BearerToken(HttpContext context)
    {
        string header = context.Request.Headers.Authorization.ToString();

        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }

    private async Task<Dictionary<string, NodeIdentityRecord>> IdentitiesAsync(CancellationToken ct) =>
        (await directory.ListAsync(ct).ConfigureAwait(false)).ToDictionary(identity => identity.KeyId);

    private static IdentityView View(NodeIdentityRecord identity) =>
        new(identity.KeyId, identity.DisplayName, identity.Nickname, identity.LastNodeName, identity.Responses);

    /// <summary>Falls back to a shortened key id for identities the directory no longer has.</summary>
    private static IdentityView Named(IReadOnlyDictionary<string, NodeIdentityRecord> identities, string keyId) =>
        identities.TryGetValue(keyId, out NodeIdentityRecord? identity)
            ? View(identity)
            : new IdentityView(keyId, keyId.Length > 8 ? keyId[..8] : keyId, null, null, 0);

    private static IResult Unauthorised() =>
        Error(StatusCodes.Status401Unauthorized, "Sign in first.");

    private static IResult Error(int statusCode, string message) =>
        Results.Json(new { error = message }, Json, statusCode: statusCode);
}
