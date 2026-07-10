#!/usr/bin/env bash
# CC PreToolUse auto-allow hook (HOST install -> ~/.local/bin/cc-autoallow-hook.sh).
#
# Architecture C (project_autoallow_design.md): instead of the bridge auto-tapping
# "1" at a permission prompt (prompt flashes + pane race), this hook returns
# {permissionDecision:"allow"} for allowed tool calls so CC runs them with NO
# prompt at all. Native, runtime-toggleable, zero pane race.
#
# TWO independent per-project tiers (flag files under $CLAUDE_DIR/cortex-autoallow/,
# created/removed by the bridge on the PWA toggles; default OFF). projectId = basename(cwd):
#   <id>.on        -> SAFE tier: read-only tools + read-only Bash (single OR chained
#                     read-only commands). Nothing here can write/network/delete.
#   <id>.autonomy  -> TRUST tier (implies read-only): also allow build/test/lint/format
#                     and local git (add/commit/stash). NOTE: build/test EXECUTE
#                     ARBITRARY CODE (package scripts, test code) — this tier is a
#                     statement of TRUST in the project, not a safety guarantee.
#       <id>.push    -> sub-flag: also allow `git push` (NOT --force). outward + hard
#                       to undo; off by default even under autonomy.
#       <id>.install -> sub-flag: also allow package installs (npm/pnpm/yarn install,
#                       dotnet restore, pip install) — postinstall scripts + network.
#
# ALWAYS-UNSAFE (even under autonomy): destructive commands (rm/dd/mkfs, git reset
# --hard, git clean, git push --force, git branch -D, force checkout) and anything
# carrying shell redirection / command substitution / subshell / backgrounding.
# Anything not provably allowed falls through with no output -> CC prompts as usual.
#
# HARD RULES (this is in CC's critical path — a misbehaving hook can WRONGLY allow or
# can hang the turn):
#   - decision MUST be a single synchronous JSON line on stdout, then exit 0
#   - never exit non-zero (a crashed hook must degrade to "normal prompt", not deny)
#   - audit POST is fire-and-forget in the background, never on the decision path
# NB: deliberately NO `set -e`.
#
# Compound-command safety note: we split on the shell operators && || ; | and require
# EVERY segment to be independently allowed. We split even inside quotes (we do not
# parse quoting) — this is intentionally fail-safe: a separator hidden in a quoted arg
# makes the segment unparseable and the command falls through to a prompt, never to a
# wrong allow. Redirection/substitution/subshell/backgrounding are rejected outright.

PAYLOAD="$(cat 2>/dev/null || true)"
CLAUDE_DIR="${CC_AUTOALLOW_CLAUDE_DIR:-$HOME/.claude}"
FLAG_DIR="$CLAUDE_DIR/cortex-autoallow"
TOKEN_FILE="${BRIDGE_HOOK_TOKEN_FILE:-/opt/cortex/data/sqlite/bridge-hook-token}"
BRIDGE_URL="${BRIDGE_INTERNAL_URL:-http://127.0.0.1:3000}"

# --- resolve project + gate on the per-project flags (cheapest checks first) ---
PROJECT_ID="$(jq -r '.cwd // empty | split("/") | last' <<<"$PAYLOAD" 2>/dev/null)"
[ -n "$PROJECT_ID" ] || exit 0

RO_ON=0; AUTON=0; PUSH_OK=0; INSTALL_OK=0
[ -f "$FLAG_DIR/$PROJECT_ID.on" ] && RO_ON=1
[ -f "$FLAG_DIR/$PROJECT_ID.autonomy" ] && AUTON=1
[ -f "$FLAG_DIR/$PROJECT_ID.push" ] && PUSH_OK=1
[ -f "$FLAG_DIR/$PROJECT_ID.install" ] && INSTALL_OK=1
[ "$AUTON" = 1 ] && RO_ON=1   # autonomy includes the read-only set
LEARNED_FILE="$FLAG_DIR/$PROJECT_ID.learned.json"
[ "$RO_ON" = 1 ] || [ -f "$LEARNED_FILE" ] || exit 0    # neither tier nor learned file -> never auto-allow

