using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Hooks;
using Microsoft.EntityFrameworkCore;

namespace CortexBridge.Api.Endpoints;

public static class PushSubscribeEndpoints
{
    public record SubscribeRequest(SubscriptionPayload Subscription, string? DeviceLabel);
    public record SubscriptionPayload(string Endpoint, ExpirationKeys Keys, double? ExpirationTime);
    public record ExpirationKeys(string P256dh, string Auth);

    public record VapidPublicKeyResponse(string PublicKey, bool Enabled);

    public static void MapPushSubscribe(this IEndpointRouteBuilder app)
    {
        // GET /api/push/vapid-key — public key bytes (base64url) so PWA can subscribe.
        // Public endpoint — VAPID public key is meant to be public.
        app.MapGet("/api/push/vapid-key", (IConfiguration config, WebPushSender sender) =>
        {
            var pub = config["VAPID_PUBLIC_KEY"]
                ?? Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY")
                ?? "";
            return Results.Json(new VapidPublicKeyResponse(pub, sender.IsEnabled), Json.Default);
        });

        // POST /api/push/subscribe — store subscription in DB. Bearer-protected.
        app.MapPost("/api/push/subscribe", async (
            HttpContext ctx,
            SubscribeRequest body,
            BridgeDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(body.Subscription?.Endpoint))
                return ResultsHelpers.Error(400, "push.bad_request", "subscription.endpoint required");

            var bearer = ctx.GetAuthToken();
            var existing = await db.PushSubscriptions
                .FirstOrDefaultAsync(p => p.Endpoint == body.Subscription.Endpoint, ct);

            if (existing is not null)
            {
                // Update keys (in case rotated) and metadata
                existing.P256dh = body.Subscription.Keys.P256dh;
                existing.Auth = body.Subscription.Keys.Auth;
                existing.BearerTokenId = bearer?.Id ?? existing.BearerTokenId;
                existing.DeviceLabel = body.DeviceLabel ?? existing.DeviceLabel;
                existing.LastUsedAt = DateTimeOffset.UtcNow;
                existing.ExpiresAt = body.Subscription.ExpirationTime is double exp
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)exp)
                    : null;
            }
            else
            {
                db.PushSubscriptions.Add(new PushSubscription
                {
                    Endpoint = body.Subscription.Endpoint,
                    P256dh = body.Subscription.Keys.P256dh,
                    Auth = body.Subscription.Keys.Auth,
                    BearerTokenId = bearer?.Id,
                    DeviceLabel = body.DeviceLabel,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = body.Subscription.ExpirationTime is double exp
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)exp)
                        : null,
                });
            }
            await db.SaveChangesAsync(ct);
            return Results.Json(new { ok = true }, Json.Default, statusCode: 201);
        });

        // DELETE /api/push/subscribe — remove by endpoint.
        app.MapDelete("/api/push/subscribe", async (
            HttpContext ctx,
            BridgeDbContext db,
            CancellationToken ct) =>
        {
            var endpoint = ctx.Request.Query["endpoint"].ToString();
            if (string.IsNullOrEmpty(endpoint))
                return ResultsHelpers.Error(400, "push.missing_endpoint", "?endpoint= required");

            var sub = await db.PushSubscriptions.FirstOrDefaultAsync(p => p.Endpoint == endpoint, ct);
            if (sub is null) return Results.Json(new { ok = true, removed = 0 }, Json.Default);

            db.PushSubscriptions.Remove(sub);
            await db.SaveChangesAsync(ct);
            return Results.Json(new { ok = true, removed = 1 }, Json.Default);
        });

        // GET /api/push/status — for PWA settings UI to check current subscription state
        app.MapGet("/api/push/status", async (
            HttpContext ctx,
            BridgeDbContext db,
            CancellationToken ct) =>
        {
            var endpoint = ctx.Request.Query["endpoint"].ToString();
            var subscribed = false;
            if (!string.IsNullOrEmpty(endpoint))
                subscribed = await db.PushSubscriptions.AnyAsync(p => p.Endpoint == endpoint, ct);
            var totalCount = await db.PushSubscriptions.CountAsync(ct);
            return Results.Json(new { subscribed, totalCount }, Json.Default);
        });
    }
}
