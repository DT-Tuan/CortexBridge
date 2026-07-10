#!/usr/bin/env bash
# migrate-project / bundle — SOURCE-side: package one project's Claude state
# (JSONL transcripts + per-project memory) into a transferable tarball +
# manifest. See SKILL.md for design + clarify brief.
#
# Usage: bundle.sh <project-absolute-path>
#   e.g. bundle.sh /home/me/projects/foo
#
# Output (in $PWD):
#   migrate-<projectname>-<UTC>.tar.gz
#   migrate-<projectname>-<UTC>.manifest.json

set -euo pipefail

if [[ $# -ne 1 || "$1" == "-h" || "$1" == "--help" ]]; then
    cat <<'USAGE' >&2
Usage: bundle.sh <project-absolute-path>
Example: bundle.sh /home/me/projects/foo
USAGE
    exit 64
fi

FROM_PATH="$1"

# Refuse a non-absolute path — encoded form would be ambiguous.
if [[ "$FROM_PATH" != /* ]]; then
    echo "ERROR: project path must be absolute (got: $FROM_PATH)" >&2
    exit 64
fi

# Encode the absolute path the way Claude does: replace '/' with '-'. The
# leading '/' becomes a leading '-' which is correct (matches existing
# ~/.claude/projects/-home-... entries).
ENCODED=$(printf '%s' "$FROM_PATH" | tr '/' '-')
SRC_DIR="$HOME/.claude/projects/$ENCODED"

if [[ ! -d "$SRC_DIR" ]]; then
    echo "ERROR: no Claude state at $SRC_DIR" >&2
    echo "(expected: project path '$FROM_PATH' encodes to '$ENCODED')" >&2
    echo "Available encoded paths under ~/.claude/projects/:" >&2
    ls -1 "$HOME/.claude/projects/" 2>/dev/null | sed 's/^/  /' >&2
    exit 66
fi

# Preflight — warn on recently-modified JSONL (likely an active session).
NOW=$(date -u +%s)
RECENT=0
while IFS= read -r -d '' jsonl; do
    mtime=$(stat -c %Y "$jsonl")
    if (( NOW - mtime < 300 )); then
        RECENT=$((RECENT + 1))
    fi
done < <(find "$SRC_DIR" -maxdepth 1 -name '*.jsonl' -print0)
if (( RECENT > 0 )); then
    echo "WARN: $RECENT JSONL file(s) modified in the last 5 minutes — likely active session(s)." >&2
    echo "      For a clean snapshot, /exit those sessions first, then re-run bundle.sh." >&2
    echo "      Proceeding anyway in 5 seconds (Ctrl-C to abort)..." >&2
    sleep 5
fi

# Derive a friendly project name (last path component).
PROJECT_NAME=$(basename "$FROM_PATH")
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
OUT_BASE="migrate-${PROJECT_NAME}-${STAMP}"
OUT_TAR="${OUT_BASE}.tar.gz"
OUT_MANIFEST="${OUT_BASE}.manifest.json"

# Build the manifest BEFORE tarring so we can include the manifest itself.
TMP_MANIFEST=$(mktemp)
{
    echo '{'
    echo "  \"skill\": \"migrate-project\","
    echo "  \"version\": 1,"
    echo "  \"bundledUtc\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\","
    echo "  \"projectName\": \"$PROJECT_NAME\","
    echo "  \"sourceAbsolutePath\": \"$FROM_PATH\","
    echo "  \"sourceEncodedPath\": \"$ENCODED\","
    echo '  "sessions": ['
    first=1
    for jsonl in "$SRC_DIR"/*.jsonl; do
        [[ -e "$jsonl" ]] || continue
        uuid=$(basename "$jsonl" .jsonl)
        bytes=$(stat -c %s "$jsonl")
        mtime=$(stat -c %Y "$jsonl")
        mtime_iso=$(date -u -d "@$mtime" +%Y-%m-%dT%H:%M:%SZ)
        if (( first == 0 )); then echo ','; fi
        first=0
        printf '    {"uuid": "%s", "bytes": %s, "mtime": "%s"}' "$uuid" "$bytes" "$mtime_iso"
    done
    echo ''
    echo '  ],'
    if [[ -d "$SRC_DIR/memory" ]]; then
        mem_count=$(find "$SRC_DIR/memory" -maxdepth 1 -type f | wc -l | tr -d ' ')
        echo "  \"memoryFileCount\": $mem_count"
    else
        echo '  "memoryFileCount": 0'
    fi
    echo '}'
} > "$TMP_MANIFEST"
mv "$TMP_MANIFEST" "$OUT_MANIFEST"

# Tar the source dir. Use -C so the archive contains the encoded-path dir at
# the top level (install rebuilds the path from there). The `--` separator is
# load-bearing: $ENCODED starts with '-' (it encodes a leading '/'), and
# without -- tar parses it as a chain of option flags ("-h -o -m -e ...").
tar -czf "$OUT_TAR" -C "$HOME/.claude/projects" -- "$ENCODED"

TAR_BYTES=$(stat -c %s "$OUT_TAR")
JSONL_COUNT=$(find "$SRC_DIR" -maxdepth 1 -name '*.jsonl' | wc -l | tr -d ' ')

cat <<DONE
✅ migrate-project bundle complete

  project       : $PROJECT_NAME
  source path   : $FROM_PATH
  encoded path  : $ENCODED
  JSONL count   : $JSONL_COUNT
  tarball       : $PWD/$OUT_TAR ($(numfmt --to=iec "$TAR_BYTES" 2>/dev/null || echo "$TAR_BYTES bytes"))
  manifest      : $PWD/$OUT_MANIFEST

📦 Transfer:
  scp  : scp $OUT_TAR $OUT_MANIFEST user@target:/path/
  rsync: rsync -av $OUT_TAR $OUT_MANIFEST user@target:/path/

🔐 If JSONL may contain secrets (pasted keys, tokens), encrypt before transit:
  age -p -o $OUT_TAR.age $OUT_TAR
  # decrypt on target:  age -d -o $OUT_TAR $OUT_TAR.age

🎯 Then on the TARGET machine:
  bash scripts/migrate/install.sh \\
       $OUT_TAR \\
       <new-absolute-path-on-target>
DONE