TOOL="$(jq -r '.tool_name // empty' <<<"$PAYLOAD" 2>/dev/null)"
[ -n "$TOOL" ] || exit 0

# --- allowlists ---------------------------------------------------------------
# Read-only simple programs (matched by basename). `find` is EXCLUDED (-exec/-delete);
# `sort` is EXCLUDED (-o/--output writes). git/env are handled specially below.
SIMPLE_SAFE=" ls cat pwd head tail wc echo file stat du df tree which whoami date \
grep rg fd jq cut uniq comm nl tac tr basename dirname realpath readlink column \
printenv sha256sum sha1sum md5sum cksum "
# git read-only subcommands (config handled separately — it can also write).
GIT_RO=" status diff log branch show remote tag describe rev-parse ls-files blame \
shortlog cat-file ls-tree rev-list for-each-ref show-ref reflog symbolic-ref whatchanged "

is_simple_safe() { case "$SIMPLE_SAFE" in *" ${1##*/} "*) return 0;; esac; return 1; }

# Returns 0 if ANY arg looks like a secret/credential path -> refuse (prompt). Broad
# on purpose: a false positive only costs a prompt, never a leak.
args_have_secret() {
  local t
  for t in "$@"; do
    case "$t" in
      */.ssh/*|.ssh/*|*id_rsa*|*id_ed25519*|*id_ecdsa*|*id_dsa*) return 0;;
      *.pem|*.key|*.p12|*.pfx) return 0;;
      *.credentials.json|*/.aws/*|*.aws/credentials) return 0;;
      .env|*/.env|.env.*|*/.env.*) return 0;;
      */.gnupg/*|*/.password-store/*|*cortex-secrets*) return 0;;
    esac
  done
  return 1
}

# Destructive commands are ALWAYS unsafe, even under autonomy (defense in depth — most
# also aren't allowlisted, but git reset/clean/push-force could otherwise sneak in).
is_destructive() {
  local prog="${1##*/}"; local joined=" $* "
  case "$prog" in
    rm|rmdir|shred|dd|mkfs|mkfs.*|fdisk|sfdisk|wipefs) return 0;;
  esac
  case "$joined" in *" --no-preserve-root "*) return 0;; esac
  if [ "$prog" = "git" ]; then
    case "$joined" in
      *" clean "*) return 0;;
      *" reset "*) case "$joined" in *" --hard "*) return 0;; esac;;
      *" push "*)  case "$joined" in *" --force "*|*" -f "*|*" --force-with-lease"*) return 0;; esac;;
      *" checkout "*) case "$joined" in *" --force "*|*" -f "*) return 0;; esac;;
      *" branch "*)   case "$joined" in *" -D "*) return 0;; esac;;
    esac
  fi
  return 1
}

# Build/test/lint/format runners (autonomy). These EXECUTE project code by design.
is_build_cmd() {
  local prog="${1##*/}"
  case "$prog" in
    vitest|eslint|prettier|tsc|svelte-check|jest|mocha|pytest) return 0;;
    npm|pnpm|yarn|bun)
      case "${2:-}" in
        test) return 0;;
        run) case "${3:-}" in
               build|test|lint|check|format|typecheck|type-check|size|coverage|ci|prepush*) return 0;;
             esac;;
      esac;;
    dotnet) case "${2:-}" in build|test|format) return 0;; esac;;
    make) return 0;;
  esac
  return 1
}

# Package installs (autonomy + .install): network + postinstall scripts.
is_install_cmd() {
  local prog="${1##*/}"
  case "$prog" in
    npm|pnpm|yarn|bun) case "${2:-}" in install|i|ci|add) return 0;; esac;;
    dotnet) case "${2:-}" in restore|add) return 0;; esac;;
    pip|pip3) case "${2:-}" in install) return 0;; esac;;
  esac
  return 1
}

