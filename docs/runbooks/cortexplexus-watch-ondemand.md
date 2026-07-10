# Runbook — On-demand cortexplexus watch (session-gated)

Implements ADR-023. Host-side
(VPS `cortex-host`), `systemd --user`, 0-sudo. **Installed + verified 2026-05-24.**

## What it does

A `systemd --user` timer polls every ~60 s, computes which allowlisted repos
have a **live CC session** (Bridge = tmux `cc:<p>` window; PC = a real
`~/.claude/ide/*.lock` for the repo), and `start`/`stop`s the existing
`cortexplexus-watch@<name>.service` accordingly — with a **30-min idle grace**
before stopping (avoids re-sync churn on session flap). Watch instances are
**not enabled** (no boot activation); only the reconcile timer is enabled.

### Naming (dir ≠ repo name)

Detection keys off the **directory name** (what tmux windows + ide-locks
report). The systemd instance / cortexplexus repo name usually equals the dir,
**except `CortexBridge`**, whose project is indexed as **`CortexBridge`**. The
reconcile script holds a `NAME` map (`CortexBridge → CortexBridge`) and the
`@CortexBridge` instance gets a drop-in pinning its path back to
`~/workspace/CortexBridge` (artifact 4). All other repos are dir == name.

## Artifacts

### 1. `~/.local/bin/cortexplexus-watch-reconcile.sh`

```bash
#!/usr/bin/env bash
# cortexplexus-watch-reconcile — start/stop per-project watch agents from live
# CC sessions. Driven by cortexplexus-watch-reconcile.timer (~60s). ADR-023.
# Defensive PATH: systemd --user gives a minimal PATH; we need tmux/python3/systemctl.
export PATH="/usr/local/bin:/usr/bin:/bin:${PATH:-}"
set -uo pipefail

# Dir name (tmux window / ide-lock basename) → cortexplexus repo + systemd
# instance name. All match except CortexBridge (project indexed as "CortexBridge";
# @CortexBridge has a drop-in pinning path back to ~/workspace/CortexBridge).
declare -A NAME=(
  [CortexBridge]=CortexBridge
  [CortexPlexus]=CortexPlexus
  [project-beta]=project-beta
  [project-delta]=project-delta
  [project-alpha]=project-alpha
  [project-epsilon]=project-epsilon
)

TMUX_SESSION="cc"
IDE_DIR="$HOME/.claude/ide"
WS="$HOME/workspace"
STATE_DIR="$HOME/.local/state/cortexplexus-watch"
GRACE_SECONDS=1800   # 30 min inactive before stopping a watch

mkdir -p "$STATE_DIR"
now=$(date +%s)
declare -A active

# Bridge mode: tmux windows (exclude the 'sleep' keepalive window).
if command -v tmux >/dev/null 2>&1; then
  while IFS= read -r w; do
    [ -n "$w" ] && [ "$w" != "sleep" ] && active["$w"]=1
  done < <(tmux list-windows -t "$TMUX_SESSION" -F '#W' 2>/dev/null)
fi

# PC mode: ide-lock workspaceFolders basename — ONLY if it maps to a real
# ~/workspace/<p> dir (filters the home-dir phantom lock; ModeWatcher parity, ADR-017).
if [ -d "$IDE_DIR" ]; then
  for f in "$IDE_DIR"/*.lock; do
    [ -f "$f" ] || continue
    while IFS= read -r name; do
      [ -n "$name" ] && [ -d "$WS/$name" ] && active["$name"]=1
    done < <(python3 - "$f" <<'PY' 2>/dev/null
import json, os, sys
try: d = json.load(open(sys.argv[1]))
except Exception: sys.exit(0)
for p in d.get("workspaceFolders", []) or []:
    if isinstance(p, str): print(os.path.basename(p.rstrip("/")))
PY
)
  done
fi

# Reconcile each allowlisted project (keyed by DIR name; unit uses mapped name).
for proj in "${!NAME[@]}"; do
  inst="${NAME[$proj]}"
  unit="cortexplexus-watch@${inst}.service"
  seen="$STATE_DIR/${proj}.lastactive"
  running="$(systemctl --user is-active "$unit" 2>/dev/null || true)"
  if [ -n "${active[$proj]:-}" ]; then
    echo "$now" > "$seen"                       # stamp last-active
    if [ "$running" != "active" ]; then
      systemctl --user start "$unit" && echo "START $inst (dir $proj session active)"
    fi
  elif [ "$running" = "active" ]; then
    last="$(cat "$seen" 2>/dev/null || echo 0)"
    if [ $(( now - last )) -ge "$GRACE_SECONDS" ]; then
      systemctl --user stop "$unit" && echo "STOP $inst (idle >= ${GRACE_SECONDS}s)"
    fi
  fi
done
```

