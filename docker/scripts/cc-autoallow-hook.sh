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
# EVERY segment to be independently allowed. The split itself is quote-blind, but each
# segment is then resolved to shell-faithful literal argv (resolve_tokens) BEFORE the
# floor/allowlist see it, so quoting/escaping can't hide a dangerous token (e.g.
# `git push "--force"`) from is_destructive. A separator hidden in a quoted arg leaves a
# segment with an unterminated quote -> resolve_tokens fails -> prompt, never a wrong
# allow. Redirection/substitution/subshell/backgrounding are rejected outright.

PAYLOAD="$(cat 2>/dev/null || true)"
CLAUDE_DIR="${CC_AUTOALLOW_CLAUDE_DIR:-$HOME/.claude}"
FLAG_DIR="$CLAUDE_DIR/cortex-autoallow"
TOKEN_FILE="${BRIDGE_HOOK_TOKEN_FILE:-/opt/cortex/data/sqlite/bridge-hook-token}"
BRIDGE_URL="${BRIDGE_INTERNAL_URL:-http://127.0.0.1:3000}"

# --- resolve project + gate on the per-project flags (cheapest checks first) ---
CWD="$(jq -r '.cwd // empty' <<<"$PAYLOAD" 2>/dev/null)"
PROJECT_ID="${CWD##*/}"
[ -n "$PROJECT_ID" ] || exit 0

RO_ON=0; AUTON=0; PUSH_OK=0; INSTALL_OK=0
[ -f "$FLAG_DIR/$PROJECT_ID.on" ] && RO_ON=1
[ -f "$FLAG_DIR/$PROJECT_ID.autonomy" ] && AUTON=1
[ -f "$FLAG_DIR/$PROJECT_ID.push" ] && PUSH_OK=1
[ -f "$FLAG_DIR/$PROJECT_ID.install" ] && INSTALL_OK=1
[ "$AUTON" = 1 ] && RO_ON=1   # autonomy includes the read-only set

# ADR-028 (A): the READ-ONLY tier is ON BY DEFAULT for projects under a workspace
# root (where real dev happens) — so reads / read-only Bash stop prompting without a
# per-project opt-in — UNLESS the user opted this project out with a .ro-off file.
# Dirs OUTSIDE the workspace (e.g. ~/.ssh, /etc) stay gated. Only the read tier is
# defaulted; autonomy/push/install stay explicit opt-in. Roots are colon-separated
# (CC_AUTOALLOW_RO_ROOTS), default ~/workspace.
if [ "$RO_ON" != 1 ] && [ ! -f "$FLAG_DIR/$PROJECT_ID.ro-off" ]; then
  case "$CWD" in
    ""|*..*) : ;;                    # empty or non-canonical path -> never default-on
    *)
      IFS=':' read -ra _RO_ROOTS <<<"${CC_AUTOALLOW_RO_ROOTS:-$HOME/workspace}"
      for _r in "${_RO_ROOTS[@]}"; do
        [ -n "$_r" ] || continue
        if [ "$CWD" = "$_r" ] || [ "${CWD#"$_r"/}" != "$CWD" ]; then
          RO_ON=1; break
        fi
      done
      ;;
  esac
fi

# ADR-028 (B): time-boxed autonomy BURST. A .burst flag's first line is an expiry epoch
# (optional 2nd word "opaque"). While unexpired it grants autonomy + installs (Class Y),
# auto-expiring; the Class-X floor + secret denylist still apply (classify_command is
# unchanged). "opaque" additionally lets normally-unanalyzable commands (ssh bodies,
# heredocs, $()) run — but only past the opaque_backstop RAW scan (see below).
BURST=0; OPAQUE_OK=0
if [ -f "$FLAG_DIR/$PROJECT_ID.burst" ]; then
  read -r _bexp _bopq _brest < "$FLAG_DIR/$PROJECT_ID.burst" 2>/dev/null || true
  case "$_bexp" in
    ''|*[!0-9]*) : ;;                               # malformed expiry -> ignore (fail safe)
    *) if [ "$_bexp" -gt "$(date +%s)" ]; then
         BURST=1; RO_ON=1; AUTON=1; INSTALL_OK=1
         [ "$_bopq" = "opaque" ] && OPAQUE_OK=1
       fi ;;
  esac
