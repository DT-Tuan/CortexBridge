using System.Text.Json;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using WebPush;

namespace CortexBridge.Api.Hooks;

/// <summary>
/// Sends Web Push notifications to subscribed PWA clients.
/// Uses VAPID for authentication. Per-subscription failure is non-fatal — expired subs (410)
/// are auto-removed; other errors logged and skipped.
/// </summary>
public class WebPushSender
{
    private readonly WebPushClient _client;
    private readonly VapidDetails? _vapid;
    private readonly ILogger<WebPushSender> _log;

    public WebPushSender(IConfiguration config, ILogger<WebPushSender> log)
    {
        _client = new WebPushClient();
        _log = log;

        var pub = config["VAPID_PUBLIC_KEY"]
            ?? Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
        var priv = config["VAPID_PRIVATE_KEY"]
            ?? Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");
        var subject = config["VAPID_SUBJECT"]
            ?? Environment.GetEnvironmentVariable("VAPID_SUBJECT")
            ?? "mailto:noreply@cortexbridge.local";

        if (!string.IsNullOrEmpty(pub) && !string.IsNullOrEmpty(priv))
        {
            _vapid = new VapidDetails(subject, pub, priv);
        }
        else
        {
            _log.LogWarning("VAPID keys not configured; Web Push will be disabled");
        }
    }

    public bool IsEnabled => _vapid is not null;

    public string? VapidPublicKey { get; init; }

    public async Task SendToAllAsync(
        BridgeDbContext db,
        string projectId,
        string title,
        string body,
        string? clickUrl,
        CancellationToken ct)
    {
        if (_vapid is null) return;

        var subs = await db.PushSubscriptions.ToListAsync(ct);
        if (subs.Count == 0) return;

        // No `actions` array — iOS Safari (through iOS 26) ignores Notification.actions
        // on Web Push, so embedding them does nothing on the primary mobile target.
        // Quick-reply UX lives inside the PWA: tap banner body → openWindow at url →
        // sessions/[id] route renders 1/2/3 buttons from needsInput state (see
        // src/pwa/src/routes/sessions/[id]/+page.svelte permission-prompt banner).
        var payloadJson = JsonSerializer.Serialize(new
        {
            title,
            body,
            url = clickUrl,
            projectId,
            ts = DateTimeOffset.UtcNow.ToString("o"),
        });

        var stale = new List<long>();
        foreach (var sub in subs)
        {
            var pushSub = new global::WebPush.PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
            try
            {
                await _client.SendNotificationAsync(pushSub, payloadJson, _vapid);
                sub.LastUsedAt = DateTimeOffset.UtcNow;
            }
            catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone
                                              || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _log.LogInformation("Push subscription {Id} expired (HTTP {Status}), removing",
                    sub.Id, (int)ex.StatusCode);
                stale.Add(sub.Id);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Push send failed for subscription {Id}", sub.Id);
                // Don't remove — could be transient (network, push service hiccup)
            }
        }

        if (stale.Count > 0)
        {
            db.PushSubscriptions.RemoveRange(subs.Where(s => stale.Contains(s.Id)));
            await db.SaveChangesAsync(ct);
        }
        else if (subs.Any(s => s.LastUsedAt.HasValue && s.LastUsedAt > DateTimeOffset.UtcNow.AddSeconds(-1)))
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Send a "clear" push so the SW dismisses any lockscreen notification for this project
    /// without showing a new one. Used when needsInput goes back to false (user replied from
    /// another device, Stop hook fired, etc) — keeps notifications in sync across devices.
    /// </summary>
    public async Task SendClearAsync(BridgeDbContext db, string projectId, CancellationToken ct)
    {
        if (_vapid is null) return;
        var subs = await db.PushSubscriptions.ToListAsync(ct);
        if (subs.Count == 0) return;

        var payloadJson = JsonSerializer.Serialize(new
        {
            clear = true,
            projectId,
            ts = DateTimeOffset.UtcNow.ToString("o"),
        });

        foreach (var sub in subs)
        {
            var pushSub = new global::WebPush.PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
            try { await _client.SendNotificationAsync(pushSub, payloadJson, _vapid); }
            catch { /* best-effort */ }
        }
    }
}
