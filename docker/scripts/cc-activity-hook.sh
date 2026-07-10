#!/usr/bin/env bash
# CC turn-lifecycle hook (HOST install -> ~/.local/bin/cc-activity-hook.sh)
# -> bridge /internal/hooks/activity.
# Wired to UserPromptSubmit / PreToolUse / PostToolUse / SessionStart / SessionEnd /
# SubagentStop so the bridge can keep an AUTHORITATIVE per-project status
# (processing vs idle vs awaiting_input) instead of guessing from JSONL shape.
#
# Also forwards CC's own `session_id` so the bridge can RE-POINT the live-slot
# marker when CC switches sessions on its own (e.g. /clear starts a fresh
# session UUID in the same tmux window). Without this the marker stays pinned to
# the pre-/clear UUID and needsInput/push/session_switch all key onto the dead
# session (GitHub issue #1).
#
# HARD RULES (this runs on PreToolUse — a misbehaving hook can DENY tool calls):
#   - never exit non-zero, never print to stdout
#   - never block: fire-and-forget the POST with a short timeout, return at once
# Arg $1 = event kind (UserPromptSubmit|PreToolUse|PostToolUse|SessionStart|...).
# NB: deliberately NO `set -e` — a failed jq/curl must not fail the hook.

KIND="${1:-unknown}"
PAYLOAD="$(cat 2>/dev/null || true)"
TOKEN_FILE="${BRIDGE_HOOK_TOKEN_FILE:-/opt/cortex/data/sqlite/bridge-hook-token}"
BRIDGE_URL="${BRIDGE_INTERNAL_URL:-http://127.0.0.1:3000}"

{
  [ -r "$TOKEN_FILE" ] || exit 0
  TOKEN="$(< "$TOKEN_FILE")"
  PROJECT_ID="$(jq -r '.cwd // empty | split("/") | last' <<<"$PAYLOAD" 2>/dev/null)"
  [ -n "$PROJECT_ID" ] || exit 0
  SESSION_ID="$(jq -r '.session_id // empty' <<<"$PAYLOAD" 2>/dev/null)"
  # sessionId only when present — keeps it null server-side otherwise (empty
  # string would be a distinct, wrong key).
  BODY="$(jq -nc --arg p "$PROJECT_ID" --arg k "$KIND" --arg s "$SESSION_ID" \
    '{projectId:$p, kind:$k} + (if $s != "" then {sessionId:$s} else {} end)' 2>/dev/null)"
  [ -n "$BODY" ] || exit 0
  # Background + capped timeout: claude never waits on the bridge here.
  curl -fsS --max-time 2 -X POST "$BRIDGE_URL/internal/hooks/activity" \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -d "$BODY" >/dev/null 2>&1 &
} >/dev/null 2>&1

exit 0
