using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Models;
using Lane.Core.Sessions;
using Lane.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class EventBusTests
{
    private static readonly SessionId Session =
        new(new SurfaceId("terminal"), SessionKind.Text, "local");

    [Fact]
    public void Several_subscribers_all_see_the_same_event()
    {
        // The property that matters: v2 wired the emoticon straight into one HTTP server,
        // so exactly one listener could ever exist. The face, the dashboard and the API's
        // event stream now subscribe independently.
        EventBus bus = new();

        List<string> first = [];
        List<string> second = [];

        bus.Subscribe<TokenUsageEvent>(e => first.Add(e.ModelInstanceId));
        bus.Subscribe<TokenUsageEvent>(e => second.Add(e.ModelInstanceId));

        bus.Publish(new TokenUsageEvent("haiku", "respond", default));

        Assert.Equal(["haiku"], first);
        Assert.Equal(["haiku"], second);
    }

    [Fact]
    public void Events_are_delivered_only_to_subscribers_of_that_type()
    {
        EventBus bus = new();

        int usage = 0, turns = 0;

        bus.Subscribe<TokenUsageEvent>(_ => usage++);
        bus.Subscribe<TurnStarted>(_ => turns++);

        bus.Publish(new TokenUsageEvent("haiku", null, default));

        Assert.Equal(1, usage);
        Assert.Equal(0, turns);
    }

    [Fact]
    public void Unsubscribing_stops_delivery()
    {
        EventBus bus = new();

        int seen = 0;
        IDisposable token = bus.Subscribe<TurnStarted>(_ => seen++);

        bus.Publish(new TurnStarted(Session, TurnKind.Respond, 1));
        token.Dispose();
        bus.Publish(new TurnStarted(Session, TurnKind.Respond, 1));

        Assert.Equal(1, seen);
    }

    [Fact]
    public void One_throwing_subscriber_does_not_stop_the_others()
    {
        // The dashboard is a subscriber. A drawing bug in it must not silence telemetry.
        EventBus bus = new(NullLogger<EventBus>.Instance);

        bool reached = false;

        bus.Subscribe<TurnStarted>(_ => throw new InvalidOperationException("bad subscriber"));
        bus.Subscribe<TurnStarted>(_ => reached = true);

        bus.Publish(new TurnStarted(Session, TurnKind.Respond, 1));

        Assert.True(reached);
    }

    [Fact]
    public void A_subscriber_that_publishes_while_handling_does_not_deadlock()
    {
        // The dashboard does exactly this kind of thing when a redraw logs something.
        EventBus bus = new();

        List<string> order = [];

        bus.Subscribe<TurnStarted>(_ =>
        {
            order.Add("outer");
            bus.Publish(new TurnFailed(Session, "inner"));
        });

        bus.Subscribe<TurnFailed>(e => order.Add(e.Error));

        bus.Publish(new TurnStarted(Session, TurnKind.Respond, 1));

        Assert.Equal(["outer", "inner"], order);
    }

    [Fact]
    public void Publishing_with_nobody_listening_is_free_and_harmless()
    {
        EventBus bus = new();

        bus.Publish(new TurnStarted(Session, TurnKind.Respond, 1));
    }

    [Fact]
    public async Task Every_model_call_reports_its_usage_without_the_adapter_knowing()
    {
        // A decorator rather than a change to each adapter, so a provider written later
        // gets telemetry for free.
        EventBus bus = new();

        List<TokenUsageEvent> seen = [];
        bus.Subscribe<TokenUsageEvent>(seen.Add);

        ILanguageModel model = new TelemetryLanguageModel(
            ScriptedLanguageModel.Echoing("hi", "haiku"), bus, "respond");

        await model.CompleteAsync(new ModelRequest { System = [], Messages = [] }, default);

        TokenUsageEvent evt = Assert.Single(seen);

        Assert.Equal("haiku", evt.ModelInstanceId);
        Assert.Equal("respond", evt.Role);
        Assert.True(evt.Usage.Output > 0);
    }
}

public sealed class ModelRegistryTests
{
    private static ScriptedLanguageModel Model(string id, ModelCapabilities capabilities) =>
        new((_, _) => ScriptedLanguageModel.Text("ok"), id, capabilities);

    [Fact]
    public void Roles_resolve_to_their_bound_instances()
    {
        // Matching v2's arrangement: a cheap model for the inner loop, a better one to reply.
        LanguageModelRegistry registry = new(
            [Model("haiku", ModelCapabilities.Tools), Model("ds", ModelCapabilities.Tools)],
            new Dictionary<string, string>
            {
                ["respond"] = "haiku", ["monologue"] = "ds", ["routing"] = "haiku", ["summarize"] = "haiku"
            });

        Assert.Equal("haiku", registry.Get(ModelRole.Respond).Descriptor.InstanceId);
        Assert.Equal("ds",    registry.Get(ModelRole.Monologue).Descriptor.InstanceId);
    }

    [Fact]
    public void A_surface_can_override_the_model_for_a_role()
    {
        LanguageModelRegistry registry = new(
            [Model("haiku", ModelCapabilities.Tools), Model("ds", ModelCapabilities.Tools)],
            new Dictionary<string, string> { ["respond"] = "ds" },
            new Dictionary<(string, string), string> { [("api", "respond")] = "haiku" });

        SessionDescriptor api = new()
        {
            Id = new SessionId(new SurfaceId("api"), SessionKind.Api, "client-1"),
            DisplayName = "api", MemoryGroup = "api/client-1"
        };

        SessionDescriptor discord = new()
        {
            Id = new SessionId(new SurfaceId("discord.main"), SessionKind.Text, "general"),
            DisplayName = "#general", MemoryGroup = "g/general"
        };

        Assert.Equal("haiku", registry.Get(ModelRole.Respond, api).Descriptor.InstanceId);
        Assert.Equal("ds",    registry.Get(ModelRole.Respond, discord).Descriptor.InstanceId);
    }

    [Fact]
    public void A_role_bound_to_an_unknown_instance_fails_at_construction()
    {
        // Config mistakes surface at startup, named, rather than on the first message.
        ModelBindingException error = Assert.Throws<ModelBindingException>(() => new LanguageModelRegistry(
            [Model("haiku", ModelCapabilities.Tools)],
            new Dictionary<string, string> { ["respond"] = "typo" }));

        Assert.Contains("typo", error.Message);
        Assert.Contains("haiku", error.Message);
    }

    [Fact]
    public void A_tool_using_role_on_a_model_without_tools_is_refused_loudly()
    {
        // With no prompt-JSON fallback this has to be caught at startup: OpenRouter fronts
        // plenty of models that simply cannot call tools.
        LanguageModelRegistry registry = new(
            [Model("weak", ModelCapabilities.Streaming)],
            new Dictionary<string, string> { ["respond"] = "weak" });

        ModelBindingException error = Assert.Throws<ModelBindingException>(
            () => registry.ValidateCapabilities([ModelRole.Respond]));

        Assert.Contains("respond", error.Message);
        Assert.Contains("does not support tool calling", error.Message);
    }

    [Fact]
    public void Validation_passes_when_every_tool_using_role_can_call_tools()
    {
        LanguageModelRegistry registry = new(
            [Model("haiku", ModelCapabilities.Tools)],
            new Dictionary<string, string> { ["respond"] = "haiku" });

        // An unbound role is not an error — the monologue does not exist yet.
        registry.ValidateCapabilities([ModelRole.Respond, ModelRole.Monologue]);
    }
}