# git config is read-only only when it carries a get/list flag (no value-set form).
config_is_readonly() {
  local t
  for t in "$@"; do
    case "$t" in --get|--get-all|--get-regexp|--list|-l) return 0;; esac
  done
  return 1
}

# Classify one git invocation (token array as args) -> SEGRES + SEGWHY.
classify_git() {
  local sub="" i n="$#"; local -a T=("$@")
  for ((i=1;i<n;i++)); do
    case "${T[i]}" in
      -C|-c) ((i++)); continue;;   # -C <path> / -c <k=v>
      -*) continue;;
      *) sub="${T[i]}"; break;;
    esac
  done
  [ -n "$sub" ] || { SEGRES=0; SEGWHY="git (no subcommand)"; return; }

  case " $GIT_RO " in
    *" $sub "*) [ "$RO_ON" = 1 ] && { SEGRES=1; SEGWHY="git $sub (read-only)"; return; };;
  esac
  if [ "$sub" = "config" ] && config_is_readonly "${T[@]}"; then
    [ "$RO_ON" = 1 ] && { SEGRES=1; SEGWHY="git config (read-only)"; return; }
  fi
  if [ "$AUTON" = 1 ]; then
    case "$sub" in
      add|commit|stash) SEGRES=1; SEGWHY="autonomy:git $sub"; return;;
      push) [ "$PUSH_OK" = 1 ] && { SEGRES=1; SEGWHY="autonomy:git push"; return; };;
    esac
  fi
  SEGRES=0; SEGWHY="git $sub (not allowed)"
}

# Classify one command's token array -> SEGRES (1 safe / 0 unsafe) + SEGWHY.
classify_tokens() {
  [ "$#" -ge 1 ] || { SEGRES=1; SEGWHY="empty"; return; }
  local prog="${1##*/}"

  if is_destructive "$@"; then SEGRES=0; SEGWHY="destructive"; return; fi
  if [ "$prog" = "git" ]; then classify_git "$@"; return; fi

  if [ "$prog" = "env" ]; then
    local -a rest=(); local seen=0 t
    for t in "${@:2}"; do
      if [ "$seen" = 0 ]; then case "$t" in -*|*=*) continue;; esac; seen=1; fi
      rest+=("$t")
    done
    if [ "${#rest[@]}" -eq 0 ]; then
      [ "$RO_ON" = 1 ] && { SEGRES=1; SEGWHY="bare env"; return; }
      SEGRES=0; SEGWHY="env"; return
    fi
    classify_tokens "${rest[@]}"; return
  fi

  if [ "$AUTON" = 1 ] && is_build_cmd "$@"; then
    SEGRES=1; SEGWHY="autonomy:build $prog"; return
  fi
  if [ "$AUTON" = 1 ] && [ "$INSTALL_OK" = 1 ] && is_install_cmd "$@"; then
    SEGRES=1; SEGWHY="autonomy:install $prog"; return
  fi

  if [ "$RO_ON" = 1 ] && is_simple_safe "$prog"; then
    if args_have_secret "${@:2}"; then SEGRES=0; SEGWHY="secret-path"; return; fi
    SEGRES=1; SEGWHY="read-only $prog"; return
  fi

  SEGRES=0; SEGWHY="$prog (not allowed)"
}

