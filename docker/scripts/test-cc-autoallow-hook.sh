#!/usr/bin/env bash
# Unit tests for cc-autoallow-hook.sh — the security gate that decides whether a
# tool call is auto-allowed. A wrong "allow" on an unsafe command is the worst
# failure, so the unsafe cases matter most. Run: bash test-cc-autoallow-hook.sh
#
# Strategy: point CC_AUTOALLOW_CLAUDE_DIR at a temp dir, toggle flag files, feed
# a synthetic PreToolUse payload on stdin, assert whether stdout carries an allow
# decision. BRIDGE_INTERNAL_URL is pointed at an unroutable host so the background
# audit POST can never interfere (and it's backgrounded anyway).

set -u
HOOK="$(dirname "$0")/cc-autoallow-hook.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
export CC_AUTOALLOW_CLAUDE_DIR="$TMP"
export BRIDGE_INTERNAL_URL="http://127.0.0.1:0"
# ADR-028 A: the read tier is ON BY DEFAULT under CC_AUTOALLOW_RO_ROOTS. Pin it to a
# path the flag-based cases below never live under, so those stay deterministic; the
# dedicated "workspace default-on" section overrides CWD to sit under this root.
export CC_AUTOALLOW_RO_ROOTS="$TMP/ws"
mkdir -p "$TMP/cortex-autoallow"
CWD="/home/x/workspace/proj"          # basename -> projectId "proj"; NOT under $TMP/ws
PASS=0; FAIL=0

# set_flags [on] [autonomy] [push] [install] -> reset then enable the named tiers.
set_flags() {
  rm -f "$TMP/cortex-autoallow/proj.on" "$TMP/cortex-autoallow/proj.autonomy" \
        "$TMP/cortex-autoallow/proj.push" "$TMP/cortex-autoallow/proj.install"
  local f; for f in "$@"; do : > "$TMP/cortex-autoallow/proj.$f"; done
}

# payload TOOL JSON_TOOL_INPUT  -> prints a PreToolUse payload
payload() {
  jq -nc --arg cwd "$CWD" --arg tool "$1" --argjson ti "$2" \
    '{cwd:$cwd, hook_event_name:"PreToolUse", tool_name:$tool, tool_input:$ti}'
}
# run TOOL JSON_TOOL_INPUT -> echoes "allow" or "prompt"
run() {
  local out; out="$(payload "$1" "$2" | bash "$HOOK" 2>/dev/null)"
  if grep -q '"permissionDecision":"allow"' <<<"$out"; then echo allow; else echo prompt; fi
}
# expect EXPECTED DESC TOOL JSON_TOOL_INPUT
expect() {
  local want="$1" desc="$2"; shift 2
  local got; got="$(run "$@")"
  if [ "$got" = "$want" ]; then PASS=$((PASS+1)); printf '  ok   %s\n' "$desc"
  else FAIL=$((FAIL+1)); printf '  FAIL %s (want=%s got=%s)\n' "$desc" "$want" "$got"; fi
}
bash_in() { jq -nc --arg c "$1" '{command:$c, description:""}'; }

echo "== flag OFF: everything prompts =="
set_flags
expect prompt "safe cmd but flag off"  Bash "$(bash_in 'ls -la')"
expect prompt "Read but flag off"      Read '{"file_path":"/etc/hosts"}'

echo "== SAFE tier (.on) =="
set_flags on

echo "-- read-only tools allowed --"
expect allow "Read"  Read '{"file_path":"/x"}'
expect allow "Grep"  Grep '{"pattern":"foo"}'
expect allow "Glob"  Glob '{"pattern":"**/*.cs"}'
expect allow "LS"    LS   '{"path":"/x"}'

echo "-- write/exec tools still prompt --"
expect prompt "Write"  Write '{"file_path":"/x","content":"y"}'
expect prompt "Edit"   Edit  '{"file_path":"/x"}'

