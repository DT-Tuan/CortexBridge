using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;

namespace CortexBridge.Api.Endpoints;

public static class AuthEndpoints
{
    public record IssueRequest(string? DeviceName);

    public static void MapAuth(this IEndpointRouteBuilder app, IConfiguration config)
    {
        // POST /api/auth/issue — admin-secret protected
        app.MapPost("/api/auth/issue", async (HttpContext ctx, TokenIssuer issuer) =>
        {
            var adminSecret = config["BRIDGE_ADMIN_SECRET"]
                ?? Environment.GetEnvironmentVariable("BRIDGE_ADMIN_SECRET");
            if (string.IsNullOrEmpty(adminSecret))
                return ResultsHelpers.Error(500, "config.missing", "BRIDGE_ADMIN_SECRET not configured");

            var presented = ctx.Request.Headers["X-Admin-Secret"].ToString();
            if (!CryptographicEquals(presented, adminSecret))
                return ResultsHelpers.Error(401, "auth.bad_admin_secret", "Invalid admin secret");

            IssueRequest? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<IssueRequest>(); }
            catch { /* body optional */ }

            var (token, row) = await issuer.IssueAsync(body?.DeviceName, ctx.RequestAborted);
            return Results.Json(
                new { token, deviceName = row.DeviceName, createdAt = row.CreatedAt },
                Json.Default,
                statusCode: 201);
        });

        // POST /api/auth/stream-token — exchange bearer for short-lived SSE token
        app.MapPost("/api/auth/stream-token", (HttpContext ctx, StreamTokenStore store) =>
        {
            var bearer = ctx.GetAuthToken();
            if (bearer is null)
                return ResultsHelpers.Error(401, "auth.invalid_token", "Bearer token required");

            var (streamToken, expiresAt) = store.Issue(bearer.Id);
            return Results.Json(new { streamToken, expiresAt }, Json.Default, statusCode: 201);
        });
    }

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
