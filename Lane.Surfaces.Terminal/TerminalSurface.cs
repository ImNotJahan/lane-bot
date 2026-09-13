using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Core.Surfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Terminal;

public sealed class TerminalSurfaceOptions
{
    /// <summary>How the person at the keyboard is identified in memory and in prompts.</summary>
    public string UserName { get; set; } = "you";

    public string LaneName { get; set; } = "Lane";

    /// <summary>Distinguishes several terminal sessions if one is ever run per pane.</summary>
    public string SessionKey { get; set; } = "local";

    /// <summary>
    /// Shut the process down when stdin closes (Ctrl-D, or a piped command finishing).
    /// Worth turning off once Lane is also reachable over Discord or the API, where losing
    /// the keyboard is no reason to hang up on everyone else.
    /// </summary>
    public bool ExitOnEndOfInput { get; set; } = true;

    /// <summary>Write a "> " prompt before each read. Off when a UI shows its own input.</summary>
    public bool ShowPrompt { get; set; } = true;
}

/// <summary>
/// The keyboard as a surface.
///
/// Deliberately no smarter than any other surface: it owns no model and no memory, and it
/// reaches the kernel through exactly the same <c>InboundEvent</c> a Discord message does.
/// That is what lets it run beside Discord and the API in one process rather than being
/// an either/or choice at startup.
/// </summary>
public sealed class TerminalSurface : ISurface
{
    private readonly IAgentKernel     _kernel;
    private readonly ISessionRegistry _sessions;
    private readonly TerminalSurfaceOptions _options;
    private readonly ILogger<TerminalSurface> _log;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly IHostApplicationLifetime? _lifetimeSignal;
    private readonly IIdentityResolver _identity;

    private CancellationTokenSource? _lifetime;
    private Task?                    _loop;
    private IDisposable?             _attachment;
    private SessionId?               _session;

    public TerminalSurface(
        SurfaceId id,
        IAgentKernel kernel,
        ISessionRegistry sessions,
        TerminalSurfaceOptions options,
        ILogger<TerminalSurface> log,
        IHostApplicationLifetime? lifetime = null,
        IIdentityResolver? identity = null,
        TextReader? input = null,
        TextWriter? output = null)
    {
        _identity       = identity ?? IdentityResolver.Empty;
        Id              = id;
        _kernel         = kernel;
        _sessions       = sessions;
        _options        = options;
        _log            = log;
        _lifetimeSignal = lifetime;
        _input          = input  ?? Console.In;
        _output         = output ?? Console.Out;
    }

    public SurfaceId Id { get; }

    public Task StartAsync(CancellationToken ct)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        SessionId id = new(Id, SessionKind.Text, _options.SessionKey);
        _session = id;

        _sessions.GetOrCreate(new SessionDescriptor
        {
            Id           = id,
            DisplayName  = "terminal",
            MemoryGroup  = $"{Id.Value}/{_options.SessionKey}",
            Capabilities = ChannelCapabilities.Text,
            IsDirect     = true,
            KnownParticipants = [User()]
        });

        _attachment = _sessions.Attach(new TerminalChannel(id, _options.LaneName, _output));

        // Console.ReadLine blocks a thread outright, so the read loop gets its own rather
        // than parking a thread-pool worker for the lifetime of the process.
        _loop = Task.Factory.StartNew(
            () => ReadLoopAsync(id, _lifetime.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        _log.LogInformation("Terminal surface {Surface} ready", Id);

        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(SessionId session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_options.ShowPrompt)
            {
                _output.Write("> ");
                _output.Flush();
            }

            string? line = await _input.ReadLineAsync(ct).ConfigureAwait(false);

            if (line is null)                                   // stdin closed (Ctrl-D)
            {
                if (_options.ExitOnEndOfInput && !ct.IsCancellationRequested)
                {
                    _log.LogInformation("Terminal input closed; shutting down");
                    _lifetimeSignal?.StopApplication();
                }

                break;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            Participant author = User();

            await _kernel.SubmitAsync(new InboundEvent
            {
                Session = session,
                Author  = author,
                Message = LaneMessage.User(session, author, line.Trim(), DateTimeOffset.UtcNow)
            }, ct).ConfigureAwait(false);
        }
    }

    private Participant User() =>
        _identity.Resolve(new ParticipantId(Id, _options.UserName), _options.UserName);

    public async Task StopAsync(CancellationToken ct)
    {
        if (_lifetime is not null) await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { /* shutting down */ }
        }

        // Close the session before detaching, not after: closing drains whatever is still
        // queued, and a reply produced during that drain still needs somewhere to go.
        if (_session is not null) await _sessions.CloseAsync(_session, "surface stopped").ConfigureAwait(false);

        _attachment?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime?.Dispose();
    }
}

public sealed class TerminalSurfaceFactory : ISurfaceFactory
{
    public string TypeName => "terminal";

    public ISurface Create(SurfaceId id, IConfiguration options, IServiceProvider services)
    {
        TerminalSurfaceOptions bound = new();
        options.Bind(bound);

        return new TerminalSurface(
            id,
            services.GetRequiredService<IAgentKernel>(),
            services.GetRequiredService<ISessionRegistry>(),
            bound,
            services.GetRequiredService<ILogger<TerminalSurface>>(),
            services.GetService<IHostApplicationLifetime>(),
            services.GetService<IIdentityResolver>());
    }
}