echo "-- read-only Bash programs allowed --"
expect allow "ls"            Bash "$(bash_in 'ls -la /tmp')"
expect allow "cat"           Bash "$(bash_in 'cat /etc/hostname')"
expect allow "git status"    Bash "$(bash_in 'git status')"
expect allow "git log"       Bash "$(bash_in 'git log --oneline -5')"
expect allow "grep"          Bash "$(bash_in 'grep -rn foo src')"
expect allow "abs path /bin" Bash "$(bash_in '/bin/ls /tmp')"

echo "-- newly added read-only programs --"
expect allow "rg"            Bash "$(bash_in 'rg foo src')"
expect allow "fd"            Bash "$(bash_in 'fd .cs')"
expect allow "jq"            Bash "$(bash_in 'jq . package.json')"
expect allow "realpath"      Bash "$(bash_in 'realpath .')"
expect allow "cut"           Bash "$(bash_in 'cut -f1 data.tsv')"
expect allow "sha256sum"     Bash "$(bash_in 'sha256sum build/app.dll')"

echo "-- newly added git read-only subcommands --"
expect allow "git cat-file"  Bash "$(bash_in 'git cat-file -p HEAD')"
expect allow "git rev-list"  Bash "$(bash_in 'git rev-list --count HEAD')"
expect allow "git for-each-ref" Bash "$(bash_in 'git for-each-ref')"
expect allow "git config --get" Bash "$(bash_in 'git config --get user.name')"
expect allow "git config --list" Bash "$(bash_in 'git config --list')"
expect prompt "git config SET" Bash "$(bash_in 'git config user.name bob')"

echo "-- read-only COMPOUND commands allowed (the main ask) --"
expect allow "&& chain RO"   Bash "$(bash_in 'git status && git diff')"
expect allow "pipe RO"       Bash "$(bash_in 'cat README.md | grep foo | head -5')"
expect allow "; chain RO"    Bash "$(bash_in 'ls -la; pwd; cat README.md')"
expect allow "|| chain RO"   Bash "$(bash_in 'git rev-parse HEAD || git status')"

echo "-- cd is read-only (CC prefixes diagnostics with 'cd <proj> && ...') --"
expect allow "cd alone"        Bash "$(bash_in 'cd /home/u/workspace/proj')"
expect allow "cd && cat"       Bash "$(bash_in 'cd /home/u/workspace/proj && cat src/x.cs')"
expect allow "cd && echo && grep" Bash "$(bash_in 'cd /w/proj && echo hi && grep -n foo y.cs')"
# dangerous cd forms must STILL prompt (guards independent of SIMPLE_SAFE)
expect prompt "cd \$(subst)"   Bash "$(bash_in 'cd $(cat /etc/x)')"
expect prompt "cd; rm"         Bash "$(bash_in 'cd /tmp && rm -rf /tmp/y')"
expect prompt "cd redirect"    Bash "$(bash_in 'cd /tmp > /tmp/out')"

echo "-- compound with ANY unsafe segment still prompts --"
expect prompt "RO && build (auton off)" Bash "$(bash_in 'cat x && npm run build')"
expect prompt "pipe to tee (write)"     Bash "$(bash_in 'ls | tee out.txt')"
expect prompt "RO && rm"                Bash "$(bash_in 'cat x && rm -rf /tmp/y')"

echo "-- build / local-git prompt while autonomy OFF --"
expect prompt "npm build (no auton)" Bash "$(bash_in 'npm run build')"
expect prompt "git commit (no auton)" Bash "$(bash_in 'git commit -m x')"