fi

LEARNED_FILE="$FLAG_DIR/$PROJECT_ID.learned.json"
[ "$RO_ON" = 1 ] || [ -f "$LEARNED_FILE" ] || [ "$BURST" = 1 ] || exit 0    # neither tier nor learned file nor burst -> never auto-allow

TOOL="$(jq -r '.tool_name // empty' <<<"$PAYLOAD" 2>/dev/null)"
[ -n "$TOOL" ] || exit 0

# NEVER auto-allow interactive/meta tools, even if a (stale) learned file lists one.
# AskUserQuestion / ExitPlanMode exist to elicit a USER decision; a PreToolUse allow
# on them silently bypasses that and corrupts the AskUserQuestion -> PWA surfacing
# flow. Defense-in-depth: the bridge already refuses to learn them (NeverLearnTools),
# this guards a file poisoned before that fix. Degrade to a normal prompt (exit 0).
case "$TOOL" in
  AskUserQuestion|ExitPlanMode) exit 0;;
esac

# --- allowlists ---------------------------------------------------------------
# Read-only simple programs (matched by basename). `find` is EXCLUDED (-exec/-delete);
# `sort` is EXCLUDED (-o/--output writes). git/env are handled specially below.
# `cd` is safe: it only moves the subshell's cwd (no write/network/delete). Its
# ONLY dangerous forms — `cd $(...)`, `cd `...``, `cd >f`, `cd x; rm` — are already
# rejected outright by the substitution/redirect/backgrounding guard and the
# per-segment split. Without it here, CC's ubiquitous `cd <project> && <read-only>`
# compound falls through to a manual permission prompt on every diagnostic command.
SIMPLE_SAFE=" ls cat pwd cd head tail wc echo file stat du df tree which whoami date \
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
      */.kube/*|.kube/config|*/.docker/config.json|.docker/config.json) return 0;;
      */.config/gcloud/*|.config/gcloud/*|*application_default_credentials*) return 0;;
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

# True if the command contains an UNQUOTED brace `{`/`}` — bash brace-expansion concatenates
# (r{m,m} -> "rm rm") and can hide a destructive token from a word scan, and unlike globs it
# expands with no matching file. Quoted braces/globs (regex in an ssh body, awk '{print}') are
# literal and safe, so they are NOT flagged. Mirrors has_unsafe_metachar's quote state machine.
has_unquoted_brace() {
  local s="$1"; local n=${#s} i=0 c st=N   # separate stmt: ${#s} needs s set first
  while [ "$i" -lt "$n" ]; do
    c="${s:$i:1}"
    case "$st" in
      N) case "$c" in \\) i=$((i+2)); continue;; \') st=S;; \") st=D;; '{'|'}') return 0;; esac;;
      S) [ "$c" = "'" ] && st=N;;
      D) case "$c" in \\) i=$((i+2)); continue;; \") st=N;; esac;;
    esac
    i=$((i+1))
  done
  return 1
}

# Opaque-ok backstop (ADR-028 B): during a burst with "opaque", commands the structured
# floor can't analyze (ssh '<body>', $(...), heredocs, non-allowlisted programs) may run.
# This RAW-string scan is then the ONLY floor left for them, so it is broad and fail-safe:
# block if the command mentions a destructive program, an irreversible flag, a brace/glob
# (bash could expand it into a hidden token), or a secret path — wherever it hides. It
# over-blocks on purpose (any ` -f `, ` -D `, ` clean `, any { } * ? [ ): a false block
# only costs a prompt, never a wrong allow.
opaque_backstop() {   # $1 = raw command; 0 = safe to allow, 1 = block (prompt)
  has_unquoted_brace "$1" && return 1   # unquoted brace-expansion can hide a token; quoted globs/regex are safe
  local ch sec=" $1 " s
  for ch in '"' "'" '`' '\'; do sec="${sec//"$ch"/}"; done                            # strip quotes/backslash: r''m -> rm, keep paths
  s="$sec"
  for ch in '$' '(' ')' ';' '|' '&' '<' '>' '=' ',' '/'; do s="${s//"$ch"/ }"; done   # + separators -> space (word view)
  s="${s//$'\t'/ }"; s="${s//$'\n'/ }"; s="${s//$'\r'/ }"
  case " $s " in
    *" rm "*|*" rmdir "*|*" shred "*|*" dd "*|*" mkfs "*|*" mkfs."*|*" fdisk "*|*" sfdisk "*|*" wipefs "*) return 1;;
    *" --no-preserve-root "*|*" --force "*|*" --force-with-lease "*|*" --hard "*) return 1;;
  esac
  case " $sec " in   # secret-path substrings (quotes stripped so ~/.ss''h -> ~/.ssh; separators/paths intact)
    */.ssh/*|*id_rsa*|*id_ed25519*|*id_ecdsa*|*id_dsa*|*.pem*|*.key*|*.p12*|*.pfx*) return 1;;
    *.credentials.json*|*/.aws/*|*.env*|*/.gnupg/*|*/.password-store/*|*cortex-secrets*) return 1;;
    */.kube/*|*/.docker/config.json*|*/.config/gcloud/*|*application_default_credentials*) return 1;;
  esac
  return 0
}

# Build/test/lint/format runners (autonomy). These EXECUTE project code by design.
is_build_cmd() {
  local prog="${1##*/}"
  case "$prog" in
    vitest|eslint|prettier|tsc|svelte-check|jest|mocha|pytest) return 0;;
    npm|pnpm|yarn|bun)
      case "${2:-}" in
        test) return 0;;
        run) case "${3:-}" in   # prefix-match so test:run / build:prod / lint:fix etc. count
               build*|test*|lint*|check*|format*|typecheck*|type-check*|size*|coverage*|ci|prepush*) return 0;;
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

