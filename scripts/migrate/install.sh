#!/usr/bin/env bash
# migrate-project / install — TARGET-side: extract a bundle into the correct
# encoded-path under ~/.claude/projects/, verify byte counts against the
# manifest, surface MCP re-auth requirements + smoke recipe.
#
# Usage: install.sh <bundle.tar.gz> <new-absolute-path> [--force]
#   e.g. install.sh migrate-foo-20260520T123456Z.tar.gz /home/youruser/workspace/foo

set -euo pipefail

FORCE=0
if [[ "${3:-}" == "--force" ]]; then FORCE=1; fi

if [[ $# -lt 2 || "$1" == "-h" || "$1" == "--help" ]]; then
    cat <<'USAGE' >&2
Usage: install.sh <bundle.tar.gz> <new-absolute-path> [--force]
Example: install.sh migrate-foo-20260520T...tar.gz /home/youruser/workspace/foo
        (add --force to overwrite an existing non-empty target encoded dir)
USAGE
    exit 64
fi

TAR="$1"
TO_PATH="$2"

if [[ ! -f "$TAR" ]]; then
    echo "ERROR: bundle not found: $TAR" >&2
    exit 66
fi
if [[ "$TO_PATH" != /* ]]; then
    echo "ERROR: target path must be absolute (got: $TO_PATH)" >&2
    exit 64
fi

# Manifest sibling — same basename, .manifest.json suffix.
MANIFEST="${TAR%.tar.gz}.manifest.json"
if [[ ! -f "$MANIFEST" ]]; then
    echo "ERROR: manifest sibling not found: $MANIFEST" >&2
    echo "(install.sh expects the manifest produced by bundle.sh alongside the tarball)" >&2
    exit 66
fi

# Read source encoded-path from manifest (jq if available, fall back to grep).
if command -v jq >/dev/null 2>&1; then
    SOURCE_ENCODED=$(jq -r '.sourceEncodedPath' "$MANIFEST")
    PROJECT_NAME=$(jq -r '.projectName' "$MANIFEST")
else
    SOURCE_ENCODED=$(grep -oE '"sourceEncodedPath"[[:space:]]*:[[:space:]]*"[^"]+"' "$MANIFEST" | sed -E 's/.*"sourceEncodedPath"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')
    PROJECT_NAME=$(grep -oE '"projectName"[[:space:]]*:[[:space:]]*"[^"]+"' "$MANIFEST" | sed -E 's/.*"projectName"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')
fi
if [[ -z "$SOURCE_ENCODED" ]]; then
    echo "ERROR: could not read sourceEncodedPath from $MANIFEST" >&2
    exit 65
fi

TARGET_ENCODED=$(printf '%s' "$TO_PATH" | tr '/' '-')
TARGET_DIR="$HOME/.claude/projects/$TARGET_ENCODED"

# Idempotency guard.
if [[ -d "$TARGET_DIR" ]] && [[ -n "$(ls -A "$TARGET_DIR" 2>/dev/null)" ]]; then
    if (( FORCE == 0 )); then
        echo "ERROR: target encoded-path dir already exists + non-empty: $TARGET_DIR" >&2
        echo "       (re-run with --force to overwrite — destructive)" >&2
        exit 73
    fi
    echo "WARN: --force given; removing existing $TARGET_DIR" >&2
    rm -rf "$TARGET_DIR"
fi

# Stage extraction in a temp dir to avoid partial state on failure.
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
tar -xzf "$TAR" -C "$STAGE"
SRC_STAGED="$STAGE/$SOURCE_ENCODED"
if [[ ! -d "$SRC_STAGED" ]]; then
    echo "ERROR: tarball did not contain expected dir $SOURCE_ENCODED" >&2
    exit 65
fi

mkdir -p "$(dirname "$TARGET_DIR")"
mv "$SRC_STAGED" "$TARGET_DIR"

# Byte-count verify per manifest. Read each session's expected bytes + compare
# against the placed file.
echo
echo "Verifying byte counts against manifest..."
if command -v jq >/dev/null 2>&1; then
    MISMATCH=0
    while IFS=$'\t' read -r uuid expected; do
        placed="$TARGET_DIR/$uuid.jsonl"
        if [[ ! -f "$placed" ]]; then
            echo "  MISSING: $uuid.jsonl" >&2
            MISMATCH=$((MISMATCH + 1))
            continue
        fi
        actual=$(stat -c %s "$placed")
        if [[ "$actual" != "$expected" ]]; then
            echo "  MISMATCH: $uuid.jsonl  manifest=$expected  placed=$actual" >&2
            MISMATCH=$((MISMATCH + 1))
        else
            echo "  ok: $uuid.jsonl ($expected bytes)"
        fi
    done < <(jq -r '.sessions[] | [.uuid, .bytes] | @tsv' "$MANIFEST")
    if (( MISMATCH > 0 )); then
        echo "ERROR: $MISMATCH file(s) failed verification" >&2
        exit 70
    fi
else
    echo "  (jq not installed — skipping per-session byte-count verify; manifest is at $MANIFEST)"
fi

# MCP re-auth list.
MCP_CACHE="$HOME/.claude/mcp-needs-auth-cache.json"
echo
if [[ -f "$MCP_CACHE" ]]; then
    echo "🔐 MCP servers may need re-auth on this machine (OAuth flows are device-bound):"
    if command -v jq >/dev/null 2>&1; then
        jq -r 'keys[]?' "$MCP_CACHE" 2>/dev/null | sed 's/^/  /' || cat "$MCP_CACHE"
    else
        cat "$MCP_CACHE"
    fi
    echo "  (Re-auth manually per server — automation here is not possible.)"
else
    echo "🔐 No global MCP needs-auth cache present — if your project uses MCP servers,"
    echo "   verify their auth state and re-authenticate as needed."
fi

cat <<DONE

✅ migrate-project install complete

  project       : $PROJECT_NAME
  target path   : $TO_PATH
  encoded path  : $TARGET_ENCODED
  placed at     : $TARGET_DIR

🎯 Smoke-test:
  1. Make sure the repo is actually at $TO_PATH (clone/rsync separately if not).
  2. cd $TO_PATH
  3. claude  # or: claude --resume <uuid-from-manifest>

If resume fails, check:
  - Repo really exists at $TO_PATH (the encoded path derives from cwd at claude start)
  - Global ~/.claude state (settings.json, MCP, skills) is migrated via the
    one-shot docs/runbooks/migrate-claude-config-to-vps.md
DONE
