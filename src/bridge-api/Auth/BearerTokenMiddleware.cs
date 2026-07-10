using CortexBridge.Api.Data.Entities;

namespace CortexBridge.Api.Auth;

/// <summary>
/// Validates Authorization: Bearer header for /api/* requests.
/// Skips /api/health and /api/auth/issue (admin-secret auth) and /internal/* (handled separately).
/// On success attaches the BearerToken row to HttpContext.Items["AuthToken"].
/// </summary>
public class BearerTokenMiddleware
{
    private readonly RequestDelegate _next;

    public BearerTokenMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, TokenIssuer issuer)
    {
        var path = ctx.Request.Path.Value ?? "";

        // Public endpoints
        if (path == "/api/health" || path == "/api/auth/issue")
        {
            await _next(ctx);
            return;
        }

        // SSE stream uses query-param auth (EventSource cannot send headers).
        // Path matches /api/sessions/{projectId}/stream — handler validates the streamToken.
        if (path.StartsWith("/api/sessions/", StringComparison.Ordinal) &&
            path.EndsWith("/stream", StringComparison.Ordinal))
        {
            await _next(ctx);
            return;
        }

        // Internal endpoints have their own auth (loopback + hook token)
        if (path.StartsWith("/internal/", StringComparison.Ordinal))
        {
            await _next(ctx);
            return;
        }

        // Static assets and SPA fallback (anything not /api/*)
        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await _next(ctx);
            return;
        }

        // /api/* — require bearer
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            await WriteError(ctx, StatusCodes.Status401Unauthorized, "auth.missing_token", "Authorization header required");
            return;
        }

        var presented = header["Bearer ".Length..].Trim();
        var token = await issuer.ValidateAsync(presented, ctx.RequestAborted);
        if (token is null)
        {
            await WriteError(ctx, StatusCodes.Status401Unauthorized, "auth.invalid_token", "Token is invalid or revoked");
            return;
        }

        ctx.Items["AuthToken"] = token;
        await _next(ctx);
    }

    private static async Task WriteError(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new { error = new { code, message } });
    }
}

public static class HttpContextAuthExtensions
{
    public static BearerToken? GetAuthToken(this HttpContext ctx) =>
        ctx.Items["AuthToken"] as BearerToken;
}