# find is read-only EXCEPT the action flags that execute or write. `sed`/`awk` are
# deliberately NOT added: they are mini-languages that can write (sed `w`, `-i`) or
# execute (awk `system()`, sed `e`) from inside the script arg — not safely vettable
# without parsing that script. Use the learned path for those instead.
is_ro_find() {   # $@ = full argv including "find"
  local t
  for t in "${@:2}"; do
    case "$t" in
      -exec|-execdir|-ok|-okdir|-delete|-fprint|-fprintf|-fls|-fprint0) return 1;;
    esac
  done
  return 0
}
# sort is read-only unless it writes back via -o/--output.
is_ro_sort() {
  local t
  for t in "${@:2}"; do
    case "$t" in -o|-o?*|--output|--output=*) return 1;; esac
  done
  return 0
}
# sed is read-only ONLY in the narrow, unambiguous line-print form: read-safe flags
# (-n/-E/-r/-z + long spellings) + a script that is a pure numeric-range print
# (e.g. 60,110p / 5p / 10,$p / $p). Rejects -i/-e/-f (write/script-file/multi-expr) and
# any regex/command script (which could carry w/W/e/r/R = write/execute). Allowlist of
# safe forms, NOT a denylist of dangerous ones — so it cannot be tricked by an unlisted
# sed command. `awk` stays fully excluded (system()/print >file are unvettable).
is_ro_sed() {   # $@ = full argv incl "sed"; 0 = safe read-only line-print
  local t seen=0
  for t in "${@:2}"; do
    case "$t" in
      -n|-E|-r|-z|--quiet|--silent|--regexp-extended|--null-data) continue;;   # read-safe flags
      -*) return 1;;                                                            # -i/-e/-f/anything else -> unsafe
      *)
        if [ "$seen" = 0 ]; then
          seen=1
          [[ "$t" =~ ^([0-9]+(,([0-9]+|\$))?|\$)p$ ]] || return 1               # only <range>p / $p
        fi
        ;;                                                                      # later positionals = files
    esac
  done
  [ "$seen" = 1 ]
}
# Remove SAFE output redirects (N>/dev/null, N>>/dev/null, &>/dev/null, N>&M, N>&-) from
# the command BEFORE the metachar gate + classification, so ubiquitous `2>/dev/null` /
# `2>&1` stop forcing a prompt. Redirects to a REAL file (`> f`, `2> f`, `>> f`) do NOT
# match these patterns, keep their `>`, and are still rejected by has_unsafe_metachar.
# This only rewrites the CLASSIFICATION view; bash still runs the original (the redirect
# just goes to /dev/null / an fd-dup, which is harmless), so program-level safety is
# unchanged and is_destructive/args_have_secret still see the real program + paths.
strip_safe_redirects() {
  printf '%s' "$1" | sed -E \
    -e 's@(^|[[:space:]])[0-9]*&?>>?[[:space:]]*/dev/null([[:space:];|&]|$)@\1 \2@g' \
    -e 's@(^|[[:space:]])[0-9]*>&[0-9]+([[:space:];|&]|$)@\1 \2@g' \
    -e 's@(^|[[:space:]])[0-9]*>&-([[:space:];|&]|$)@\1 \2@g'
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

  if is_destructive "$@"; then SEGRES=0; SEGWHY="destructive"; FLOOR_HIT=1; return; fi
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

  if [ "$RO_ON" = 1 ] && [ "$prog" = "find" ] && is_ro_find "$@"; then
    if args_have_secret "${@:2}"; then SEGRES=0; SEGWHY="secret-path"; FLOOR_HIT=1; return; fi
    SEGRES=1; SEGWHY="read-only find"; return
  fi
  if [ "$RO_ON" = 1 ] && [ "$prog" = "sort" ] && is_ro_sort "$@"; then
    if args_have_secret "${@:2}"; then SEGRES=0; SEGWHY="secret-path"; FLOOR_HIT=1; return; fi
    SEGRES=1; SEGWHY="read-only sort"; return
  fi
  if [ "$RO_ON" = 1 ] && [ "$prog" = "sed" ] && is_ro_sed "$@"; then
    if args_have_secret "${@:2}"; then SEGRES=0; SEGWHY="secret-path"; FLOOR_HIT=1; return; fi
    SEGRES=1; SEGWHY="read-only sed"; return
  fi

  if [ "$RO_ON" = 1 ] && is_simple_safe "$prog"; then
    if args_have_secret "${@:2}"; then SEGRES=0; SEGWHY="secret-path"; FLOOR_HIT=1; return; fi
    SEGRES=1; SEGWHY="read-only $prog"; return
  fi

  SEGRES=0; SEGWHY="$prog (not allowed)"
}