echo "-- unsafe Bash still prompts (the cases that matter) --"
expect prompt "rm"               Bash "$(bash_in 'rm -rf /tmp/x')"
expect prompt "git push (write)" Bash "$(bash_in 'git push origin main')"
expect prompt "sort -o (writes)" Bash "$(bash_in 'sort -o out.txt in.txt')"
expect prompt "lone & background" Bash "$(bash_in 'sleep 5 & ls')"
expect prompt "semicolon -> rm"  Bash "$(bash_in 'ls; rm -rf /')"
expect prompt "and -> rm"        Bash "$(bash_in 'ls && rm -rf /')"
expect prompt "redirect"         Bash "$(bash_in 'echo x > /etc/passwd')"
expect prompt "append redirect"  Bash "$(bash_in 'echo x >> ~/.bashrc')"
expect prompt "cmd subst \$()"   Bash "$(bash_in 'echo $(rm -rf /)')"
expect prompt "backtick subst"   Bash "$(bash_in 'echo `rm -rf /`')"
expect prompt "newline 2nd cmd"  Bash "$(bash_in "$(printf 'ls\nrm -rf /tmp/x')")"
expect prompt "CR 2nd cmd"       Bash "$(bash_in "$(printf 'ls\rrm -rf /')")"
expect prompt "env bypass -> rm" Bash "$(bash_in 'env FOO=1 rm -rf /')"
expect allow  "env -> ls (safe)" Bash "$(bash_in 'env FOO=1 ls')"
expect allow  "bare env"         Bash "$(bash_in 'env')"

echo "-- secret-path reads still prompt (P2) --"
expect prompt "cat ssh key"      Bash "$(bash_in 'cat /home/x/.ssh/id_rsa')"
expect prompt "cat .ssh dir"     Bash "$(bash_in 'cat ~/.ssh/id_ed25519')"
expect prompt "cat .env"         Bash "$(bash_in 'cat .env')"
expect prompt "cat creds.json"   Bash "$(bash_in 'cat app.credentials.json')"
expect prompt "head .pem"        Bash "$(bash_in 'head -5 server.pem')"
expect prompt "grep secret env"  Bash "$(bash_in 'grep TOKEN .env.local')"
expect prompt "jq on .env"       Bash "$(bash_in 'jq . .env')"
expect prompt "env->cat secret"  Bash "$(bash_in 'env X=1 cat ~/.ssh/id_rsa')"
expect allow  "cat normal file"  Bash "$(bash_in 'cat README.md')"
expect allow  "ls .ssh (no read)" Bash "$(bash_in 'ls -la /home/x/.ssh')"
# ADR-028 C: extended secret denylist (kube/docker/gcloud) + brace/glob rejected
expect prompt "cat kube config"   Bash "$(bash_in 'cat /home/x/.kube/config')"
expect prompt "cat docker auth"   Bash "$(bash_in 'cat /home/x/.docker/config.json')"
expect prompt "cat gcloud ADC"    Bash "$(bash_in 'cat /home/x/.config/gcloud/application_default_credentials.json')"
expect prompt "unquoted glob read"    Bash "$(bash_in 'cat *.md')"
expect prompt "brace secret .en{v,v}" Bash "$(bash_in 'cat .en{v,v}')"
expect allow  "quoted glob unaffected" Bash "$(bash_in 'find src -name "*.md"')"

