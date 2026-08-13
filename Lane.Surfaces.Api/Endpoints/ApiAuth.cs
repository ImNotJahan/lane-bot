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

        ApiClient? client = clients.Authenticate(Presented(context));

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