# Classify a full Bash command line (may be a && || ; | chain) -> 0 (allow) + ALLREASON.
classify_command() {
  local cmd="$1"
  [ -n "$cmd" ] || return 1
  case "$cmd" in *$'\n'*|*$'\r'*) return 1;; esac          # 2nd line would run unseen
  case "$cmd" in *[\<\>\`\$\(\)]*) return 1;; esac          # redirect/subst/subshell
  case "${cmd//&&/}" in *"&"*) return 1;; esac              # lone & = backgrounding

  local sep=$'\x01' norm="$cmd"
  norm="${norm//&&/$sep}"; norm="${norm//||/$sep}"; norm="${norm//;/$sep}"; norm="${norm//|/$sep}"

  local -a SEGS=(); local IFS="$sep"
  read -ra SEGS <<<"$norm"
  unset IFS

  ALLREASON=""; local any=0 seg
  for seg in "${SEGS[@]}"; do
    seg="${seg#"${seg%%[![:space:]]*}"}"; seg="${seg%"${seg##*[![:space:]]}"}"
    [ -n "$seg" ] || continue
    any=1
    local -a T=(); read -ra T <<<"$seg"
    classify_tokens "${T[@]}"
    [ "$SEGRES" = 1 ] || return 1
    ALLREASON="${ALLREASON:+$ALLREASON + }$SEGWHY"
  done
  [ "$any" = 1 ] || return 1
  return 0
}

# --- decide -------------------------------------------------------------------
safe=0
reason=""
case "$TOOL" in
  Read|Grep|Glob|LS)
    if [ "$RO_ON" = 1 ]; then
      safe=1; reason="read-only tool $TOOL"
    elif [ -f "$LEARNED_FILE" ]; then
      _found="$(jq -r --arg t "$TOOL" '.tools[]? | select(. == $t)' "$LEARNED_FILE" 2>/dev/null)"
      [ -n "$_found" ] && { safe=1; reason="learned:$TOOL"; }
    fi
    ;;
  Bash)
    CMD="$(jq -r '.tool_input.command // empty' <<<"$PAYLOAD" 2>/dev/null)"
    if classify_command "$CMD"; then
      safe=1; reason="$ALLREASON"
    elif [ -f "$LEARNED_FILE" ] && [ -n "$CMD" ]; then
      _found="$(jq -r --arg cmd "$CMD" '.bashCommands[]? | select(. == $cmd)' "$LEARNED_FILE" 2>/dev/null)"
      [ -n "$_found" ] && { safe=1; reason="learned:bash"; }
    fi
    ;;
  *)
    # Write, Edit, TodoWrite, mcp__*, WebFetch, etc.: check learned file only.
    # No tier-based allow — user must have explicitly approved this tool from PWA before.
    if [ -f "$LEARNED_FILE" ] && [ -n "$TOOL" ]; then
      _found="$(jq -r --arg t "$TOOL" '.tools[]? | select(. == $t)' "$LEARNED_FILE" 2>/dev/null)"
      [ -n "$_found" ] && { safe=1; reason="learned:$TOOL"; }
    fi
    ;;
esac

[ "$safe" = 1 ] || exit 0   # not provably allowed -> no output -> CC prompts as usual

# --- emit the allow decision (synchronous, single line) ---
jq -nc --arg r "$reason" '{
  hookSpecificOutput: {
    hookEventName: "PreToolUse",
    permissionDecision: "allow",
    permissionDecisionReason: ("cortex-autoallow: " + $r)
  }
}'

# --- fire-and-forget audit (never blocks the decision) ---
# Only Bash auto-allows are interesting to audit. Read/Grep/Glob/LS are already
# default-allowed by CC (the hook is a no-op for them) — auditing every one would
# just flood audit_log, so skip them.
[ "$TOOL" = "Bash" ] || exit 0
{
  [ -r "$TOKEN_FILE" ] || exit 0
  TOKEN="$(< "$TOKEN_FILE")"
  BODY="$(jq -nc --arg p "$PROJECT_ID" --arg t "$TOOL" --arg r "$reason" \
    '{projectId:$p, tool:$t, reason:$r}' 2>/dev/null)"
  [ -n "$BODY" ] || exit 0
  curl -fsS --max-time 2 -X POST "$BRIDGE_URL/internal/hooks/autoallow" \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -d "$BODY" >/dev/null 2>&1 &
} >/dev/null 2>&1

exit 0
