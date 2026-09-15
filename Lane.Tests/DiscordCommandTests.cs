using Lane.Core.Memory;
using Lane.Surfaces.Discord;
using Xunit;

namespace Lane.Tests;

public sealed class DiscordCommandTests
{
    [Fact]
    public void Every_defined_command_has_a_reply()
    {
        Assert.Equal<string>(["tos", "privacy-policy", "opt-in", "opt-out"], DiscordCommands.Definitions.Select(c => c.Name));
        Assert.All(DiscordCommands.Definitions, c => Assert.NotNull(DiscordCommands.ReplyTo(c.Name)));
    }

    [Fact]
    public void Commands_link_to_the_published_documents()
    {
        Assert.Contains("https://lane.readthedocs.io/en/latest/discord_tos/", DiscordCommands.ReplyTo("tos"));
        Assert.Contains("https://lane.readthedocs.io/en/latest/discord_privacy_policy/", DiscordCommands.ReplyTo("privacy-policy"));
        Assert.Null(DiscordCommands.ReplyTo("unknown"));
    }

    [Fact]
    public void Only_opt_in_and_opt_out_change_consent()
    {
        Assert.True(DiscordCommands.ConsentSetBy("opt-in"));
        Assert.False(DiscordCommands.ConsentSetBy("opt-out"));
        Assert.Null(DiscordCommands.ConsentSetBy("tos"));
    }

    [Fact]
    public async Task Users_are_ignored_until_they_opt_in_and_consent_survives_a_restart()
    {
        MemoryKeyValueStore store = new();
        DiscordConsent      consent = new(store);

        Assert.False(await consent.IsOptedInAsync(42, CancellationToken.None));

        await consent.SetAsync(42, true, CancellationToken.None);

        Assert.True(await new DiscordConsent(store).IsOptedInAsync(42, CancellationToken.None));
        Assert.False(await consent.IsOptedInAsync(43, CancellationToken.None));

        await consent.SetAsync(42, false, CancellationToken.None);

        Assert.False(await consent.IsOptedInAsync(42, CancellationToken.None));
        Assert.False(await new DiscordConsent(store).IsOptedInAsync(42, CancellationToken.None));
    }

    private sealed class MemoryKeyValueStore : IKeyValueStore
    {
        private readonly Dictionary<(string, string), object?> _values = [];

        public ValueTask<T?> GetAsync<T>(ScopeKey scope, string key, CancellationToken ct) =>
            ValueTask.FromResult(_values.TryGetValue((scope.Value, key), out object? value) ? (T?)value : default);

        public ValueTask SetAsync<T>(ScopeKey scope, string key, T value, CancellationToken ct)
        {
            _values[(scope.Value, key)] = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(ScopeKey scope, string key, CancellationToken ct)
        {
            _values.Remove((scope.Value, key));
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<string>> ListKeysAsync(ScopeKey scope, string prefix, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<string>>(
                [.. _values.Keys.Where(k => k.Item1 == scope.Value && k.Item2.StartsWith(prefix)).Select(k => k.Item2)]);
    }
}
