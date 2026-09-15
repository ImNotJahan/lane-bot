using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lane.Audio;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Sessions;
using Lane.Surfaces.Api.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Api.Endpoints;

/// <summary>
/// A client application's microphone and speaker, over one socket.
///
/// This is the practical answer to "there will be multiple microphones": local audio capture
/// is platform-specific and Windows-only through NAudio, whereas any phone, browser tab or
/// helper process can open a socket and stream PCM. Each socket is one source bound to one
/// conversation, and several can be open at once — including several bound to the same
/// conversation, which arrive through that session's pump as one ordered exchange.
/// </summary>
public static class VoiceEndpoints
{
    public static void Map(IEndpointRouteBuilder app, ILoggerFactory loggers) =>
        app.MapGet("/v1/sessions/{key}/voice", (
            HttpContext context,
            string key,
            ApiSessionMap map,
            IAgentKernel kernel,
            ApiSurfaceOptions options,
            // Explicit, because it is the one dependency that may legitimately be absent.
            // Left to inference, a missing AudioRouter is read as a request body — which
            // fails at route-construction time and takes every other endpoint down with it.
            [FromServices] AudioRouter? router,
            int? rate,
            int? channels,
            int? outRate,
            int? outChannels,
            string? speaker,
            bool? diarize) =>
            HandleAsync(
                context, key, map, kernel, options, router,
                rate, channels, outRate, outChannels, speaker, diarize,
                loggers.CreateLogger("Lane.Surfaces.Api.Voice")));

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        string key,
        ApiSessionMap map,
        IAgentKernel kernel,
        ApiSurfaceOptions options,
        AudioRouter? router,
        int? rate,
        int? channels,
        int? outRate,
        int? outChannels,
        string? speaker,
        bool? diarize,
        ILogger log)
    {
        ApiClient client = ApiAuth.Require(context);

        if (!context.WebSockets.IsWebSocketRequest)
            return Results.BadRequest(new ErrorResponse("not_a_websocket", "This endpoint expects a WebSocket upgrade."));

        if (!ApiKeys.IsValid(key)) return Errors.BadKey(key);

        // Naming a speaker and asking for the speakers to be told apart are contradictory
        // instructions, and silently honouring one of them would put a whole room's words
        // under one person's name — the exact fault diarization is here to fix.
        if (diarize == true && !string.IsNullOrWhiteSpace(speaker))
            return Results.BadRequest(new ErrorResponse(
                "conflicting_speaker",
                "Send either 'speaker' for a microphone with one person on it, or 'diarize' for one with several."));

        // Audio being unconfigured costs the socket, not the surface — every text endpoint
        // keeps working, and the client is told why rather than left guessing.
        if (router is null)
            return Results.Json(
                new ErrorResponse("voice_unavailable", "Audio is not configured on this instance."),
                ApiJson.Options, statusCode: StatusCodes.Status503ServiceUnavailable);

        AudioFormat incoming = new(rate ?? 16000, channels ?? 1, 16);

        if (!ApiAudioSource.IsSupported(incoming))
            return Results.BadRequest(new ErrorResponse(
                "unsupported_format",
                $"Cannot accept {incoming}. Send {ApiAudioSource.SupportedFormats}."));

        if (!await ApiAuth.ChargeAsync(context, client, context.RequestAborted).ConfigureAwait(false))
            return ApiAuth.OutOfCredits(client);

        AudioFormat outgoing = new(
            outRate ?? options.VoiceOptions.OutputSampleRate,
            outChannels ?? options.VoiceOptions.OutputChannels,
            16);

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        await RunAsync(
            socket, client, key, map, kernel, router,
            incoming, outgoing, speaker, diarize == true, options, log, context.RequestAborted).ConfigureAwait(false);

        return Results.Empty;
    }

    private static async Task RunAsync(
        WebSocket socket,
        ApiClient client,
        string key,
        ApiSessionMap map,
        IAgentKernel kernel,
        AudioRouter router,
        AudioFormat incoming,
        AudioFormat outgoing,
        string? speakerName,
        bool diarize,
        ApiSurfaceOptions options,
        ILogger log,
        CancellationToken ct)
    {
        // A voice conversation is its own session, sharing the text one's memory group. That
        // is the same relationship a Discord channel has with the voice channel beside it:
        // one conversation as far as memory is concerned, two as far as turn-taking is.
        SessionId id = map.IdFor(client, key, SessionKind.Voice);

        Participant speaker = string.IsNullOrWhiteSpace(speakerName)
            ? client.Participant
            : new Participant(new ParticipantId(map.Surface, $"{client.Id}:{speakerName}"), speakerName);

        using WebSocketVoiceSocket transport = new(socket);

        ApiVoiceOutput output = new(id, transport, outgoing, log);

        ApiSessionEntry entry = map.OpenWith(client, key, SessionKind.Voice, speaker, output);

        await using ApiAudioSource source = new(
            new AudioSourceId($"{map.Surface.Value}/ws/{Guid.NewGuid():n}"),
            incoming,
            diarize ? null : speaker.DisplayName);

        // A socket that says who is on it is one person's microphone; one that asks to be
        // diarized is a microphone in a room, and the speaker it opened with is only who
        // the words fall back to when the audio cannot place them.
        if (diarize) router.RegisterShared(source, id, speaker);
        else router.Register(source, id, speaker);

        log.LogInformation("Voice socket open for {Client} on {Session} ({In} in, {Out} out){Diarized}",
            client.Id, id, incoming, outgoing, diarize ? ", telling speakers apart" : "");

        try
        {
            await transport.SendEventAsync("ready", new
            {
                session  = id.Value,
                input    = new { rate = incoming.SampleRate, channels = incoming.Channels, encoding = "pcm_s16le" },
                output   = new { rate = outgoing.SampleRate, channels = outgoing.Channels, encoding = "pcm_s16le" },
                diarize
            }, ct).ConfigureAwait(false);

            await ReceiveAsync(socket, source, kernel, id, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* the client hung up */ }
        catch (WebSocketException ex)
        {
            log.LogDebug(ex, "Voice socket on {Session} closed abruptly", id);
        }
        finally
        {
            await router.UnregisterAsync(source.Id).ConfigureAwait(false);

            // The session outlives the socket: a client that reconnects rejoins the same
            // conversation, and history written during the call is still there.
            entry.Attachment.Dispose();

            log.LogInformation("Voice socket closed for {Client} on {Session}", client.Id, id);
        }
    }

    private static async Task ReceiveAsync(
        WebSocket socket,
        ApiAudioSource source,
        IAgentKernel kernel,
        SessionId session,
        ApiSurfaceOptions options,
        CancellationToken ct)
    {
        byte[] buffer = new byte[16 * 1024];

        using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(ct);

        idle.CancelAfter(options.VoiceOptions.IdleTimeout);

        while (socket.State == WebSocketState.Open && !idle.IsCancellationRequested)
        {
            WebSocketReceiveResult result;

            try
            {
                result = await socket.ReceiveAsync(buffer, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "idle").ConfigureAwait(false);
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "bye").ConfigureAwait(false);
                return;
            }

            idle.CancelAfter(options.VoiceOptions.IdleTimeout);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                source.Write(buffer.AsSpan(0, result.Count));
                continue;
            }

            await HandleControlAsync(
                Encoding.UTF8.GetString(buffer, 0, result.Count), kernel, session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Text frames are control, not speech. Only one command matters so far: stop talking —
    /// the same barge-in a microphone triggers, for a client whose user pressed a button.
    /// </summary>
    private static async Task HandleControlAsync(string json, IAgentKernel kernel, SessionId session)
    {
        string? type;

        try
        {
            type = JsonDocument.Parse(json).RootElement.TryGetProperty("type", out JsonElement found)
                ? found.GetString()
                : null;
        }
        catch (JsonException)
        {
            return;
        }

        if (string.Equals(type, "cancel", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "interrupt", StringComparison.OrdinalIgnoreCase))
            await kernel.CancelTurnAsync(session, "client interrupted").ConfigureAwait(false);
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(status, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { /* already gone */ }
    }
}
