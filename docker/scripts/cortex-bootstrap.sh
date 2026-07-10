#!/usr/bin/env bash
# CortexBridge container entrypoint (ADR-013):
#   The tmux SERVER lives on the VPS host; this container is just the bridge
#   process + a tmux client that talks to /tmp/tmux-1000 (bind-mounted).
#
#   1) Verify host tmux server is reachable (best-effort, log if not).
#   2) Auto-resume CC for each project that has a JSONL transcript +
#      matching workspace dir. The new-window command runs on the HOST tmux
#      server, which spawns `claude` from the HOST PATH.
#   3) Hand off to bridge process.
#
# Failed `claude --resume` simply closes its tmux window — bootstrap doesn't
# crash, project just shows as `stopped` in PWA dashboard until user taps
# "Khởi động lại".
set -euo pipefail

TMUX_SESSION="${BRIDGE_TMUX_SESSION:-cc}"
CC_PROJECTS_ROOT="${BRIDGE_CC_PROJECTS_ROOT:-/home/cortex/.claude/projects}"
WORKSPACE_ROOT="${BRIDGE_WORKSPACE_ROOT:-/workspace}"
CLAUDE_USER_DIR="$(dirname "$CC_PROJECTS_ROOT")"
DATA_DIR="${BRIDGE_DATA_DIR:-/data}"
OWNERSHIP_DB="$DATA_DIR/cortexbridge.db"

log() { echo "[bootstrap $(date -u +%H:%M:%S)] $*"; }

# 1) Probe host tmux. Retry up to 10s in case host setup is racing container start.
log "checking host tmux server via /tmp/tmux-1000/default ..."
for i in 1 2 3 4 5; do
    if tmux list-sessions >/dev/null 2>&1; then
        log "host tmux reachable"
        break
    fi
    if [[ $i -eq 5 ]]; then
        log "WARN: host tmux not reachable yet — bridge will still start. Auto-resume skipped."
        log "      Start it on host: tmux new-session -d -s $TMUX_SESSION 'sleep infinity'"
    fi
    sleep 2
done

# 1.5) Workspace-shared skills + commands → symlink into ~/.claude so CC discovers them.
# Runs INSIDE container, on bind-mounted ~/.claude. Host claude reads the same files.
link_workspace_dir() {
    local kind="$1"  # "skills" or "commands"
    local src_root="$WORKSPACE_ROOT/.claude/$kind"
    local dst_root="$CLAUDE_USER_DIR/$kind"
    [[ -d "$src_root" ]] || return 0
    mkdir -p "$dst_root"
    for src in "$src_root"/*; do
        [[ -e "$src" ]] || continue
        local name; name="$(basename "$src")"
        local dst="$dst_root/$name"
        if [[ -L "$dst" ]]; then
            ln -sfn "$src" "$dst"
            log "shared $kind: $name (relinked)"
        elif [[ -e "$dst" ]]; then
            log "shared $kind: SKIP $name — user-level entry exists, not clobbering"
        else
            ln -s "$src" "$dst"
            log "shared $kind: $name (linked)"
        fi
    done
}
link_workspace_dir skills
link_workspace_dir commands

# 2) Auto-resume per project (only if host tmux reachable)
if tmux list-sessions >/dev/null 2>&1; then
    if [[ -d "$CC_PROJECTS_ROOT" ]]; then
        for projdir in "$CC_PROJECTS_ROOT"/*/; do
            [[ -d "$projdir" ]] || continue

            latest_jsonl=$(ls -t "$projdir"*.jsonl 2>/dev/null | head -1 || true)
            [[ -n "$latest_jsonl" ]] || continue

            cwd=$(jq -r 'select(.cwd) | .cwd' "$latest_jsonl" 2>/dev/null | head -1 || true)
            [[ -n "$cwd" ]] || {
                log "skip $(basename "$projdir"): no cwd in JSONL"
                continue
            }
            project_id=$(basename "$cwd")
            [[ -n "$project_id" ]] || continue

            workspace_dir="$WORKSPACE_ROOT/$project_id"
            if [[ ! -d "$workspace_dir" ]]; then
                log "skip $project_id: workspace $workspace_dir not present"
                continue
            fi

            if tmux list-windows -t "$TMUX_SESSION" -F '#W' 2>/dev/null | grep -qx "$project_id"; then
                log "skip $project_id: tmux window already exists"
                continue
            fi

            # Respect user intent: do NOT resurrect a session the user
            # deliberately stopped. ExitEndpoint tombstones owner='none' on
            # Stop; ModeWatcher sets owner='pc' when the PC native ext owns it.
            # Only owner='tmux' (was a live Bridge session) or an untracked
            # project (no row — e.g. fresh host reboot) is eligible to resume.
            # Live failure 2026-05-22: three cc-bridge rebuilds each re-spawned
            # sessions the user had stopped. Best-effort: if sqlite3/db missing
            # we fall through to resume (preserves prior behavior on a fresh
            # host; never blocks startup).
            if command -v sqlite3 >/dev/null 2>&1 && [[ -f "$OWNERSHIP_DB" ]]; then
                # Escape single quotes (double them) for the SQL literal.
                pid_sql="${project_id//\'/\'\'}"
                owner=$(sqlite3 "$OWNERSHIP_DB" \
                    "SELECT owner FROM session_ownership WHERE project_id='$pid_sql' LIMIT 1;" \
                    2>/dev/null || true)
                case "$owner" in
                    none|pc)
                        log "skip $project_id: owner=$owner (user-stopped or PC-owned — not resurrecting)"
                        continue
                        ;;
                esac
            fi

            session_uuid=$(basename "$latest_jsonl" .jsonl)
            log "resume $project_id → claude --resume $session_uuid (on host)"
            # -c uses the WORKSPACE path. Host has a /workspace symlink to
            # /home/youruser/workspace so the same path works on both sides.
            # Run via a LOGIN shell: this new-window is created by the container's
            # tmux client, so the host window inherits a minimal PATH without
            # ~/.local/bin where the native `claude` lives → a bare `claude`
            # resolves to nothing (root /usr/bin/claude removed) and the window
            # dies instantly. `bash -lc` sources the host profile (PATH fixed);
            # `exec` keeps claude as the pane process. Same fix as TmuxClient.
            tmux new-window -t "$TMUX_SESSION" -n "$project_id" -c "$workspace_dir" \
                "bash -lc 'exec claude --resume $session_uuid'" || log "resume failed for $project_id (window will close)"
        done
    else
        log "no CC projects dir at $CC_PROJECTS_ROOT — fresh start"
    fi

    log "windows: $(tmux list-windows -t "$TMUX_SESSION" -F '#W' 2>/dev/null | tr '\n' ',' | sed 's/,$//')"
fi

# 3) Hand off to bridge
log "starting bridge"
exec dotnet /app/bridge-api.dll
