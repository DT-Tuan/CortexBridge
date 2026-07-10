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
mkdir -p "$TMP/cortex-autoallow"
CWD="/home/x/workspace/proj"          # basename -> projectId "proj"
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
expect prompt "find (excluded)"  Bash "$(bash_in 'find . -name x')"
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

echo "== AUTONOMY tier (.on + .autonomy) =="
set_flags on autonomy

echo "-- build / test / lint allowed --"
expect allow "npm run build"  Bash "$(bash_in 'npm run build')"
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

echo "== AUTONOMY + .push =="
set_flags on autonomy push
expect allow  "git push allowed"    Bash "$(bash_in 'git push origin main')"
expect prompt "git push --force"    Bash "$(bash_in 'git push --force origin main')"
expect prompt "git push -f"         Bash "$(bash_in 'git push -f')"

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

echo
echo "PASS=$PASS FAIL=$FAIL"
[ "$FAIL" -eq 0 ]
