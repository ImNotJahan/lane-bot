using Lane.Core.Credits;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Lane.Surfaces.Api.Endpoints;

/// <summary>
/// Who is calling, established once per request.
/// </summary>
public static class ApiAuth
{
    private const string ItemKey = "lane.client";

    public static async Task Middleware(HttpContext context, RequestDelegate next)
    {
        // Health is deliberately open: it exists to be polled by something that has no key.
        if (context.Request.Path.StartsWithSegments("/v1/health"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        ApiClientRegistry clients = context.RequestServices.GetRequiredService<ApiClientRegistry>();

        string? presented = Presented(context);

        ApiClient? client = clients.Match(presented)
            ?? await SponsoredAsync(context, clients, presented).ConfigureAwait(false)
            ?? clients.Anonymous;

        if (client is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";

            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("unauthorized", "Provide a client key as 'Authorization: Bearer <key>'."),
                ApiJson.Options).ConfigureAwait(false);

            return;
        }

        context.Items[ItemKey] = client;

        await next(context).ConfigureAwait(false);
    }

    /// <summary>The authenticated caller. Only null on paths the middleware lets through.</summary>
    public static ApiClient? Current(HttpContext context) => context.Items[ItemKey] as ApiClient;

    public static ApiClient Require(HttpContext context) =>
        Current(context) ?? throw new InvalidOperationException("No authenticated client on this request.");

    /// <summary>Charges a sponsored client's sponsors for one request. Always true for configured clients.</summary>
    public static async ValueTask<bool> ChargeAsync(HttpContext context, ApiClient client, CancellationToken ct) =>
        client.SponsoredId is null ||
        context.RequestServices.GetService<ISponsoredAccess>() is { } access &&
        await access.TryChargeAsync(SponsoredKind.ApiClient, client.SponsoredId, ct).ConfigureAwait(false);

    public static IResult OutOfCredits(ApiClient client) =>
        Results.Json(
            new ErrorResponse("out_of_credits", $"Nobody sponsoring '{client.SponsoredId}' has credits left to spend on it today."),
            ApiJson.Options,
            statusCode: StatusCodes.Status402PaymentRequired);

    private static async ValueTask<ApiClient?> SponsoredAsync(HttpContext context, ApiClientRegistry clients, string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;

        if (context.RequestServices.GetService<ISponsoredAccess>() is not { } access) return null;

        return await access.FindApiClientAsync(presented, context.RequestAborted).ConfigureAwait(false) is { } sponsored
            ? clients.Sponsored(sponsored)
            : null;
    }

    private static string? Presented(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Lane-Key", out Microsoft.Extensions.Primitives.StringValues header)
            && header.Count > 0)
            return header[0];

        string? authorization = context.Request.Headers.Authorization.ToString();

        if (!string.IsNullOrEmpty(authorization) &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..].Trim();

        // A query key only for the voice socket: browsers cannot set headers on a WebSocket
        // handshake. Allowing it everywhere would write client keys into every access log
        // and proxy trace that records a URL.
        if (context.WebSockets.IsWebSocketRequest && context.Request.Query.TryGetValue("key", out var query))
            return query.Count > 0 ? query[0] : null;

        return null;
    }
}
