using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Lane.Audio;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Memory;
using Lane.Core.Sessions;
using Lane.Core.Surfaces;
using Lane.Surfaces.Api.Endpoints;
using Lane.Surfaces.Api.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Api;

/// <summary>
/// Lane over HTTP.
///
/// The point of this surface is that there is nothing special about it. It owns no model, no
/// memory and no tools; it turns requests into <c>InboundEvent</c>s and attaches channels,
/// exactly as the Discord and terminal surfaces do. What it adds is that several unrelated
/// client applications can each hold their own conversation at once — namespaced by the key
/// they authenticate with — while Discord and the terminal are live in the same process.
///
/// Kestrel runs in its own small container. That is not isolation for its own sake: the
/// kernel's container must never acquire a web server's worth of services, and the pieces
/// this surface genuinely shares — the kernel, the registry, the transcript, the stream hub —
/// are handed across explicitly, so what is shared is visible in one place.
/// </summary>
public sealed class ApiSurface : ISurface
{
    private readonly ApiSurfaceOptions   _options;
    private readonly ApiClientRegistry   _clients;
    private readonly ApiSessionMap       _map;
    private readonly IAgentKernel        _kernel;
    private readonly ISessionRegistry    _sessions;
    private readonly ITranscriptStore    _transcript;
    private readonly IEventBus           _bus;
    private readonly TurnStreamHub       _hub;
    private readonly AudioRouter?        _router;
    private readonly ILoggerFactory      _loggers;
    private readonly ILogger<ApiSurface> _log;

    private WebApplication? _app;

    public ApiSurface(
        SurfaceId id,
        ApiSurfaceOptions options,
        IAgentKernel kernel,
        ISessionRegistry sessions,
        IIdentityResolver identity,
        ITranscriptStore transcript,
        IEventBus bus,
        TurnStreamHub hub,
        ILoggerFactory loggers,
        AudioRouter? router = null)
    {
        Id          = id;
        _options    = options;
        _kernel     = kernel;
        _sessions   = sessions;
        _transcript = transcript;
        _bus        = bus;
        _hub        = hub;
        _router     = router;
        _loggers    = loggers;
        _log        = loggers.CreateLogger<ApiSurface>();

        _clients = new ApiClientRegistry(id, options.Clients, options.AllowAnonymous, identity);
        _map     = new ApiSessionMap(id, sessions, hub);

        if (_clients.Count == 0 && !_clients.AllowsAnonymous)
            throw new InvalidOperationException(
                $"API surface '{id}' has no clients configured. Add one with a KeyRef, or set AllowAnonymous " +
                "on a loopback address.");

        if (options.AllowAnonymous && !IsLoopbackOnly(options.Urls))
            throw new InvalidOperationException(
                $"API surface '{id}' sets AllowAnonymous but listens on '{options.Urls}'. " +
                "Unauthenticated access is only permitted on a loopback address.");
    }

    public SurfaceId Id { get; }

    internal ApiSessionMap Sessions => _map;

    internal ApiClientRegistry Clients => _clients;

    /// <summary>
    /// Where it actually ended up listening. Not the same as the configured URL when the
    /// port is 0 — which is how tests get a port without racing each other for a fixed one.
    /// </summary>
    public IReadOnlyList<string> Addresses => _app is null ? [] : [.. _app.Urls];

    public async Task StartAsync(CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });

        // Kestrel would otherwise write straight to the console, which the dashboard owns.
        // Everything it has to say goes through the host's logging instead.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggers);

        builder.WebHost.UseUrls(_options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // The slim builder ships with an empty type-info resolver, on the assumption that an
        // app being trimmed will supply a source-generated one. Lane is not trimmed, and
        // without this every response fails at serialisation time — as a 500 with an empty
        // body, which is a genuinely unpleasant thing to diagnose.
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        builder.Services.AddSingleton(_options);
        builder.Services.AddSingleton(_clients);
        builder.Services.AddSingleton(_map);
        builder.Services.AddSingleton(_kernel);
        builder.Services.AddSingleton(_sessions);
        builder.Services.AddSingleton(_transcript);
        builder.Services.AddSingleton(_bus);
        builder.Services.AddSingleton(_hub);

        if (_router is not null) builder.Services.AddSingleton(_router);

        if (_options.AllowedOrigins.Count > 0)
        {
            builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
                .WithOrigins([.. _options.AllowedOrigins])
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));
        }

        _app = builder.Build();

        if (_options.AllowedOrigins.Count > 0) _app.UseCors();

        if (_options.Voice) _app.UseWebSockets();

        // A handler that throws would otherwise hand the client an empty 500 with the reason
        // recorded nowhere the host can see it.
        _app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Unhandled error serving {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                if (context.Response.HasStarted) throw;

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;

                // The message stays in the log: it can name file paths and internals, and a
                // client app is not the right audience for either.
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponse("internal_error"), ApiJson.Options).ConfigureAwait(false);
            }
        });

        _app.Use(ApiAuth.Middleware);

        _app.MapGet("/v1/health", () => Results.Ok(new { status = "ok", surface = Id.Value }));

        SessionEndpoints.Map(_app);
        MessageEndpoints.Map(_app);
        EventEndpoints.Map(_app);

        if (_options.Voice) VoiceEndpoints.Map(_app, _loggers);

        await _app.StartAsync(ct).ConfigureAwait(false);

        _log.LogInformation("API surface {Surface} listening on {Urls} for {Clients} client(s){Voice}",
            Id, _options.Urls, _clients.Count,
            _options.Voice && _router is not null ? ", voice enabled" : "");

        _bus.Publish(new SurfaceStateChanged(Id, Connected: true, _options.Urls));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _map.CloseAllAsync("surface stopped").ConfigureAwait(false);

        if (_app is not null)
        {
            try { await _app.StopAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "API surface {Surface} did not stop cleanly", Id); }
        }

        _bus.Publish(new SurfaceStateChanged(Id, Connected: false, "stopped"));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        if (_app is not null) await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// True when every configured URL is loopback. Anonymous access is gated on this, and a
    /// wildcard binding must never satisfy it.
    /// </summary>
    public static bool IsLoopbackOnly(string urls)
    {
        string[] parts = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0) return false;

        foreach (string part in parts)
        {
            if (!Uri.TryCreate(part, UriKind.Absolute, out Uri? uri)) return false;

            if (!uri.IsLoopback) return false;
        }

        return true;
    }
}