# Reject shell metacharacters that are dangerous OUTSIDE quotes — command
# substitution $(...) / `...`, redirection < >, subshell ( ), backgrounding &, and
# brace/pathname expansion { } * ? [ (bash expands these BEFORE word-splitting, e.g.
# `git push --forc{e,e}` -> `--force`, which the resolver cannot model) —
# with CORRECT quote semantics so a QUOTED paren/redirect in a search pattern
# (grep "Foo(" / grep 'a>b') is NOT a false reject. Bash rules honored:
#   single quotes  -> everything literal (safe);
#   double quotes  -> $ and ` STILL active (subst/expand, unsafe) but ( ) < > & literal;
#   backslash      -> escapes the next char (outside quotes, and $ ` " \ inside "").
# && is a segment separator (allowed here; the split handles it); a lone & is not.
# ; and | are separators too and are intentionally NOT flagged here — the per-segment
# split + classify + is_destructive floor still gate each piece, so anything the
# quote-agnostic split mis-cuts fails to classify and PROMPTS (fails safe, never allows).
# Returns 0 if an unsafe metachar is present, 1 if clean.
has_unsafe_metachar() {
  local s="$1"; local n=${#s} i=0 c st=N   # st: N=none  S=single-quote  D=double-quote
  #                ^ separate stmt: `local s=.. n=${#s}` evaluates ${#s} before s is set (n=0)
  while [ "$i" -lt "$n" ]; do
    c="${s:$i:1}"
    case "$st" in
      N)
        case "$c" in
          \\) i=$((i+2)); continue;;                      # unquoted escape: skip next
          \') st=S;;
          \") st=D;;
          '$'|'`'|'<'|'>'|'('|')') return 0;;             # active subst/redirect/subshell
          '{'|'}'|'*'|'?'|'[') return 0;;                 # brace/pathname expansion: bash expands (e.g. --forc{e,e} -> --force) -> prompt
          '&') [ "${s:$((i+1)):1}" = "&" ] && i=$((i+1)) || return 0;;  # && ok, lone & no
        esac;;
      S)  case "$c" in \') st=N;; esac;;                   # single quote: all literal
      D)
        case "$c" in
          \\) i=$((i+2)); continue;;                       # dq escape: skip next
          '$'|'`') return 0;;                              # still active inside ""
          \") st=N;;
        esac;;                                             # ( ) < > & literal inside ""
    esac
    i=$((i+1))
  done
  [ "$st" = N ] || return 0   # unterminated quote -> reject
  return 1
}

