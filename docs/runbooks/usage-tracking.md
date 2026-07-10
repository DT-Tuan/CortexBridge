# Runbook — Usage tracking (official quota % + ccusage cost, host sampler)

Tracks Claude Code usage two ways (ADR-024):

1. **Quota gauges 5h/7d** — the OFFICIAL Anthropic utilization % (the exact
   numbers Claude Code's `/usage` panel shows), sampled host-side from the
   OAuth usage endpoint. No caps, no estimation, no tuning.
2. **Cost attribution** — [ccusage](https://github.com/ryoppippi/ccusage)
   token/USD detail per 5h-block / rolling-7d / per-project / per-model
   ("where did my budget go" — the official endpoint has no such breakdown).

Host samples every ~60s (ccusage is a Node tool + the OAuth token lives in
`~/.claude/.credentials.json` — neither belongs in the slim bridge container);
bridge reads the resulting JSON via a read-only bind mount. Same host-side
pattern as ADR-023.
**Installed 2026-06-05; official endpoint added 2026-06-11 (ADR-024).**

## Architecture

```
host: ~/.local/bin/cortex-usage-sample.sh    container: cc-bridge
  ↓ ccusage blocks/daily/session --json          ↓ GET /api/usage
  ↓ GET api.anthropic.com/api/oauth/usage        ↑ UsageService.GetCurrent()
  ↓   (Bearer = ~/.claude/.credentials.json)
~/.local/share/cortex-bridge/usage.json  ──bind──►  /var/cortex-bridge/usage.json (ro)
  (systemd-user timer, 60s)
```

### Official endpoint (ADR-024)

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <accessToken>          # from ~/.claude/.credentials.json
anthropic-beta: oauth-2025-04-20
→ { five_hour: {utilization, resets_at}, seven_day: {...}, extra_usage: {...} }
```

- **Undocumented/beta** — schema may change without notice. Degradation: on
  any failure the sampler carries the previous `official` block forward
  UNCHANGED (its `takenAtUtc` goes stale) → PWA shows a stale-badge instead
  of losing the gauge. No estimated fallback (the old cap/calibration
  subsystem is deleted; `usage_caps` table dropped on startup).
- **Token handling:** read inside the sampler process only — never logged,
  never written to usage.json, never enters the container env. Claude Code
  refreshes the credentials file itself; if no CC session runs for long, the
  token can expire → official block goes stale until the next CC start.

## Artifacts

### 1. `~/.local/bin/cortex-usage-sample.sh`
Shells `ccusage blocks --active --json` (5h block) + `ccusage daily --json`
(rolled into a rolling 7-day window) + `ccusage claude session --json`
(per-project lifetime) + the official OAuth usage fetch. Atomic-mv to final
path. Bridge reads on demand; cache = file mtime.

### 2. `~/.config/systemd/user/cortex-usage-sample.{service,timer}`
Oneshot + 60s recurring timer. Lingering on (per ADR-023) ⇒ survives reboot.

### 3. `docker-compose.yml` volume
```yaml
- usage-data:/var/cortex-bridge:ro    # in cc-bridge.volumes
...
usage-data:
  driver: local
  driver_opts: { type: none, o: bind, device: /home/youruser/.local/share/cortex-bridge }
```

### 4. Bridge code
`src/bridge-api/Usage/` — `UsagePaths`, `UsageService`, `UsageDtos`, `UsagePoller`.
`src/bridge-api/Endpoints/UsageEndpoint.cs` — `GET /api/usage` (bearer-auth).

## Verify

```bash
# host: timer alive + last run recent
systemctl --user list-timers cortex-usage-sample.timer

# host: file fresh (mtime should be < 90s old) + official block present
stat -c '%Y %n' ~/.local/share/cortex-bridge/usage.json
python3 -c "import json;print(json.load(open('$HOME/.local/share/cortex-bridge/usage.json'))['official'])"

# cross-check: official.fiveHour.utilization must MATCH the /usage panel
# in any running Claude Code session (±1% for sampling lag).

# bridge: endpoint returns valid JSON
TOKEN=$(curl -sS -X POST http://127.0.0.1:3000/api/auth/issue \
  -H "X-Admin-Secret: $BRIDGE_ADMIN_SECRET" -H 'Content-Type: application/json' \
  -d '{"label":"smoke","scopes":["read"]}' | jq -r .token)
curl -sS -H "Authorization: Bearer $TOKEN" http://127.0.0.1:3000/api/usage | jq .official
```

Expected: `{ takenAtUtc, block5h: {…cost detail…}, week7d: {…}, projects: […],
official: { fiveHour: {utilization, resetsAt}, sevenDay: {…}, takenAtUtc } }`.

## Operate

- **Quota gauge stale** (PWA badge): check token freshness — open any CC
  session on the VM (refreshes `~/.claude/.credentials.json`), then
  `systemctl --user restart cortex-usage-sample.timer`. If still stale,
  probe the endpoint manually (curl with the access token) — a schema change
  means patching the sampler's `fetch_official()`.
- **Pause sampling:** `systemctl --user stop cortex-usage-sample.timer`. Endpoint will keep returning the last-good cached snapshot.
- **Change sample interval:** edit `OnUnitActiveSec=` in the `.timer`; `systemctl --user daemon-reload && systemctl --user restart cortex-usage-sample.timer`.

## Uninstall

```bash
systemctl --user disable --now cortex-usage-sample.timer
rm ~/.config/systemd/user/cortex-usage-sample.{service,timer}
rm ~/.local/bin/cortex-usage-sample.sh
rm -rf ~/.local/share/cortex-bridge
systemctl --user daemon-reload
# revert docker-compose.yml (drop the usage-data volume + mount) + docker compose up -d cc-bridge
```
