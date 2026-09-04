using System.Collections.Concurrent;
using System.Text.Json;
using Lane.Core;
using Lane.Core.Agent;
using Lane.Core.Energy;
using Lane.Core.Events;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Pipeline;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Memory;
using Lane.Memory.Sqlite;
using Lane.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Tiredness: a token budget that accrues back over a rolling window, and what running low on
/// it does to her.
///
/// The arithmetic here is deliberately round — a budget of 144 000 over a day in ten-minute
/// steps is exactly 1 000 a step — so a failing assertion says which step went wrong rather
/// than which rounding did.
/// </summary>
public sealed class EnergyTests
{
    private const long Budget = 144_000;
    private const long Step   = 1_000;

    private static readonly DateTimeOffset Start = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static EnergyOptions Options(Action<EnergyOptions>? configure = null)
    {
        EnergyOptions options = new()
        {
            Enabled         = true,
            Budget          = Budget,
            Window          = TimeSpan.FromHours(24),
            AccrualInterval = TimeSpan.FromMinutes(10)
        };

        configure?.Invoke(options);

        return options;
    }

    private static EnergyService Service(
        EnergyOptions? options = null,
        FakeClock? clock = null,
        IKeyValueStore? store = null,
        IEventBus? bus = null) =>
        new(bus ?? new EventBus(),
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            NullLogger<EnergyService>.Instance,
            store,
            clock ?? new FakeClock(Start));

    private static TokenUsage Spent(int input = 0, int output = 0, int cacheRead = 0, int cacheWrite = 0) =>
        new(input, output, cacheRead, cacheWrite, "scripted", TimeSpan.Zero);

    /// <summary>
    /// Only <see cref="GetUtcNow"/> is overridden, which is enough precisely because accrual is
    /// lazy: nothing here waits on a timer to move the number along.
    /// </summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;

