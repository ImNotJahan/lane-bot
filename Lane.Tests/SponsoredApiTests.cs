using System.Net;
using System.Text.Json;
using Lane.Core.Credits;
using Xunit;

namespace Lane.Tests;

public sealed class SponsoredApiTests
{
    private sealed class FakeAccess : ISponsoredAccess
    {
        public int Credits { get; set; }

        public List<string> Charged { get; } = [];

        public ValueTask<bool> TryChargeAsync(SponsoredKind kind, string target, CancellationToken ct)
        {
            if (Credits == 0) return ValueTask.FromResult(false);

            Credits--;
            Charged.Add($"{kind}:{target}");

            return ValueTask.FromResult(true);
        }

        public ValueTask<SponsoredApiClient?> FindApiClientAsync(string key, CancellationToken ct) =>
            ValueTask.FromResult(key == "sponsored-key"
                ? new SponsoredApiClient("app", "App", "someone", DateTimeOffset.UnixEpoch)
                : null);
    }

    [Fact]
    public async Task Sponsored_clients_pay_per_message_and_are_refused_without_credits()
    {
        FakeAccess access = new() { Credits = 1 };

        await using ApiFixture api = await ApiFixture.StartAsync(sponsored: access);

        HttpClient sponsored = api.Client("sponsored-key");

        Assert.Equal(HttpStatusCode.Accepted, (await ApiFixture.SendAsync(sponsored, "main", new { text = "hi" })).StatusCode);
        Assert.Equal(["ApiClient:app"], access.Charged);

        HttpResponseMessage refused = await ApiFixture.SendAsync(sponsored, "main", new { text = "again" });
        Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
        Assert.Equal("out_of_credits", (await ApiFixture.JsonAsync(refused)).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.OK, (await sponsored.GetAsync("/v1/sessions")).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await ApiFixture.SendAsync(api.Client(), "main", new { text = "free" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client("wrong").GetAsync("/v1/sessions")).StatusCode);

        JsonElement sessions = await ApiFixture.JsonAsync(await sponsored.GetAsync("/v1/sessions"));
        Assert.Contains("sponsored.app", sessions.GetRawText());
    }
}