echo "-- Fix1: quote-aware metachar (quoted parens/redirect in patterns allowed) --"
expect allow  "grep quoted paren"    Bash "$(bash_in 'grep -rn "Foo(" src/')"
expect allow  "grep single-q paren"  Bash "$(bash_in "grep 'bar(\$x)' file")"
expect allow  "cd && grep paren"     Bash "$(bash_in 'cd /w/proj && grep "Method(" src')"
expect allow  "git --format paren"   Bash "$(bash_in 'git log --format="%(align)%H"')"
expect allow  "grep quoted redirect" Bash "$(bash_in 'grep "a>b" file')"
expect allow  "escaped \$ in dquote"  Bash "$(bash_in 'echo "a\$b"')"
expect allow  "single-quoted subst literal" Bash "$(bash_in "echo 'a\$(b)'")"
# security: substitution/redirect/subshell/background must STILL prompt (quote-aware != blanket-off)
expect prompt "subst ACTIVE in dquote"  Bash "$(bash_in 'echo "$(rm -rf x)"')"
expect prompt "backtick ACTIVE in dquote" Bash "$(bash_in 'echo "`id`"')"
expect prompt "var-expand in dquote"    Bash "$(bash_in 'echo "$HOME"')"
expect prompt "unquoted subshell"       Bash "$(bash_in '(rm -rf x)')"
expect prompt "unterminated quote"      Bash "$(bash_in 'grep "foo')"
expect prompt "quoted-then-unquoted subst" Bash "$(bash_in 'grep "ok" && echo $(id)')"

echo "-- Fix2: read-only find/sort via arg inspection --"
expect allow  "find read-only"       Bash "$(bash_in 'find src -type f -name "*.cs"')"
expect prompt "find -delete"         Bash "$(bash_in 'find . -delete')"
expect prompt "find -exec"           Bash "$(bash_in 'find . -exec rm {} ;')"
expect allow  "sort read-only"       Bash "$(bash_in 'sort data.txt')"
expect prompt "sort --output writes" Bash "$(bash_in 'sort --output=out data')"
# read-only sed is now the NARROW line-print form only (was fully excluded)
expect allow  "sed -n range,p (read)"    Bash "$(bash_in 'sed -n 1,5p f')"
expect allow  "sed -n 60,110p file"      Bash "$(bash_in 'sed -n 60,110p tests/x.ts')"
expect prompt "sed -i (write)"           Bash "$(bash_in 'sed -i s/a/b/ f')"
expect prompt "sed regex /foo/p"         Bash "$(bash_in 'sed -n /foo/p f')"
expect prompt "sed w (write cmd)"        Bash "$(bash_in 'sed -n w-file f')"
expect prompt "sed on secret file"       Bash "$(bash_in 'sed -n 1,5p /home/x/.ssh/id_rsa')"

echo "-- safe output redirects (2>/dev/null, 2>&1) stripped before the gate --"
expect allow  "cat 2>/dev/null"          Bash "$(bash_in 'cat file 2>/dev/null')"
expect allow  "git status 2>&1"          Bash "$(bash_in 'git status 2>&1')"
expect allow  "grep 2>/dev/null | head"  Bash "$(bash_in 'grep -rn foo src 2>/dev/null | head')"
expect allow  "ls >/dev/null 2>&1"       Bash "$(bash_in 'ls >/dev/null 2>&1')"
expect prompt "redirect to real file"    Bash "$(bash_in 'cat f 2>/tmp/x')"
expect prompt ">/dev/nullx not exact"    Bash "$(bash_in 'cat f >/dev/nullx')"
expect prompt "secret after strip"       Bash "$(bash_in 'cat /home/x/.ssh/id_rsa 2>/dev/null')"
expect prompt "safe+dangerous redirect"  Bash "$(bash_in 'cat x 2>/dev/null > realfile')"

echo "-- interactive/meta tools NEVER auto-allowed, even from a poisoned learned file --"
echo '{"bashCommands":[],"tools":["AskUserQuestion","ExitPlanMode","Read"]}' > "$TMP/cortex-autoallow/proj.learned.json"
expect prompt "AskUserQuestion (poisoned learned)" AskUserQuestion '{}'
expect prompt "ExitPlanMode (poisoned learned)"    ExitPlanMode    '{}'
expect allow  "Read still allowed (RO tier)"       Read            '{"file_path":"/x"}'
rm -f "$TMP/cortex-autoallow/proj.learned.json"

echo "== AUTONOMY tier (.on + .autonomy) =="
set_flags on autonomy

echo "-- build / test / lint allowed --"
expect allow "npm run build"  Bash "$(bash_in 'npm run build')"
expect allow "npm run test:run (prefix)" Bash "$(bash_in 'npm run test:run')"
expect allow "npm run build:prod (prefix)" Bash "$(bash_in 'npm run build:prod')"
expect prompt "npm run dev (server)" Bash "$(bash_in 'npm run dev')"
expect allow "pnpm test"      Bash "$(bash_in 'pnpm test')"
expect allow "dotnet test"    Bash "$(bash_in 'dotnet test')"
expect allow "dotnet build"   Bash "$(bash_in 'dotnet build -c Release')"
expect allow "vitest"         Bash "$(bash_in 'vitest run')"
expect allow "eslint"         Bash "$(bash_in 'eslint .')"
expect allow "make"           Bash "$(bash_in 'make build')"
expect prompt "npm run deploy (not build)" Bash "$(bash_in 'npm run deploy')"
expect prompt "dotnet run (serves)"        Bash "$(bash_in 'dotnet run')"
expect prompt "curl (network)"             Bash "$(bash_in 'curl http://x')"

echo "-- local git allowed --"
expect allow "git add"        Bash "$(bash_in 'git add -A')"
expect allow "git commit"     Bash "$(bash_in 'git commit -m fixbug')"
expect allow "git stash"      Bash "$(bash_in 'git stash')"
expect allow "build && test"  Bash "$(bash_in 'npm run build && dotnet test')"
expect allow "add && commit"  Bash "$(bash_in 'git add -A && git commit -m wip')"

echo "-- read-only still works under autonomy --"
expect allow "ls under auton" Bash "$(bash_in 'ls -la')"
expect allow "git status auton" Bash "$(bash_in 'git status')"

echo "-- push / install gated; destructive ALWAYS prompt --"
expect prompt "push (no .push)"     Bash "$(bash_in 'git push origin main')"
expect prompt "npm install (no flag)" Bash "$(bash_in 'npm install')"
expect prompt "rm under autonomy"   Bash "$(bash_in 'rm -rf node_modules')"
expect prompt "git reset --hard"    Bash "$(bash_in 'git reset --hard HEAD~1')"
expect prompt "git clean"           Bash "$(bash_in 'git clean -fd')"
# ADR-028 C: quote/escape can no longer dodge the destructive floor
expect prompt "git reset \"--hard\" (quoted)" Bash "$(bash_in 'git reset "--hard" HEAD~1')"
expect prompt "rm \"-rf\" (quoted flag)"      Bash "$(bash_in 'rm "-rf" node_modules')"

echo "== AUTONOMY + .push =="
set_flags on autonomy push
expect allow  "git push allowed"    Bash "$(bash_in 'git push origin main')"
expect prompt "git push --force"    Bash "$(bash_in 'git push --force origin main')"
expect prompt "git push -f"         Bash "$(bash_in 'git push -f')"
# ADR-028 C: quoted flag / brace / glob can't smuggle --force past the floor
expect prompt "git push \"--force\" quoted"   Bash "$(bash_in 'git push "--force" origin main')"
expect prompt "git push --forc{e,e} brace"    Bash "$(bash_in 'git push --forc{e,e} origin main')"
expect prompt "git push --for* glob"          Bash "$(bash_in 'git push --for* origin main')"

echo "== AUTONOMY + .install =="
set_flags on autonomy install
expect allow "npm install"    Bash "$(bash_in 'npm install')"
expect allow "pnpm install"   Bash "$(bash_in 'pnpm install')"
expect allow "dotnet restore" Bash "$(bash_in 'dotnet restore')"
expect prompt "curl still prompts (install flag != arbitrary)" Bash "$(bash_in 'curl http://x')"

echo "== autonomy implies read-only (only .autonomy, no .on) =="
set_flags autonomy
expect allow "ls (autonomy implies RO)" Bash "$(bash_in 'ls -la')"
expect allow "cat (autonomy implies RO)" Bash "$(bash_in 'cat README.md')"

echo "== other project's flag does not enable this project =="
set_flags
: > "$TMP/cortex-autoallow/other.on"
expect prompt "wrong-project flag" Bash "$(bash_in 'ls')"

echo "== ADR-028 A: read-only ON by default under workspace root, opt-out via .ro-off =="
set_flags                                 # NO flag files at all
CWD="$TMP/ws/proj"                        # basename proj; under CC_AUTOALLOW_RO_ROOTS=$TMP/ws
expect allow  "workspace read (no flag)"     Bash "$(bash_in 'grep -n foo src/x.cs')"
expect allow  "workspace cat (no flag)"      Bash "$(bash_in 'cat README.md')"
expect allow  "workspace Read tool"          Read '{"file_path":"/x"}'
expect prompt "workspace build still gated"  Bash "$(bash_in 'npm run build')"
expect prompt "workspace floor still on"     Bash "$(bash_in 'rm -rf /tmp/x')"
expect prompt "workspace secret still on"    Bash "$(bash_in 'cat .env')"
expect prompt "workspace Write still gated"  Write '{"file_path":"/x","content":"y"}'
: > "$TMP/cortex-autoallow/proj.ro-off"    # opt this project out
expect prompt "workspace .ro-off opts out"   Bash "$(bash_in 'grep -n foo src/x.cs')"
expect prompt ".ro-off blocks Read too"      Read '{"file_path":"/x"}'
rm -f "$TMP/cortex-autoallow/proj.ro-off"
CWD="/home/x/elsewhere/proj"              # outside the workspace root
expect prompt "outside workspace gated"      Bash "$(bash_in 'ls')"
CWD="$TMP/ws/../ws/proj"                  # non-canonical path never default-on
expect prompt "dotdot path not default-on"   Bash "$(bash_in 'ls')"

echo "== ADR-028 B: time-boxed autonomy burst + opaque-ok backstop =="
set_flags; rm -f "$TMP/cortex-autoallow/proj.ro-off"
CWD="/home/x/elsewhere/proj"              # not under workspace; only .burst decides
NOW="$(date +%s)"
printf '%s\n' "$((NOW+3600))" > "$TMP/cortex-autoallow/proj.burst"        # ACTIVE (autonomy+install)
expect allow  "burst: build"                 Bash "$(bash_in 'npm run build')"
expect allow  "burst: git commit"            Bash "$(bash_in 'git commit -m x')"
expect allow  "burst: npm install"           Bash "$(bash_in 'npm install')"
expect prompt "burst: rm floor"              Bash "$(bash_in 'rm -rf /tmp/x')"
expect prompt "burst: force-push floor"      Bash "$(bash_in 'git push --force origin m')"
expect prompt "burst: plain push (needs .push)" Bash "$(bash_in 'git push origin main')"
expect prompt "burst: secret"                Bash "$(bash_in 'cat .env')"
expect prompt "burst: ssh opaque (no opaque-ok)" Bash "$(bash_in "ssh h 'systemctl restart x'")"
printf '%s\n' "$((NOW-3600))" > "$TMP/cortex-autoallow/proj.burst"        # EXPIRED
expect prompt "burst expired -> gated"       Bash "$(bash_in 'npm run build')"
echo abc > "$TMP/cortex-autoallow/proj.burst"                            # malformed
expect prompt "burst malformed -> gated"     Bash "$(bash_in 'npm run build')"
printf '%s opaque\n' "$((NOW+3600))" > "$TMP/cortex-autoallow/proj.burst" # opaque-ok
expect allow  "opaque: ssh benign"           Bash "$(bash_in "ssh h 'systemctl restart nginx'")"
expect allow  "opaque: python not-allowlisted" Bash "$(bash_in 'python3 deploy.py --env prod')"
expect prompt "opaque: remote rm (backstop)" Bash "$(bash_in "ssh h 'rm -rf /'")"
expect prompt "opaque: subst rm (backstop)"  Bash "$(bash_in 'echo $(rm -rf /)')"
expect prompt "opaque: concat rm (backstop)" Bash "$(bash_in "ssh h 'r''m -rf /'")"
expect prompt "opaque: secret read (backstop)" Bash "$(bash_in 'cat /home/x/.ssh/id_rsa')"
expect prompt "opaque: brace (backstop)"     Bash "$(bash_in 'git push --forc{e,e} o m')"
# backstop is quote-aware: quoted regex/globs in an opaque body are NOT false-blocked
expect allow  "opaque: ssh regex body [A-Za-z]*" Bash "$(bash_in "ssh h 'grep -oE \"[A-Za-z]+\" ~/x/compose.yml | head'")"
expect allow  "opaque: tail -f not over-blocked" Bash "$(bash_in "ssh h 'tail -f /var/log/x'")"
expect allow  "opaque: quoted brace awk"     Bash "$(bash_in "ssh h 'awk \"{print}\" f'")"
expect prompt "opaque: unquoted brace r{m,m}" Bash "$(bash_in 'r{m,m} -rf /tmp/x')"
rm -f "$TMP/cortex-autoallow/proj.burst"

echo
echo "PASS=$PASS FAIL=$FAIL"
[ "$FAIL" -eq 0 ]