        public void Rewind(TimeSpan by) => _now -= by;
    }

    // ---- accrual and debit ------------------------------------------------

    [Fact]
    public void She_starts_a_first_run_rested()
    {
        // Booting asleep, with nothing on record and no way to find out why, reads as broken
        // rather than as tired.
        EnergyState state = Service().Current;

        Assert.Equal(Budget, state.Remaining);
        Assert.Equal(1, state.Fraction);
        Assert.Equal(EnergyTier.Rested, state.Tier);
        Assert.False(state.Asleep);
    }

    [Fact]
    public void Spending_debits_what_a_call_cost()
    {
        EnergyService energy = Service();

        energy.Spend(Spent(input: 100, output: 50), "respond");

        Assert.Equal(Budget - 150, energy.Current.Remaining);
    }

    [Fact]
    public void A_cached_read_costs_a_tenth_of_a_fresh_token()
    {
        // Anthropic reports cache reads outside Input, so counting them at face value would
        // overstate a long conversation and ignoring them entirely would understate the bill.
        EnergyService energy = Service();

        energy.Spend(Spent(cacheRead: 1_000, cacheWrite: 200), "respond");

        Assert.Equal(Budget - 300, energy.Current.Remaining);
    }

    [Fact]
    public void Energy_accrues_one_step_per_interval_and_not_a_moment_sooner()
    {
        FakeClock clock = new(Start);
        EnergyService energy = Service(clock: clock);

        energy.Spend(Spent(output: 5_000), "respond");

        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Equal(Budget - 5_000, energy.Current.Remaining);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Budget - 5_000 + Step, energy.Current.Remaining);
    }

    [Fact]
    public void The_accrual_phase_does_not_drift_when_the_number_is_read_often()
    {
        // Advancing by whole steps rather than to now. Otherwise every read would drag the
        // next accrual forward and a busy channel would accrue more slowly than a quiet one.
        FakeClock clock = new(Start);
        EnergyService energy = Service(clock: clock);

        energy.Spend(Spent(output: 5_000), "respond");

        clock.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal(Budget - 5_000 + 2 * Step, energy.Current.Remaining);

        // Five more minutes is thirty in total, which is the third step and not the fourth.
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(Budget - 5_000 + 3 * Step, energy.Current.Remaining);
    }

    [Fact]
    public void A_whole_day_of_accrual_cannot_take_her_past_the_budget()
    {
        FakeClock clock = new(Start);
        EnergyService energy = Service(clock: clock);

        energy.Spend(Spent(output: 500), "respond");

        clock.Advance(TimeSpan.FromDays(2));

        Assert.Equal(Budget, energy.Current.Remaining);
    }

    [Fact]
    public void A_clock_that_goes_backwards_neither_accrues_nor_throws()
    {
        // A VM restored, or an NTP correction. Re-anchoring means she neither gains a windfall
        // nor stalls forever waiting for a boundary that has already gone past.
        FakeClock clock = new(Start);
        EnergyService energy = Service(clock: clock);

        energy.Spend(Spent(output: 5_000), "respond");

        clock.Rewind(TimeSpan.FromHours(3));

        Assert.Equal(Budget - 5_000, energy.Current.Remaining);

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(Budget - 5_000 + Step, energy.Current.Remaining);
    }

    [Fact]
    public void One_runaway_turn_cannot_put_her_a_week_in_debt()
    {
        EnergyService energy = Service();

        energy.Spend(Spent(output: int.MaxValue), "respond");

        Assert.Equal(-Budget, energy.Current.Remaining);
    }

    [Theory]
    [InlineData(1.00, EnergyTier.Rested)]
    [InlineData(0.70, EnergyTier.Rested)]
    [InlineData(0.69, EnergyTier.Tired)]
    [InlineData(0.30, EnergyTier.Tired)]
    [InlineData(0.29, EnergyTier.Weary)]
    [InlineData(0.00, EnergyTier.Weary)]
    public void The_tiers_are_where_they_are_configured(double fraction, EnergyTier expected)
    {
        EnergyService energy = Service();

        energy.Spend(Spent(output: (int)(Budget * (1 - fraction))), "respond");

        Assert.Equal(expected, energy.Current.Tier);
    }

    // ---- the sleep state machine ------------------------------------------

    [Fact]
    public void Running_out_puts_her_to_sleep()
    {
        EnergyService energy = Service();

        energy.Spend(Spent(output: (int)Budget), "respond");

        Assert.True(energy.Current.Asleep);
    }

    [Fact]
    public void Sleep_ends_on_its_own_only_once_she_is_properly_rested()
    {
        FakeClock clock = new(Start);
        EnergyService energy = Service(clock: clock);

        energy.Spend(Spent(output: (int)Budget), "respond");

        // A hundred steps is 100 000, which is 69% — not yet.
        clock.Advance(TimeSpan.FromMinutes(10 * 100));
        Assert.True(energy.Current.Asleep);

        // One more crosses seventy.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(energy.Current.Asleep);
    }

    [Fact]
    public void Being_named_with_energy_in_hand_wakes_her()
    {
        FakeClock clock = new(Start);
        EnergyService energy = Service(clock: clock);

        energy.Spend(Spent(output: (int)Budget), "respond");

        // Nowhere near rested, but no longer at nothing.
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(energy.Wake("named"));
        Assert.False(energy.Current.Asleep);
    }

    [Fact]
    public void Being_named_with_nothing_left_answers_once_and_stays_asleep()
    {
        // The whole point of latching sleep rather than deriving it: she can be dragged out to
        // answer without that counting as having woken up.
        EnergyService energy = Service();

        energy.Spend(Spent(output: (int)Budget), "respond");

        Assert.False(energy.Wake("named"));
        Assert.True(energy.Current.Asleep);
    }

    [Fact]
    public void Waking_an_already_awake_lane_changes_nothing()
    {
        EnergyService energy = Service();

        Assert.True(energy.Wake("named"));
        Assert.False(energy.Current.Asleep);
    }

    [Fact]
    public void How_long_until_she_wakes_is_answerable_while_she_sleeps()
    {
        EnergyService energy = Service();

        Assert.Null(energy.TimeUntilRested);

        energy.Spend(Spent(output: (int)Budget), "respond");

        // Zero to seventy percent of 144 000 is 101 steps of ten minutes.
        Assert.Equal(TimeSpan.FromMinutes(1010), energy.TimeUntilRested);
    }

    // ---- persistence ------------------------------------------------------

    [Fact]
    public async Task Falling_asleep_is_written_down_at_once()
    {
        // Everything else can ride the ten-minute tick, because the worst a crash can do is
        // restart her slightly better rested than she should be. Sleep cannot: it is the one
        // part of the state the clock cannot recompute.
        RecordingStore store = new();

        EnergyService energy = Service(store: store);

        energy.Spend(Spent(output: (int)Budget), "respond");

        Assert.True(await store.WaitForWriteAsync());
        Assert.True(store.Last!.Asleep);
    }

    [Fact]
    public async Task Energy_survives_a_restart_and_accrues_for_the_time_she_was_off()
    {
        RecordingStore store = new();
        FakeClock clock = new(Start);

        EnergyService before = Service(clock: clock, store: store, bus: new EventBus());

        await before.StartAsync(default);
        before.Spend(Spent(output: 50_000), "respond");
        await before.StopAsync(default);

        Assert.Equal(Budget - 50_000, store.Last!.Remaining);

        // Three hours down is eighteen steps she is owed on the way back up.
        clock.Advance(TimeSpan.FromHours(3));

        EnergyService after = Service(clock: clock, store: store, bus: new EventBus());

        await after.StartAsync(default);

        Assert.Equal(Budget - 50_000 + 18 * Step, after.Current.Remaining);

        await after.StopAsync(default);
    }

    [Fact]
    public async Task Lowering_the_budget_between_runs_does_not_leave_her_over_full()
    {
        RecordingStore store = new();

        await store.SetAsync(new ScopeKey("global"), "energy",
            new { Remaining = 10_000_000L, AccruedThrough = Start, Asleep = false }, default);

        EnergyService energy = Service(store: store);

        await energy.StartAsync(default);

        Assert.Equal(Budget, energy.Current.Remaining);

        await energy.StopAsync(default);
    }

    /// <summary>An in-memory key-value store that also says when it was last written to.</summary>
    private sealed class RecordingStore : IKeyValueStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        private readonly SemaphoreSlim _written = new(0);

        public Stored? Last { get; private set; }

        public sealed record Stored(long Remaining, DateTimeOffset AccruedThrough, bool Asleep);

        public Task<bool> WaitForWriteAsync() => _written.WaitAsync(TimeSpan.FromSeconds(5));

        public ValueTask<T?> GetAsync<T>(ScopeKey scope, string key, CancellationToken ct) =>
            ValueTask.FromResult(_values.TryGetValue($"{scope.Value}/{key}", out string? json)
                ? JsonSerializer.Deserialize<T>(json)
                : default);

        public ValueTask SetAsync<T>(ScopeKey scope, string key, T value, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(value);

            _values[$"{scope.Value}/{key}"] = json;

            Last = JsonSerializer.Deserialize<Stored>(json);

            _written.Release();

            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(ScopeKey scope, string key, CancellationToken ct)
        {
            _values.TryRemove($"{scope.Value}/{key}", out _);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<string>> ListKeysAsync(ScopeKey scope, string prefix, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);
    }

    // ---- what a turn does about it ----------------------------------------

    /// <summary>
    /// A hand-driven metabolism, so a pipeline test can put her in a state without spending
    /// three hours of fake clock getting there.
    /// </summary>
    private sealed class StubEnergy : IEnergyService
    {
        public EnergyTier Tier   { get; set; } = EnergyTier.Rested;
        public bool       Asleep { get; set; }

        /// <summary>Whether being named actually ends the sleep, or only buys one answer.</summary>
        public bool WakesFully { get; set; }

        public int WakeCalls { get; private set; }

        public EnergyState Current => new(Asleep ? 0 : 100, 100, Asleep, default) { Tier = Tier };

        public void Spend(TokenUsage usage, string? role) { }

        public bool Wake(string reason)
        {
            WakeCalls++;

            if (!WakesFully) return false;

            Asleep = false;
            return true;
        }

        public TimeSpan? TimeUntilRested => Asleep ? TimeSpan.FromHours(1) : null;
    }

    private static LaneHarness Harness(
        StubEnergy energy,
        ScriptedLanguageModel? model = null,
        Action<IServiceCollection>? configure = null) =>
        LaneHarness.Create(model ?? ScriptedLanguageModel.Echoing("mm"), services =>
        {
            services.AddSingleton<IEnergyService>(energy);
            configure?.Invoke(services);
        });

    [Fact]
    public async Task An_asleep_lane_ignores_a_message_that_does_not_name_her()
    {
        await using LaneHarness harness = Harness(new StubEnergy { Asleep = true });

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "anyone know what time it is");

        await channel.QuietAsync();

        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task Sleeping_through_a_message_costs_no_model_call_at_all()
    {
        // The stage ordering, asserted. The sleep gate sits ahead of the response policy, so
        // she does not pay a small model to decide whether to answer something she is not
        // awake for. This is the regression test for moving it.
        await using LaneHarness harness = Harness(
            new StubEnergy { Asleep = true },
            configure: services => services.Configure<Lane.Core.Pipeline.Stages.ResponsePolicyOptions>(o =>
            {
                o.Enabled              = true;
                o.SkipInDirectSessions = false;
            }));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "chatter");

        await channel.QuietAsync();

        Assert.Equal(0, harness.Model.CallCount);
    }

    [Fact]
    public async Task A_message_she_slept_through_reaches_the_transcript_but_not_memory()
    {
        // Nothing said to her is ever lost. What does not happen is the summarising and
        // profiling — she was not there, and those cost model calls of their own.
        RecordingTranscript transcript = new();
        RecordingMemory     memory     = new();

        await using LaneHarness harness = Harness(new StubEnergy { Asleep = true }, configure: services =>
        {
            services.AddSingleton<ITranscriptStore>(transcript);
            services.AddSingleton<IMemoryService>(memory);
        });

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "a thing worth keeping");

        await channel.QuietAsync();

        Assert.Contains(transcript.Written, m => m.TextContent.Contains("a thing worth keeping"));
        Assert.Empty(memory.Remembered);
    }

    [Fact]
    public async Task A_turn_she_is_awake_for_is_still_remembered()
    {
        RecordingMemory memory = new();

        await using LaneHarness harness = Harness(new StubEnergy(), configure: services =>
            services.AddSingleton<IMemoryService>(memory));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "still here");

        await channel.WaitForAsync(1);

        Assert.NotEmpty(memory.Remembered);
    }

    [Theory]
    [InlineData("Lane, wake up", true)]
    [InlineData("hey lane are you there", true)]
    [InlineData("I think Lane's asleep", true)]
    [InlineData("(LANE)", true)]
    [InlineData("Laney said so", false)]
    [InlineData("we planed it yesterday", false)]
    [InlineData("use the multi-lane road", true)]
    [InlineData("nobody is talking to her", false)]
    public async Task Her_name_is_matched_on_word_boundaries(string text, bool getsThrough)
    {
        // A hyphen is a word boundary, so "multi-lane" does reach her. Worth pinning down as a
        // known cost of the simple rule rather than discovering it in a channel about roads.
        await using LaneHarness harness = Harness(new StubEnergy { Asleep = true });

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", text);

        if (getsThrough) await channel.WaitForAsync(1);
        else             await channel.QuietAsync();

        Assert.Equal(getsThrough ? 1 : 0, channel.Sent.Count);
    }

    [Fact]
    public async Task Saying_her_name_while_she_has_something_left_wakes_her_properly()
    {
        StubEnergy energy = new() { Asleep = true, WakesFully = true };

        await using LaneHarness harness = Harness(energy);

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "jahan", "Lane, are you up?");

        await channel.WaitForAsync(1);

        Assert.Equal(1, energy.WakeCalls);
        Assert.False(energy.Asleep);
    }

    [Fact]
    public async Task Woken_with_nothing_left_she_answers_once_and_is_told_to_go_back_to_sleep()
    {
        StubEnergy energy = new() { Asleep = true, WakesFully = false };

        await using LaneHarness harness = Harness(energy);

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "jahan", "Lane?");

        await channel.WaitForAsync(1);

        Assert.True(energy.Asleep);

        Assert.Contains(harness.Model.Requests[^1].System,
            block => block.Text.Contains("go back to sleep"));
    }

    [Fact]
    public async Task A_tired_lane_asks_for_a_shorter_reply()
    {
        await using LaneHarness harness = Harness(new StubEnergy { Tier = EnergyTier.Tired });

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "go on then");

        await channel.WaitForAsync(1);

        ModelRequest request = harness.Model.Requests[^1];

        // Half of the 1024 the agent options default to.
        Assert.Equal(512, request.MaxOutputTokens);
    }

    [Fact]
    public async Task A_tired_lane_is_told_she_is_tired()
    {
        await using LaneHarness harness = Harness(new StubEnergy { Tier = EnergyTier.Weary });

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "go on then");

        await channel.WaitForAsync(1);

        Assert.Contains(harness.Model.Requests[^1].System, block => block.Text.Contains("exhausted"));
    }

    [Fact]
    public async Task A_rested_lane_is_told_nothing_at_all()
    {
        // Absent rather than "you feel fine": a sentence about her energy on every rested turn
        // would be a standing instruction to think about it.
        await using LaneHarness harness = Harness(new StubEnergy());

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "go on then");

        await channel.WaitForAsync(1);

        Assert.DoesNotContain(harness.Model.Requests[^1].System,
            block => block.Text.Contains("tired") || block.Text.Contains("exhausted"));
    }

    [Fact]
    public async Task A_tired_lane_gets_fewer_steps_and_fewer_tool_calls()
    {
        await using LaneHarness harness = Harness(new StubEnergy { Tier = EnergyTier.Weary }, configure: services =>
            services.Configure<AgentOptions>(o => o.Budget = new AgentBudget(MaxSteps: 6, MaxToolCalls: 12)));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "someone", "go on then");

        await channel.WaitForAsync(1);

        // The weary tier's ceilings, clamped against the configured ones rather than raising them.
        Assert.Equal(2, new EnergyOptions().Weary.Scale(new AgentBudget(6, 12)).MaxSteps);
        Assert.Equal(2, new EnergyOptions().Weary.Scale(new AgentBudget(6, 12)).MaxToolCalls);
    }

    [Fact]
    public void A_tier_can_only_lower_the_ceilings_it_is_given()
    {
        // A tier configured more generously than the agent budget must not raise it: tiredness
        // is a constraint, and one that could relax a limit would be a way around it.
        AgentBudget budget = new(MaxSteps: 2, MaxToolCalls: 1);

        EnergyTierOptions generous = new(1.0, 99, 99, 1.0, []);

        Assert.Equal(2, generous.Scale(budget).MaxSteps);
        Assert.Equal(1, generous.Scale(budget).MaxToolCalls);
    }

    [Fact]
    public void A_reply_ceiling_never_scales_to_nothing()
    {
        Assert.Equal(128, new EnergyOptions().Weary.Scale(100, floor: 128));
    }

    // ---- tools ------------------------------------------------------------

    private sealed class NamedTool(string name) : Tool<NoArgs>
    {
        protected override string Name => name;
        protected override string Description => "a tool";

        protected override ValueTask<ToolResult> InvokeAsync(NoArgs args, ToolContext c, CancellationToken ct) =>
            ValueTask.FromResult(ToolResult.Ok("done"));
    }

    private sealed class FixedSource(params ITool[] tools) : IToolSource
    {
        public string SourceId => "test";
        public event Action<string>? ToolsChanged { add { } remove { } }

        public ValueTask<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<ITool>>(tools);
    }

    private static ToolRegistry Registry(EnergyOptions energy, params ITool[] tools) =>
        new([new FixedSource(tools)],
            Microsoft.Extensions.Options.Options.Create(new ToolOptions()),
            NullLogger<ToolRegistry>.Instance,
            null,
            Microsoft.Extensions.Options.Options.Create(energy));

    [Fact]
    public async Task The_advertised_tool_list_does_not_change_when_she_gets_tired()
    {
        // The prompt-cache decision, pinned. The advertised names and schemas are hashed into
        // the cache lineage, so hiding a tool would invalidate the whole cached prefix behind
        // it — a cache miss bought in exchange for saving tokens, and one that would flap
        // every time she crossed a threshold.
        ToolRegistry registry = Registry(
            Options(o => o.Weary = new EnergyTierOptions(0.25, 2, 2, 4.0, ["web_search"])),
            new NamedTool("web_search"), new NamedTool("read_note"));

        ToolSet rested = await registry.ResolveAsync(new ToolScope(null, TurnKind.Respond), default);
        ToolSet weary  = await registry.ResolveAsync(
            new ToolScope(null, TurnKind.Respond, null, EnergyTier.Weary), default);

        Assert.Equal(
            rested.Descriptors.Select(d => d.Name),
            weary.Descriptors.Select(d => d.Name));

        // And the fingerprint with it, since that is what actually reaches the provider.
        Assert.Equal(rested.Fingerprint, weary.Fingerprint);
    }

    [Fact]
    public async Task An_expensive_tool_is_refused_while_tired_rather_than_hidden()
    {
        ToolRegistry registry = Registry(
            Options(o => o.Weary = new EnergyTierOptions(0.25, 2, 2, 4.0, ["web_*"])),
            new NamedTool("web_search"), new NamedTool("read_note"));

        ToolResult refused = await registry.InvokeAsync(
            "web_search", "c1", JsonSerializer.SerializeToElement(new { }),
            Context(EnergyTier.Weary), default);

        Assert.True(refused.IsError);
        Assert.Contains("too tired", refused.Text);

        // Not a blanket ban: only what the tier names.
        ToolResult allowed = await registry.InvokeAsync(
            "read_note", "c2", JsonSerializer.SerializeToElement(new { }),
            Context(EnergyTier.Weary), default);

        Assert.False(allowed.IsError);
    }

    [Fact]
    public async Task A_rested_lane_may_use_everything()
    {
        ToolRegistry registry = Registry(
            Options(o => o.Weary = new EnergyTierOptions(0.25, 2, 2, 4.0, ["web_*"])),
            new NamedTool("web_search"));

        ToolResult result = await registry.InvokeAsync(
            "web_search", "c1", JsonSerializer.SerializeToElement(new { }),
            Context(EnergyTier.Rested), default);

        Assert.False(result.IsError);
    }

    private static ToolContext Context(EnergyTier tier) => new()
    {
        Services = new ServiceCollection().BuildServiceProvider(),
        Turn     = TurnKind.Respond,
        Energy   = tier
    };

    // ---- the whole thing wired together ------------------------------------

    [Fact]
    public async Task She_talks_herself_to_sleep_and_only_her_name_gets_through()
    {
        // The real service, the real store and real turns: what the stubs above cannot show is
        // that a reply's own cost comes back round through the event bus and lands on the
        // number that decides whether the next one happens.
        ServiceCollection services = new();

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddLaneCore();
        services.AddLaneMemory(new SqliteOptions { InMemory = true }, o => o.Handlers = []);

        services.Configure<SessionOptions>(o =>
        {
            o.BatchWindow      = TimeSpan.Zero;
            o.VoiceBatchWindow = TimeSpan.Zero;
        });

        // Two turns' worth: the scripted model charges ten in and a few out per call.
        services.AddLaneEnergy(o =>
        {
            o.Enabled         = true;
            o.Budget          = 24;
            o.Window          = TimeSpan.FromHours(24);
            o.AccrualInterval = TimeSpan.FromMinutes(10);
        });

        ScriptedLanguageModel scripted = ScriptedLanguageModel.Echoing("mm");

        services.AddSingleton<ILanguageModel>(sp =>
            new TelemetryLanguageModel(scripted, sp.GetRequiredService<IEventBus>()));

        services.AddSingleton<ILanguageModelRegistry>(sp => new LanguageModelRegistry(
            sp.GetServices<ILanguageModel>(),
            new Dictionary<string, string> { ["respond"] = "scripted" }));

        await using ServiceProvider provider = services.BuildServiceProvider();

        EnergyService energy = provider.GetRequiredService<EnergyService>();
        await energy.StartAsync(default);

        await using LaneHarness harness = LaneHarness.Wrap(provider, scripted);

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        // Awake, and each reply costs her.
        await harness.SendAsync(channel, "someone", "one");
        await channel.WaitForAsync(1);

        Assert.True(energy.Current.Remaining < 24);

        // Keep going until the budget is gone, one turn at a time. She is awake at the top of
        // each pass, so a reply is owed — waiting for it is what keeps the assertions below
        // from racing the tail of this loop. Bounded, so a bug that never spends fails the
        // test rather than hanging it.
        for (int i = 0; i < 10 && !energy.Current.Asleep; i++)
        {
            int before = channel.Sent.Count;

            await harness.SendAsync(channel, "someone", $"message {i}");

            await channel.WaitForAsync(before + 1);
        }

        Assert.True(energy.Current.Asleep, "she never ran out of energy");

        int spokenBefore = channel.Sent.Count;

        // Asleep: ordinary chatter does not reach her. Not QuietAsync, which asserts the
        // channel has never carried anything — by now it has carried the replies that wore her
        // out. Same idea, against a baseline: long enough that a reply on its way would have
        // landed.
        await harness.SendAsync(channel, "someone", "still going on about it");
        await Task.Delay(750);

        Assert.Equal(spokenBefore, channel.Sent.Count);

        // Her name does, even with nothing left — and leaves her asleep afterwards.
        await harness.SendAsync(channel, "jahan", "Lane, are you there?");
        await channel.WaitForAsync(spokenBefore + 1);

        Assert.Equal(spokenBefore + 1, channel.Sent.Count);
        Assert.True(energy.Current.Asleep, "answering once should not have woken her");

        await energy.StopAsync(default);
    }

    // ---- the shipped configuration -----------------------------------------

    [Fact]
    public void The_shipped_configuration_binds_to_what_the_code_expects()
    {
        // A whole day is "1.00:00:00": TimeSpan rejects an hours component of 24, and the
        // difference between the two is a host that will not start. Worth pinning here rather
        // than finding out on a deploy, along with the nested tier objects, which bind by
        // property name and would silently keep their defaults if one were misspelled.
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        EnergyOptions options = Lane.Host.LaneHostBuilderExtensions
            .ReadEnergyOptions(configuration.GetSection("Lane:Energy"));

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromHours(24), options.Window);
        Assert.Equal(TimeSpan.FromMinutes(10), options.AccrualInterval);
        Assert.Equal(144, options.StepsPerWindow);

        Assert.Equal(0.7, options.TiredBelow);
        Assert.Equal(["Lane"], options.WakeWords);

        // The nested objects, which are the part that would fail quietly.
        Assert.Equal(0.5, options.Tired.OutputScale);
        Assert.Equal(["web_search"], options.Tired.DenyTools);
        Assert.Equal(4.0, options.Weary.MonologueScale);
        Assert.Contains("read_book", options.Weary.DenyTools);
    }

    [Fact]
    public void A_configured_list_replaces_the_default_rather_than_joining_it()
    {
        // Bind appends. Without the clear-and-restore in ReadEnergyOptions, an operator who
        // writes "DenyTools": [] to let a tired Lane keep her tools would get the code default
        // anyway, and every configured entry would arrive twice.
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Energy:Enabled"]           = "true",
                ["Energy:WakeWords:0"]       = "Laney",
                ["Energy:Tired:DenyTools:0"] = "read_book"
            })
            .Build();

        EnergyOptions options = Lane.Host.LaneHostBuilderExtensions
            .ReadEnergyOptions(configuration.GetSection("Energy"));

        Assert.Equal(["Laney"], options.WakeWords);
        Assert.Equal(["read_book"], options.Tired.DenyTools);

        // And a list nobody mentioned keeps the default it had.
        Assert.Equal(new EnergyOptions().Weary.DenyTools, options.Weary.DenyTools);
    }

    [Fact]
    public void The_shipped_persona_asks_for_every_slot_the_code_fills()
    {
        // FilePromptLibrary throws on a slot with no value supplied, so a template that gains
        // one before the code does takes every reply down. The pairing is worth asserting.
        string persona = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "Persona.md"));

        Assert.Contains("{{energy}}", persona);
        Assert.Contains("{{mood}}", persona);
    }

    // ---- shared recorders --------------------------------------------------

    private sealed class RecordingTranscript : ITranscriptStore
    {
        private readonly ConcurrentQueue<LaneMessage> _written = new();

        public IReadOnlyList<LaneMessage> Written => [.. _written];

        public ValueTask<long> AppendAsync(LaneMessage message, CancellationToken ct)
        {
            _written.Enqueue(message);
            return ValueTask.FromResult((long)_written.Count);
        }

        public ValueTask<IReadOnlyList<LaneMessage>> ReadAsync(TranscriptQuery query, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<LaneMessage>>([]);
    }

    private sealed class RecordingMemory : IMemoryService
    {
        private readonly ConcurrentQueue<LaneMessage> _remembered = new();

        public IReadOnlyList<LaneMessage> Remembered => [.. _remembered];

        public ValueTask RememberAsync(LaneMessage message, MemoryContext ctx, CancellationToken ct)
        {
            _remembered.Enqueue(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask<AssembledContext> AssembleAsync(MemoryContext ctx, LaneMessage? cue, CancellationToken ct) =>
            ValueTask.FromResult(AssembledContext.Empty);

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