# Resolve ONE segment into shell-faithful literal argv (global RESOLVED_TOKENS), so the
# floor (is_destructive/args_have_secret) sees the SAME tokens the shell would execute —
# closing the quote/escape dodge (e.g. `git push "--force"`, `git reset "--hard"`, which
# a raw `read -ra` leaves as the quote-carrying token "--force" that the substring floor
# then misses). Only the post-`has_unsafe_metachar` quoting subset can occur here
# (single/double quotes + backslash; NO $()/backtick/redirect/subshell). ALSO independently
# fail-safe: any active-expansion / redirect / subshell / lone-& / unterminated quote
# -> return 1 so the caller PROMPTS, never guesses.
resolve_tokens() {
  local s="$1"; local n=${#s} i=0 c st=N word="" have=0 nx   # separate stmt: ${#s} needs s set first
  RESOLVED_TOKENS=()
  while [ "$i" -lt "$n" ]; do
    c="${s:$i:1}"
    case "$st" in
      N)
        case "$c" in
          ' '|$'\t') [ "$have" = 1 ] && { RESOLVED_TOKENS+=("$word"); word=""; have=0; };;
          '$'|'`'|'<'|'>'|'('|')'|'&') return 1;;        # active/redirect/subshell/bg -> fail safe
          '{'|'}'|'*'|'?'|'[') return 1;;                # brace/glob expansion (bash expands before tokenizing) -> fail safe
          \\) i=$((i+1)); word+="${s:$i:1}"; have=1;;     # escape: next char literal
          \') st=S; have=1;;
          \") st=D; have=1;;
          *) word+="$c"; have=1;;
        esac;;
      S) case "$c" in \') st=N;; *) word+="$c";; esac;;   # single quote: all literal
      D)
        case "$c" in
          '$'|'`') return 1;;                             # still active inside "" -> fail safe
          \\) nx="${s:$((i+1)):1}"; case "$nx" in '"'|'`'|'$'|\\) word+="$nx"; i=$((i+1));; *) word+="$c";; esac;;
          \") st=N;;
          *) word+="$c";;
        esac;;
    esac
    i=$((i+1))
  done
  [ "$st" = N ] || return 1                               # unterminated quote -> fail safe
  [ "$have" = 1 ] && RESOLVED_TOKENS+=("$word")
  return 0
}

# Classify a full Bash command line (may be a && || ; | chain) -> 0 (allow) + ALLREASON.
classify_command() {
  local cmd="$1"
  FLOOR_HIT=0                    # reset per top-level command; set on any is_destructive/args_have_secret hit
  [ -n "$cmd" ] || return 1
  case "$cmd" in *$'\n'*|*$'\r'*) return 1;; esac          # 2nd line would run unseen
  cmd="$(strip_safe_redirects "$cmd")"                     # drop 2>/dev/null / 2>&1 before the gate
  has_unsafe_metachar "$cmd" && return 1                   # subst/redirect/subshell/bg (quote-aware)

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
    resolve_tokens "$seg" || return 1   # quote/escape-faithful argv so the floor can't be dodged
    classify_tokens "${RESOLVED_TOKENS[@]}"
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
    elif [ "$OPAQUE_OK" = 1 ] && [ "$FLOOR_HIT" != 1 ] && [ -n "$CMD" ] && opaque_backstop "$CMD"; then
      safe=1; reason="burst:opaque"
    fi
    # ADR-028 D: exact-string learned-bash tier RETIRED — it never re-matched (write-once)
    # and was a plaintext-secret capture surface. Reads=default-on (A) + autonomy/burst (B)
    # cover the safe cases; the rest correctly prompt. Tool-name learning (below) stays.
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
