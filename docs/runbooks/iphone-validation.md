# Runbook: iPhone validation checklist (Phase 1 sign-off)

This is the manual gate before declaring Phase 1 done. Wedges 1–9 are validated programmatically; Wedge 10 is the human-only verification that the actual product works on a real iPhone.

Per ADR-012, mobile push goes through Web Push (no separate ntfy app required). The PWA must be served from a publicly-trusted hostname (Cloudflare Tunnel route) — Apple gates `PushManager.subscribe()` on cert eligibility.

## Prerequisites

- Phase 1 stack deployed per [deployment.md](deployment.md): `cc-bridge` + `caddy` + `cloudflared` running.
- A publicly-trusted hostname routed via Cloudflare Tunnel → `cc-bridge:3000` (e.g. `cortex.example.com`). Cert auto-issued by CF (Google Trust Services).
- VAPID keypair generated, `VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY` / `VAPID_SUBJECT` configured in `.env`.
- One bearer token issued via `POST /api/auth/issue` and pasted into the PWA on the iPhone.
- At least one CC session running inside `cc-bridge` (deployment.md §"Onboard first project").

## A. PWA install + Web Push opt-in

| Step | Expected |
|------|----------|
| Open `https://<cf-hostname>` in iOS Safari | Padlock green (publicly-trusted cert); login screen renders |
| Paste bearer token, tap **Đăng nhập** | Redirect to dashboard |
| Share menu → **Add to Home Screen** | App icon appears |
| Launch from icon (standalone, no Safari chrome) | Dashboard loads |
| Settings → "Thông báo đẩy" → **Bật** | iOS popup "Allow Notifications" appears |
| Tap **Allow** | Status shows "Đang nhận thông báo"; bridge logs a `POST /api/push/subscribe` entry |
| Force-quit + relaunch | PWA opens, subscription persists |
| Cold-launch on cellular | First paint < 3s |

## B. Vietnamese input

| Step | Expected |
|------|----------|
| Open chat view, tap composer | iOS keyboard rises, safe-area respected |
| Type using telex/VNI: "Xin chào, đây là phép thử" | All tone marks render correctly while composing |
| Tap **Gửi** | Reply lands in tmux pane EXACTLY as typed (`docker exec cc-bridge tmux capture-pane -p -t cc:<project>`) |
| Send multi-line reply (paste or Shift+Enter) | Newlines preserved; CC receives one atomic input followed by Enter |
| Send a reply containing backticks, semicolons, `; rm -rf /` literal | Text appears verbatim, **no shell interpretation** |

## C. Live transcript streaming

| Step | Expected |
|------|----------|
| Open chat view; from PC trigger CC to emit a tool_use | Tool-use block appears in PWA within ~500ms |
| Tool result writes to JSONL | Collapsed `tool result` row appears under user message |
| Force-close + reopen Safari → PWA | Initial transcript GET re-fetches; SSE re-attaches; no duplicate messages (dedup-by-uuid working) |
| Toggle airplane mode on/off | SSE auto-reconnects with exponential backoff; no manual refresh needed |

## D. Web Push delivery

| Step | Expected |
|------|----------|
| From PC, ask CC something that triggers a Notification hook (permission prompt, AskUserQuestion) | Push banner arrives within ~3s on iPhone lockscreen |
| Tap notification | PWA opens at `/sessions/<projectId>` |
| Reply from PWA → CC resumes; another device with same PWA installed | Lockscreen notification on that device dismisses (via `clear` push) |
| Lock phone, trigger another notification | Lockscreen banner appears; tap unlocks straight to PWA |
| Toggle "Tắt" in Settings → trigger hook again | No notification arrives |
| Toggle "Bật" again → trigger hook | Notification resumes |

## E. Edge cases

| Step | Expected |
|------|----------|
| Dashboard with 4–5 active projects | All listed; the one with `needsInput=true` shows the warning badge |
| Send reply, immediately try another from a second tab | First → 202; second → 409 `reply.in_flight` (spec 03 §3.6) |
| Issue a wrong/revoked token in /settings | Login fails with clean message; old token invalidated |
| Bridge container restart while PWA is open | SSE reconnects; transcript re-fetches; no message loss |
| Push subscription becomes stale (server returns HTTP 410) | Backend auto-removes; PWA opt-in flow lets user re-subscribe |
| Stream-token URL leak (e.g. logged in proxy access log) | Replay attempt → 401 `auth.invalid_stream_token` |

## F. Performance acceptance

Run on the actual iPhone, opened via the public CF hostname:

| Metric | Target | How to measure |
|--------|--------|----------------|
| Cold load (first paint) | < 3s on 4G | Safari Develop menu over USB → Network panel |
| Reply latency (tap Send → text in tmux) | < 500ms p95 | Stopwatch + tmux capture-pane diff |
| SSE message delay | < 800ms p95 | Append a JSONL line by hand, time until appears in PWA |
| Push delivery latency (hook → lockscreen banner) | < 3s p95 | `curl /internal/hooks/notification` from container, stopwatch |
| Initial JS bundle | ≤ 50 KB gz | `pnpm build`, sum of `_app/immutable/entry/*.js` gzipped |

## G. Sign-off

Phase 1 is **done** when:

- [ ] All A/B/C/D rows pass on a real iPhone (model + iOS version recorded below).
- [ ] All E rows produce the documented behavior (no surprises).
- [ ] All F metrics meet the target (or deviation is documented + accepted).
- [ ] End-to-end run captured: PC commits to repo (or VS Code Remote-SSH edits trigger a CC hook) → CC asks question → Web Push arrives on phone → user replies in Vietnamese → CC resumes execution.

| Field | Value |
|-------|-------|
| iPhone model | user's iPhone |
| iOS version | 26 |
| Tester | you@example.com |
| Date | 2026-05-13 |
| Result | **PASS** (core flow) |
| Notes | Push subscription via `pushManager.subscribe()` succeeded on CF Tunnel public hostname (`cortex.example.com`). Did NOT work on Netbird-only Caddy local CA hostname — Apple eligibility check requires public CA, see ADR-012. End-to-end: hook → push → lockscreen banner → tap body → PWA standalone opens → in-PWA 3-button banner → tap `1. Cho phép lần này` → POST `/reply` → tmux `1` echoed → `audit_log id=4 reply ok @ 2026-05-13T00:30:20Z`. `Notification.actions` ignored on iOS (long-press doesn't show custom Đồng ý/Từ chối buttons) — see [PushOptIn.svelte commit 34ea989](../../src/pwa/src/lib/components/PushOptIn.svelte). Cross-device clear via Stop hook observed. Performance metrics in F not separately measured — flag for Phase 2 follow-up. |
