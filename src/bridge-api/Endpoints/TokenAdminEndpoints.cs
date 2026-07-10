using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// ADR-020: self-service bearer-token admin. Bearer-gated (the PWA never holds
/// the admin-secret) — these paths fall through BearerTokenMiddleware's generic
/// /api/* requirement. Logic lives in <see cref="TokenIssuer"/>; these handlers
/// stay thin and audit the two mutations. Secrets are never listed; the rotate
/// response is the only place a new token is returned (once, like issue).
/// </summary>
public static class TokenAdminEndpoints
{
    public record TokenInfo(
        long Id, string? DeviceName, DateTimeOffset CreatedAt,
        DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, bool Current);

    public record RotateRequest(string? DeviceName);

    public static void MapTokenAdmin(this IEndpointRouteBuilder app)
    {
        // GET /api/auth/tokens — list devices (no secrets/hashes).
        app.MapGet("/api/auth/tokens", async (
            HttpContext ctx, TokenIssuer issuer, CancellationToken ct) =>
        {
            var me = ctx.GetAuthToken();
            var rows = await issuer.ListAsync(ct);
            var dto = new List<TokenInfo>(rows.Count);
            foreach (var r in rows)
                dto.Add(new TokenInfo(r.Id, r.DeviceName, r.CreatedAt,
                    r.LastUsedAt, r.RevokedAt, me is not null && r.Id == me.Id));
            return Results.Json(dto, Json.Default);
        });

        // DELETE /api/auth/tokens/{id} — terminal, idempotent revoke.
        app.MapDelete("/api/auth/tokens/{id:long}", async (
            long id, HttpContext ctx, TokenIssuer issuer,
            BridgeDbContext db, CancellationToken ct) =>
        {
            var me = ctx.GetAuthToken();
            var result = await issuer.RevokeAsync(id, ct);
            if (result is null)
            {
                await Audit(db, me, "token.revoke", "not_found",
                    $"target={id}", ct);
                return ResultsHelpers.Error(404, "token.not_found",
                    "No token with that id");
            }

            var alreadyRevoked = result == false;
            await Audit(db, me, "token.revoke", "ok",
                $"target={id} self={(me is not null && me.Id == id)} "
                + $"alreadyRevoked={alreadyRevoked}", ct);
            return Results.Json(new { ok = true, alreadyRevoked }, Json.Default);
        });

        // POST /api/auth/tokens/rotate — new token + revoke current, atomic.
        app.MapPost("/api/auth/tokens/rotate", async (
            HttpContext ctx, TokenIssuer issuer, BridgeDbContext db,
            RotateRequest? body, CancellationToken ct) =>
        {
            var me = ctx.GetAuthToken();
            if (me is null)
                return ResultsHelpers.Error(401, "auth.invalid_token",
                    "Bearer token required");

            var (token, row) = await issuer.RotateAsync(me, body?.DeviceName, ct);
            await Audit(db, me, "token.rotate", "ok",
                $"old={me.Id} new={row.Id}", ct);
            return Results.Json(
                new { token, deviceName = row.DeviceName, createdAt = row.CreatedAt },
                Json.Default, statusCode: 201);
        });
    }

    // Mirrors the AuditLog Add pattern used across the other endpoints. Detail
    // carries ids + the user-chosen device label only — NEVER a token string.
    private static async Task Audit(
        BridgeDbContext db, BearerToken? actor, string action,
        string result, string detail, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = action,
            TokenId = actor?.Id,
            Result = result,
            Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }
}
