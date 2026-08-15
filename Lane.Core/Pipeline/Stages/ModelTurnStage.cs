using Lane.Core.Agent;
using Lane.Core.Memory;
using Lane.Core.Tools;
using Lane.Core.Context;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Prompts;
using Lane.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Core.Pipeline.Stages;

/// <summary>
/// Produces Lane's reply, running the agent loop so she can use tools to get there.
/// </summary>
public sealed class ModelTurnStage(
    ILanguageModelRegistry models,
    IToolRegistry          tools,
    AgentLoop              loop,
    IEnumerable<IAgentObserverFactory> observerFactories,
    IPromptLibrary         prompts,
    ITranscriptFormatter   formatter,
    ISessionDescriptions   descriptions,
    IServiceProvider       services,
    IOptions<AgentOptions> options,
    ILogger<ModelTurnStage> log) : ITurnStage
{
    private readonly AgentOptions _options = options.Value;

    public async Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct)
    {
        if (ctx.Suppressed)
        {
            log.LogInformation("Turn suppressed: {Reason}", ctx.SuppressionReason ?? "(unspecified)");
            await next().ConfigureAwait(false);
            return;
        }

        Participant lane = Participant.Lane(ctx.Session.Id.Surface);

        // A directive is text Lane already decided to say — the monologue volunteering
        // something. It still flows through delivery and persistence, but never the model.
        if (ctx.Kind == TurnKind.Directive)
        {
            string text = ctx.DirectiveText ?? "";

            LaneMessage spoken = LaneMessage.Assistant(ctx.Session.Id, lane, text, DateTimeOffset.UtcNow);

            ctx.Result = new TurnResult(text, [spoken], StopReason.EndTurn, default);
            ctx.Produced.Add(spoken);

            await next().ConfigureAwait(false);
            return;
        }

        await RunModelAsync(ctx, lane, ct).ConfigureAwait(false);

        await next().ConfigureAwait(false);
    }

    private async Task RunModelAsync(TurnContext ctx, Participant lane, CancellationToken ct)
    {
        ILanguageModel model = models.Get(ModelRole.Respond, ctx.Descriptor);

        List<LaneMessage> messages = [.. ctx.Context.InlineMessages, .. ctx.Incoming];

        if (messages.Count == 0)
        {
            log.LogDebug("Nothing to respond to in {Session}", ctx.Session.Id);
            return;
        }

        ToolScope scope = new(ctx.Descriptor, ctx.Kind, RequesterOf(ctx));

        ToolSet available = await tools.ResolveAsync(scope, ct).ConfigureAwait(false);

        // A voice conversation gets an observer that speaks as the reply is written; a text
        // one gets nothing and pays nothing. Every factory that offers one is taken, not
        // just the first: an API voice session wants audio *and* text deltas, and picking
        // one would leave the other silent depending on registration order.
        await using IAgentObserver? observer = CompositeAgentObserver.Of(
            [.. observerFactories.Select(f => f.Create(ctx.Session, ctx.Kind)).OfType<IAgentObserver>()], log);

        AgentRunResult result = await loop.RunAsync(new AgentRunRequest
        {
            Model    = model,
            System   = [new PromptBlock(BuildPersona(ctx), CacheHint.Ephemeral), .. ctx.Context.SystemBlocks],
            Messages = messages,
            Tools    = available,
            ToolContext = new ToolContext
            {
                Session    = ctx.Session.Id,
                Descriptor = ctx.Descriptor,
                Requester  = RequesterOf(ctx),
                Turn       = ctx.Kind,
                Memory     = ctx.Items.TryGetValue("memory", out object? m) && m is MemoryContext mc ? mc : new(),
                Services   = services,
                Sessions   = services.GetService<ISessionRegistry>()
            },
            Lane            = lane,
            Session         = ctx.Session.Id,
            Budget          = _options.Budget,
            MaxOutputTokens = _options.MaxOutputTokens,
            Temperature     = _options.Temperature,
            Observer        = observer,
            CacheLineage    = $"respond:{model.Descriptor.InstanceId}"
        }, ct).ConfigureAwait(false);

        ctx.Result = new TurnResult(result.FinalText, result.NewMessages, result.Stop, result.TotalUsage);

        // Everything the run produced is persisted, including the intermediate tool turns:
        // the transcript keeps the full record, and memory strips what it must not replay.
        ctx.Produced.AddRange(result.NewMessages);
        ctx.Produced.AddRange(result.Observations);

        if (result.Cancelled)
        {
            // Persist what happened, but do not say a half-finished thing out loud.
            ctx.Suppressed        = true;
            ctx.SuppressionReason = "turn cancelled";
        }

        if (result.ToolCalls.Count > 0)
            log.LogInformation("Used {Count} tool call(s): {Tools}",
                result.ToolCalls.Count, string.Join(", ", result.ToolCalls.Select(c => c.Name)));
    }

    private static Participant? RequesterOf(TurnContext ctx) =>
        ctx.Incoming.LastOrDefault(m => m.Role == LaneRole.User)?.Author;

    /// <summary>
    /// The persona template, filled with who and where, plus whatever Lane has written down
    /// about this particular conversation.
    /// </summary>
    private string BuildPersona(TurnContext ctx) => Persona(ctx) + Description(ctx);

    /// <summary>
    /// What <c>set_session_description</c> last wrote about this conversation.
    ///
    /// Folded into the persona block rather than added as a block of its own: it belongs to
    /// the same slowly-changing prefix, and a separate block would spend one of the handful
    /// of cache breakpoints a provider allows on a sentence. Appended in code rather than
    /// rendered into the template so a checkout with no template files keeps it too — and so
    /// that a conversation she has said nothing about adds nothing at all, rather than an
    /// empty heading.
    ///
    /// It is labelled as her own note on purpose. The text arrives from a conversation, by
    /// way of a model that was asked to write it, and it lands in the system prompt: saying
    /// where it came from is the difference between a note she wrote and an instruction she
    /// has no way to place.
    /// </summary>
    private string Description(TurnContext ctx)
    {
        if (descriptions.For(ctx.Descriptor) is not { } description) return "";

        return $"\n\nYou have written this down about {ctx.Descriptor.DisplayName}, for yourself, " +
               $"and can change it with set_session_description:\n\n{description}";
    }

    /// <summary>
    /// Falls back to the inline option when no template file is present, so tests and a bare
    /// checkout still run.
    /// </summary>
    private string Persona(TurnContext ctx)
    {
        // The fallback still gets the mood appended. Otherwise a checkout with no template
        // files silently loses the pitch of every reply, which is the sort of difference
        // nobody thinks to look for.
        if (!prompts.Has(_options.PersonaPrompt)) return _options.Persona + Mood(ctx);

        IEnumerable<string> people = ctx.Descriptor.KnownParticipants
            .Where(p => !p.IsLane)
            .Select(p => p.DisplayName);

        // Whoever is actually speaking counts as present even if the roster is stale.
        people = people.Concat(ctx.Incoming.Where(m => m.Role == LaneRole.User).Select(m => m.Author.DisplayName));

        string participants = string.Join(", ", people.Distinct(StringComparer.OrdinalIgnoreCase));

        return prompts.Render(_options.PersonaPrompt,
            ("participants", string.IsNullOrWhiteSpace(participants) ? "someone" : participants),
            ("session", ctx.Descriptor.DisplayName),
            ("mood", Mood(ctx)),
            ("time", formatter.FormatTime(DateTimeOffset.UtcNow,
                new TranscriptFormatOptions(_options.DisplayOffset, IncludeRelativeTime: false))));
    }

    /// <summary>
    /// How much the turn is worth, as the response policy judged it.
    ///
    /// v2 passed this as "high" / "neutral" / "low" and it is the difference between a reply
    /// that was merely permitted and one that is pitched right: a message that scraped past
    /// the threshold should get an acknowledgement, not an essay. Absent when no policy ran.
    /// </summary>
    private static string Mood(TurnContext ctx)
    {
        if (!ctx.Items.TryGetValue("enthusiasm", out object? value) || value is not float score) return "";

        return score switch
        {
            >= 0.8f => " This one interests you; reply properly.",
            >= 0.5f => "",
            _       => " This barely warrants a reply; keep it short and low-key."
        };
    }
}
