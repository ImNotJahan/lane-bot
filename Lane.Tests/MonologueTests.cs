using System.Text.Json;
using Lane.Core;
using Lane.Core.Energy;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Monologue;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Memory;
using Lane.Memory.Sqlite;
using Lane.Testing;
using Lane.Tools;
using Lane.Tools.Monologue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Lane's inner life: one loop for all of her, able to volunteer a remark into a named
/// conversation without cutting across whatever is already happening there.
/// </summary>
public sealed class MonologueTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private ServiceProvider? _services;

    private static MemoryHandlerOptions Thoughts() => new()
    {
        Id = "thoughts", Type = "SlidingWindow", Scope = MemoryScope.Global,
        Slot = MemorySlot.Volatile, MaxMessages = 10, Pinned = true,
        SectionTitle = "Recent thoughts", Kinds = [MessageKind.Thought]
    };

    private static MemoryHandlerOptions Recent() => new()
    {
        Id = "recent", Type = "SlidingWindow", Scope = MemoryScope.Session,
        Slot = MemorySlot.Inline, MaxMessages = 20, Kinds = [MessageKind.Utterance]
    };

    /// <summary>Builds a kernel with the monologue wired but not yet started.</summary>
    private MonologueService Build(
        ScriptedLanguageModel model,
        out LaneHarness harness,
        Action<MonologueOptions>? configure = null,
        Action<ServiceCollection>? register = null)
    {
        ServiceCollection services = new();

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddLaneCore();
        services.AddLaneMemory(new SqliteOptions { InMemory = true },
            o => o.Handlers = [Recent(), Thoughts()]);

        services.Configure<SessionOptions>(o =>
        {
            o.BatchWindow = TimeSpan.Zero;
            o.VoiceBatchWindow = TimeSpan.Zero;
        });

        services.AddSingleton<ILanguageModel>(model);
        services.AddSingleton<ILanguageModelRegistry>(sp => new LanguageModelRegistry(
            sp.GetServices<ILanguageModel>(),
            new Dictionary<string, string> { ["respond"] = "scripted", ["monologue"] = "scripted" }));

        // The real registration, so every built-in tool has what it needs to construct.
        services.AddLaneBuiltinTools(new ToolsSetupOptions());

        services.AddLaneMonologue(o =>
        {
            o.StartupDelay = TimeSpan.FromMilliseconds(50);
            o.Interval     = TimeSpan.FromSeconds(30);
            o.MinInterval  = TimeSpan.FromMilliseconds(10);
            configure?.Invoke(o);
        });

        register?.Invoke(services);

        _services = services.BuildServiceProvider();

        harness = LaneHarness.Wrap(_services, model);

        return _services.GetRequiredService<MonologueService>();
    }

    private static async Task<T> Eventually<T>(Func<T?> read, TimeSpan timeout) where T : class
    {
        using CancellationTokenSource cts = new(timeout);

        while (true)
        {
            if (read() is { } value) return value;

            if (cts.IsCancellationRequested) throw new TimeoutException("condition was never met");

            await Task.Delay(15, CancellationToken.None);
        }
    }

    // ---- thinking ----------------------------------------------------------

    [Fact]
    public async Task A_thought_is_the_models_own_words_and_is_remembered()
    {
        // v2 asked for a JSON object and parsed a "thought" field back out of it.
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("the tide pool is probably empty by now");

        MonologueService monologue = Build(model, out LaneHarness harness);

        List<ThoughtHad> thoughts = [];
        harness.Services.GetRequiredService<IEventBus>().Subscribe<ThoughtHad>(thoughts.Add);

        await monologue.StartAsync(CancellationToken.None);

        ThoughtHad had = await Eventually(() => thoughts.FirstOrDefault(), Timeout);

        Assert.Equal("the tide pool is probably empty by now", had.Thought);
        Assert.False(had.Spoke);

        await monologue.StopAsync(CancellationToken.None);

        // Stored as a thought, not an utterance, so it lands in the thought window only.
        IReadOnlyList<LaneMessage> logged = await harness.Services
            .GetRequiredService<ITranscriptStore>()
            .ReadAsync(new TranscriptQuery { Limit = 10 }, default);

        Assert.Contains(logged, m => m.Kind == MessageKind.Thought && m.TextContent.Contains("tide pool"));
    }

    [Fact]
    public async Task Thinking_is_global_and_belongs_to_no_conversation()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("just musing");

        MonologueService monologue = Build(model, out LaneHarness harness);

        await monologue.StartAsync(CancellationToken.None);
        await Eventually(() => monologue.Status.LastThought, Timeout);
        await monologue.StopAsync(CancellationToken.None);

        IReadOnlyList<LaneMessage> logged = await harness.Services
            .GetRequiredService<ITranscriptStore>()
            .ReadAsync(new TranscriptQuery { Limit = 10 }, default);

        LaneMessage[] thoughts = [.. logged.Where(m => m.Kind == MessageKind.Thought)];

        // How many cycles ran before the service stopped is timing, not behaviour. What
        // matters is that a thought belongs to nobody's conversation.
        Assert.NotEmpty(thoughts);
        Assert.All(thoughts, t => Assert.Null(t.Session));
    }

    // ---- speaking ----------------------------------------------------------

    [Fact]
    public async Task She_can_volunteer_a_remark_into_a_named_conversation()
    {
        // The milestone: a thought reaching one specific channel, chosen by her.
        ScriptedLanguageModel model = new((_, call) => call == 0
            ? ScriptedLanguageModel.ToolCall("speak_to_session",
                new { sessionId = "discord.main/Text/general", message = "I keep thinking about cuttlefish." }, "c1")
            : ScriptedLanguageModel.Text("said it"));

        MonologueService monologue = Build(model, out LaneHarness harness);

        RecordingChannel general  = harness.OpenSession("discord.main", "general", memoryGroup: "m/general");
        RecordingChannel offtopic = harness.OpenSession("discord.main", "offtopic", memoryGroup: "m/offtopic");

        await monologue.StartAsync(CancellationToken.None);

        Assert.Equal(["I keep thinking about cuttlefish."], await general.WaitForAsync(1, Timeout));

        // Only the conversation she named. This is the cohesion guarantee applied to her
        // own initiative rather than to someone else's message.
        await Task.Delay(200);
        Assert.Empty(offtopic.Texts);

        await monologue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Speaking_to_a_conversation_that_is_not_open_is_refused_not_guessed()
    {
        ScriptedLanguageModel model = new((_, call) => call == 0
            ? ScriptedLanguageModel.ToolCall("speak_to_session",
                new { sessionId = "discord.main/Text/nowhere", message = "hello?" }, "c1")
            : ScriptedLanguageModel.Text("never mind"));

        MonologueService monologue = Build(model, out LaneHarness harness);

        RecordingChannel open = harness.OpenSession("discord.main", "general", memoryGroup: "m/general");

        await monologue.StartAsync(CancellationToken.None);
        await Eventually(() => monologue.Status.LastThought, Timeout);
        await monologue.StopAsync(CancellationToken.None);

        // Never redirected to whichever channel happened to be handy.
        Assert.Empty(open.Texts);
    }

    // ---- scheduling --------------------------------------------------------

    [Fact]
    public async Task She_can_decide_when_to_think_next()
    {
        ScriptedLanguageModel model = new((_, call) => call == 0
            ? ScriptedLanguageModel.ToolCall("schedule_next_thought", new { seconds = 600, reason = "nothing doing" }, "c1")
            : ScriptedLanguageModel.Text("later then"));

        MonologueService monologue = Build(model, out _);

        await monologue.StartAsync(CancellationToken.None);
        await Eventually(() => monologue.Status.LastThought, Timeout);

        DateTimeOffset next = await Eventually(
            () => monologue.Status.NextThoughtAt is { } at && at > DateTimeOffset.UtcNow.AddMinutes(5)
                ? (object)at : null, Timeout) is DateTimeOffset d ? d : default;

        Assert.True(next > DateTimeOffset.UtcNow.AddMinutes(5));

        await monologue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void An_absurd_schedule_is_clamped_rather_than_obeyed()
    {
        // A model asking to think every second would otherwise be a billing incident.
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("hm");

        MonologueService monologue = Build(model, out _, o =>
        {
            o.MinInterval = TimeSpan.FromSeconds(30);
            o.MaxInterval = TimeSpan.FromMinutes(10);
        });

        monologue.Schedule(TimeSpan.FromMilliseconds(1), "far too soon");
        Assert.True(monologue.Status.NextThoughtAt >= DateTimeOffset.UtcNow.AddSeconds(25));

        monologue.Schedule(TimeSpan.FromDays(7), "far too late");
        Assert.True(monologue.Status.NextThoughtAt <= DateTimeOffset.UtcNow.AddMinutes(11));
    }

    [Fact]
    public void Someone_talking_pushes_the_next_thought_back_rather_than_pulling_it_in()
    {
        // Thinking the instant a conversation starts means interrupting it.
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("hm");

        MonologueService monologue = Build(model, out _, o => o.AfterMessage = TimeSpan.FromMinutes(2));

        monologue.Schedule(TimeSpan.FromSeconds(20), "soon");
        DateTimeOffset before = monologue.Status.NextThoughtAt!.Value;

        monologue.Interrupt("someone is talking");

        Assert.True(monologue.Status.NextThoughtAt > before);
    }

    [Fact]
    public void An_interrupt_never_delays_a_thought_that_was_already_further_out()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("hm");

        MonologueService monologue = Build(model, out _, o => o.AfterMessage = TimeSpan.FromSeconds(30));

        monologue.Schedule(TimeSpan.FromMinutes(20), "much later");
        DateTimeOffset planned = monologue.Status.NextThoughtAt!.Value;

        monologue.Interrupt("someone is talking");

        Assert.Equal(planned, monologue.Status.NextThoughtAt);
    }

    [Fact]
    public async Task The_schedule_is_announced_so_the_dashboard_can_count_down()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("hm");

        MonologueService monologue = Build(model, out LaneHarness harness);

        List<MonologueTick> ticks = [];
        harness.Services.GetRequiredService<IEventBus>().Subscribe<MonologueTick>(ticks.Add);

        monologue.Schedule(TimeSpan.FromMinutes(1), "testing");

        await Task.Yield();

        MonologueTick tick = Assert.Single(ticks);
        Assert.Equal("testing", tick.Reason);
        Assert.NotNull(tick.NextThoughtAt);
    }

    // ---- gating ------------------------------------------------------------

    [Fact]
    public async Task Monologue_tools_are_not_offered_while_replying_to_someone()
    {
        // speak_to_session during a reply would let her answer in a different conversation
        // than the one she was addressed in.
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("hello");

        Build(model, out LaneHarness harness);

        RecordingChannel channel = harness.OpenSession("discord.main", "general", memoryGroup: "m/general");

        await harness.SendAsync(channel, "alice", "hi");
        await channel.WaitForAsync(1, Timeout);

        string[] offered = [.. harness.Model.Requests[^1].Tools.Select(t => t.Name)];

        Assert.DoesNotContain("speak_to_session", offered);
        Assert.DoesNotContain("schedule_next_thought", offered);
        Assert.DoesNotContain("list_sessions", offered);
    }

    [Fact]
    public async Task Monologue_tools_are_offered_while_thinking()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("hm");

        MonologueService monologue = Build(model, out LaneHarness harness);

        await monologue.StartAsync(CancellationToken.None);
        await Eventually(() => monologue.Status.LastThought, Timeout);
        await monologue.StopAsync(CancellationToken.None);

        string[] offered = [.. harness.Model.Requests[0].Tools.Select(t => t.Name)];

        Assert.Contains("speak_to_session", offered);
        Assert.Contains("schedule_next_thought", offered);
        Assert.Contains("list_sessions", offered);

        // The everyday tools are still hers while thinking.
        Assert.Contains("web_search", offered);
    }

    [Fact]
    public async Task A_failed_thought_does_not_end_her_inner_life()
    {
        // v2 restarted the whole loop after ten seconds; here the schedule absorbs it.
        ScriptedLanguageModel model = new((_, call) => call == 0
            ? throw new InvalidOperationException("provider fell over")
            : ScriptedLanguageModel.Text("recovered"));

        MonologueService monologue = Build(model, out _, o => o.Interval = TimeSpan.FromMilliseconds(80));

        await monologue.StartAsync(CancellationToken.None);

        string thought = await Eventually(() => monologue.Status.LastThought, Timeout);

        Assert.Equal("recovered", thought);

        await monologue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task She_sees_which_conversations_are_open()
    {
        // Without the world view, speak_to_session has nothing to name.
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("hm");

        MonologueService monologue = Build(model, out LaneHarness harness);

        harness.OpenSession("discord.main", "general", memoryGroup: "m/general");

        await monologue.StartAsync(CancellationToken.None);
        await Eventually(() => monologue.Status.LastThought, Timeout);
        await monologue.StopAsync(CancellationToken.None);

        string system = string.Join("\n", harness.Model.Requests[0].System.Select(s => s.Text));

        Assert.Contains("discord.main/Text/general", system);
    }

    // ---- tiredness ---------------------------------------------------------

    /// <summary>A metabolism a test can set directly, rather than one it has to exhaust.</summary>
    private sealed class StubEnergy(EnergyTier tier, bool asleep) : IEnergyService
    {
        public EnergyState Current => new(asleep ? 0 : 100, 100, asleep, default) { Tier = tier };

        public void Spend(TokenUsage usage, string? role) { }

        public bool Wake(string reason) => !asleep;

        public TimeSpan? TimeUntilRested => asleep ? TimeSpan.FromHours(1) : null;
    }

    [Fact]
    public async Task She_does_not_think_while_she_is_asleep()
    {
        // No thoughts, and no bill for having them. The loop keeps running, which is why the
        // tick says "asleep" rather than the schedule simply going quiet.
        MonologueService monologue = Build(
            ScriptedLanguageModel.Echoing("something she would have thought"),
            out LaneHarness harness,
            register: services => services.AddSingleton<IEnergyService>(
                new StubEnergy(EnergyTier.Weary, asleep: true)));

        List<MonologueTick> ticks = [];
        harness.Services.GetRequiredService<IEventBus>().Subscribe<MonologueTick>(ticks.Add);

        await monologue.StartAsync(CancellationToken.None);

        await Eventually(() => ticks.FirstOrDefault(t => t.Reason == "asleep"), Timeout);

        await monologue.StopAsync(CancellationToken.None);

        Assert.Null(monologue.Status.LastThought);
        Assert.Equal(0, harness.Model.CallCount);
    }

    [Fact]
    public async Task Being_tired_spreads_her_thoughts_further_apart()
    {
        MonologueService monologue = Build(
            ScriptedLanguageModel.Echoing("mm"),
            out LaneHarness harness,
            configure: o =>
            {
                o.Interval    = TimeSpan.FromSeconds(30);
                o.MaxInterval = TimeSpan.FromHours(2);
            },
            register: services => services.AddSingleton<IEnergyService>(
                new StubEnergy(EnergyTier.Weary, asleep: false)));

        List<MonologueTick> ticks = [];
        harness.Services.GetRequiredService<IEventBus>().Subscribe<MonologueTick>(ticks.Add);

        await monologue.StartAsync(CancellationToken.None);

        MonologueTick cadence = await Eventually(
            () => ticks.FirstOrDefault(t => t.Reason == "default cadence"), Timeout);

        await monologue.StopAsync(CancellationToken.None);

        // Four times the thirty-second cadence, so comfortably past a minute out.
        Assert.NotNull(cadence.NextThoughtAt);
        Assert.True(cadence.NextThoughtAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(90),
            $"expected the cadence to be stretched, but the next thought is at {cadence.NextThoughtAt}");
    }

    [Fact]
    public async Task A_stretched_cadence_is_still_clamped_by_the_maximum()
    {
        // Tiredness slows her down; it does not get to override the ceiling on how long she
        // may go without a thought.
        MonologueService monologue = Build(
            ScriptedLanguageModel.Echoing("mm"),
            out LaneHarness harness,
            configure: o =>
            {
                o.Interval    = TimeSpan.FromMinutes(10);
                o.MaxInterval = TimeSpan.FromMinutes(12);
            },
            register: services => services.AddSingleton<IEnergyService>(
                new StubEnergy(EnergyTier.Weary, asleep: false)));

        List<MonologueTick> ticks = [];
        harness.Services.GetRequiredService<IEventBus>().Subscribe<MonologueTick>(ticks.Add);

        await monologue.StartAsync(CancellationToken.None);

        MonologueTick cadence = await Eventually(
            () => ticks.FirstOrDefault(t => t.Reason == "default cadence"), Timeout);

        await monologue.StopAsync(CancellationToken.None);

        // Forty minutes stretched, twelve allowed.
        Assert.NotNull(cadence.NextThoughtAt);
        Assert.True(cadence.NextThoughtAt - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(12),
            $"MaxInterval was not applied; the next thought is at {cadence.NextThoughtAt}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
    }
}

public sealed class MonologueToolTests
{
    [Fact]
    public async Task Listing_sessions_reports_what_is_open()
    {
        await using LaneHarness harness = LaneHarness.Create();

        harness.OpenSession("discord.main", "general", memoryGroup: "m/general");
        harness.OpenSession("terminal", "local", memoryGroup: "t/local");

        ITool tool = new ListSessionsTool();

        ToolResult result = await tool.InvokeAsync(new ToolInvocation(
            "c1", JsonSerializer.SerializeToElement(new { }),
            new ToolContext
            {
                Turn = TurnKind.Monologue,
                Services = new ServiceCollection().BuildServiceProvider(),
                Sessions = harness.Sessions
            }), default);

        Assert.Contains("discord.main/Text/general", result.Text);
        Assert.Contains("terminal/Text/local", result.Text);
    }

    [Fact]
    public async Task Scheduling_a_thought_needs_a_positive_number()
    {
        ITool tool = new ScheduleNextThoughtTool();

        ToolResult result = await tool.InvokeAsync(new ToolInvocation(
            "c1", JsonSerializer.SerializeToElement(new { seconds = -5 }),
            new ToolContext { Turn = TurnKind.Monologue, Services = new ServiceCollection().BuildServiceProvider() }),
            default);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Speaking_needs_something_to_say()
    {
        ITool tool = new SpeakToSessionTool();

        ToolResult result = await tool.InvokeAsync(new ToolInvocation(
            "c1", JsonSerializer.SerializeToElement(new { sessionId = "terminal/Text/local", message = "   " }),
            new ToolContext { Turn = TurnKind.Monologue, Services = new ServiceCollection().BuildServiceProvider() }),
            default);

        Assert.True(result.IsError);
    }
}