### 2. `~/.config/systemd/user/cortexplexus-watch-reconcile.service`

```ini
[Unit]
Description=Reconcile cortexplexus watch agents against live CC sessions (ADR-023)
After=cortex-tmux.service

[Service]
Type=oneshot
ExecStart=%h/.local/bin/cortexplexus-watch-reconcile.sh
```

### 3. `~/.config/systemd/user/cortexplexus-watch-reconcile.timer`

```ini
[Unit]
Description=Periodic cortexplexus watch reconcile (~60s)

[Timer]
OnBootSec=30
OnUnitActiveSec=60
AccuracySec=10
Persistent=false

[Install]
WantedBy=timers.target
```

### 4. `~/.config/systemd/user/cortexplexus-watch@CortexBridge.service.d/path.conf`

Pins the `CortexBridge` instance (dir is `CortexBridge`, repo name `CortexBridge`):

```ini
[Service]
ExecStart=
ExecStart=/usr/bin/dotnet %h/.cortexplexus/agent/cortexplexus-agent.dll watch %h/workspace/CortexBridge --server http://192.168.1.20:8080 --name CortexBridge
WorkingDirectory=%h/workspace/CortexBridge
```

### 5. `cortexplexus-watch@.service` — pre-existing template, unchanged

## Install (executed 2026-05-24)

```bash
chmod +x ~/.local/bin/cortexplexus-watch-reconcile.sh
mkdir -p ~/.config/systemd/user/cortexplexus-watch@CortexBridge.service.d   # (drop-in dir)
systemctl --user daemon-reload
systemctl --user disable --now cortexplexus-watch@CortexPlexus.service   # drop legacy always-on
systemctl --user enable --now cortexplexus-watch-reconcile.timer
systemctl --user start cortexplexus-watch-reconcile.service               # first reconcile now
```

> ⚠ Never `pkill -f` the agent with a pattern that also matches your own
> command line — it kills the running shell. Kill stray agents by PID, or
> prefer `systemctl --user stop`.

Lingering is already on (`loginctl show-user $USER | grep Linger` → `yes`), so
the timer + transient watches survive reboot.

## Verify

```bash
systemctl --user list-timers cortexplexus-watch-reconcile.timer
systemctl --user list-units 'cortexplexus-watch@*' --state=active --no-legend   # = active sessions
journalctl --user -u cortexplexus-watch-reconcile.service --since '10 min ago' -o cat   # START/STOP log
```

**Verified 2026-05-24:** with sessions for CortexBridge (ide-lock), CortexPlexus
(ide-lock), project-delta (tmux) → exactly `@CortexBridge`, `@CortexPlexus`,
`@project-delta` running; project-beta + project-alpha (no session) not
started. `@CortexBridge` ExecStart confirmed `watch …/CortexBridge --name CortexBridge`.

**Full-lifecycle verified 2026-05-25** (CortexPlexus, live, real timeline): B→A
PC→Bridge auto-resume kept watch active across the ~52s gap; mobile **Stop** →
`owner=none` tombstone → reconcile `STOP CortexPlexus (idle >= grace)` released
it; mobile **Activate** → tmux window → reconcile `START`; A→B Bridge→PC kept
watch **continuously** active (`NRestarts=0`, `ActiveEnterTimestamp` unchanged
through the `ModeWatcher A→B` kill) — confirming the union (tmux ∪ ide-lock)
signal is gapless across every mode transition, decoupled from ownership.

## Operate

- **Add/remove a repo:** edit the `NAME` map in the script (re-read each tick).
- **Change grace:** edit `GRACE_SECONDS`.
- **Force a repo on/off now:** `systemctl --user start|stop cortexplexus-watch@<name>`
  (next reconcile may revert based on session state).
- **Pause the controller:** `systemctl --user stop cortexplexus-watch-reconcile.timer`.

## Uninstall / revert to always-on

```bash
systemctl --user disable --now cortexplexus-watch-reconcile.timer
systemctl --user stop 'cortexplexus-watch@*'
rm ~/.config/systemd/user/cortexplexus-watch-reconcile.{service,timer}
rm -rf ~/.config/systemd/user/cortexplexus-watch@CortexBridge.service.d
rm ~/.local/bin/cortexplexus-watch-reconcile.sh
rm -rf ~/.local/state/cortexplexus-watch
systemctl --user daemon-reload
systemctl --user enable --now cortexplexus-watch@CortexPlexus.service   # restore old always-on
```
