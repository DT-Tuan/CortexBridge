#!/usr/bin/env bash
# CC Stop hook (HOST install) -> bridge /internal/hooks/stop.
# Per ADR-013, claude runs on host; the bridge container exposes port 3000 on
# the host's loopback. Defaults here match those host-side paths.
set -euo pipefail

PAYLOAD=$(cat)
TOKEN_FILE="${BRIDGE_HOOK_TOKEN_FILE:-/opt/cortex/data/sqlite/bridge-hook-token}"
BRIDGE_URL="${BRIDGE_INTERNAL_URL:-http://127.0.0.1:3000}"

if [[ ! -r "$TOKEN_FILE" ]]; then
  logger -t cc-hook "stop-hook: token file not readable: $TOKEN_FILE" || true
  exit 0
fi
TOKEN=$(< "$TOKEN_FILE")

PROJECT_ID=$(jq -r '.cwd // empty | split("/") | last' <<<"$PAYLOAD")
if [[ -z "$PROJECT_ID" ]]; then
  logger -t cc-hook "stop-hook: cwd missing from payload" || true
  exit 0
fi

ENRICHED=$(jq --arg p "$PROJECT_ID" '. + {projectId: $p}' <<<"$PAYLOAD")

curl -fsS --max-time 5 -X POST "$BRIDGE_URL/internal/hooks/stop" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "$ENRICHED" \
  > /dev/null \
  || logger -t cc-hook "stop-hook: POST failed for $PROJECT_ID" || true

exit 0
