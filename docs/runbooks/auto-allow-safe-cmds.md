# Runbook — Auto-allow SAFE permission prompts

PWA-managed auto-accept of SAFE read-only permission prompts, so you don't have to
tap "1" on your phone for harmless commands. Design: `project_autoallow_design.md`
(Architecture C — a host PreToolUse hook returns `permissionDecision:allow`; no
bridge auto-tap, no pane race). Branch `feat/auto-allow-safe-cmds`.

## What it does

A host `PreToolUse` hook (`cc-autoallow-hook.sh`) inspects each tool call and
returns `{permissionDecision:"allow"}` for allowed calls — CC then runs them with
**no permission prompt at all**. Everything else falls through and prompts as
usual. There are **two independent per-project tiers** (+ two sub-flags), all
default OFF, toggled from the PWA shield popover in the session header.

### Tier 1 — SAFE (`.on`, shield = accent/blue)
- Read-only tools: `Read`, `Grep`, `Glob`, `LS`.
- Read-only Bash programs: `ls cat pwd head tail wc echo file stat du df tree which
  whoami date env grep rg fd jq cut uniq comm nl tac tr basename dirname realpath
  readlink column printenv sha256sum sha1sum md5sum cksum`.
- Read-only `git`: `status diff log branch show remote tag describe rev-parse
  ls-files blame shortlog cat-file ls-tree rev-list for-each-ref show-ref reflog
  symbolic-ref whatchanged`, and `git config` **only** with a get/list flag.
- **Compound read-only chains**: commands joined by `&&  ||  ;  |` are allowed iff
  **every** segment is itself read-only (e.g. `git status && git diff`,
  `cat x | grep y | head`). Still nothing that writes/networks/deletes.

### Tier 2 — TRUST / "Tự chủ" (`.autonomy`, shield = amber + `!`, implies SAFE)
> ⚠ build/test **execute arbitrary project code** (package scripts, test code). This
> tier is a statement of **trust in the project**, not a safety guarantee.
- Build/test/lint/format: `npm|pnpm|yarn|bun {test, run <build|test|lint|check|
  format|typecheck|size|coverage|ci|prepush*>}`, `dotnet {build,test,format}`,
  `vitest eslint prettier tsc svelte-check jest mocha pytest`, `make`.
- Local git: `add`, `commit`, `stash`.
- **Sub-flag `.push`** — also `git push` (never `--force`). Off by default.
- **Sub-flag `.install`** — also `npm/pnpm/yarn install|ci|add`, `dotnet restore|add`,
  `pip install` (postinstall scripts + network). Off by default.

**Never auto-allowed — even under autonomy (fail-safe):**
- Destructive: `rm rmdir shred dd mkfs* fdisk wipefs`, `git reset --hard`,
  `git clean`, `git push --force`, `git branch -D`, `git checkout --force`.
- Shell redirection / substitution / subshell / backgrounding: any of ``< > ` $ ( )``
  or a lone `&`, or a newline/CR (a 2nd line would run unseen). Note `&& || ; |` are
  now allowed **as separators** but each segment must still pass on its own.
- `find`/`sort` (their `-exec`/`-delete`/`-o` write or destroy) and any program not listed.
- `env <cmd>` where `<cmd>` is not itself allowed (env is a known relaunch bypass).
- Read on secret paths — any allowed reader touching `*/.ssh/*`, `id_rsa*`,
  `*.pem`/`*.key`, `*.credentials.json`, `*/.aws/*`, `.env*`, `*/.gnupg/*`,
  `*cortex-secrets*`. `ls`-ing those dirs is still fine (no content).
- Network/serve (`curl`, `dotnet run`, `npm run dev`…) unless explicitly matched above.

> Read/Grep/Glob/LS are already default-allowed by CC, so the hook is effectively
> a no-op for them — its real surface is **Bash**. Only Bash auto-allows are
> audited (auditing every default-allowed read would flood `audit_log`).

## Mechanism

```
PWA shield popover toggle
  → POST /api/sessions/{projectId}/autoallow {enabled?,autonomy?,push?,install?}  (partial patch)
  → bridge creates/removes  ~/.claude/cortex-autoallow/<projectId>.{on|autonomy|push|install}
  ↑ same fs (claude-config bind = host ~/.claude) ↓
  → host PreToolUse hook reads the flags live each tool call
  → allowed? emit allow JSON on stdout (+ fire-and-forget audit POST → audit_log)
```

`projectId = basename(cwd)`. Each flag is existence-only (content irrelevant). The
POST is a partial patch — only the tiers you send flip. Autonomy implies the
read-only set even without `.on`; `.push`/`.install` are inert without `.autonomy`.

## Install (host, VPS `cortex-host`, 0-sudo)

The hook ships in the repo at `docker/scripts/cc-autoallow-hook.sh`. Install it as a
**second** `PreToolUse` hook alongside `cc-activity-hook.sh`:

```bash
# 1. Copy the hook to the user-writable bin (first in PATH)
install -m 755 docker/scripts/cc-autoallow-hook.sh ~/.local/bin/cc-autoallow-hook.sh

# 2. Add it to ~/.claude/settings.json PreToolUse array (alongside cc-activity-hook.sh).
#    The two hooks compose: activity emits no decision; autoallow emits allow-or-nothing.
#    Use the update-config skill or edit by hand so the entry reads:
#      "PreToolUse": [
#        { "matcher": ".*", "hooks": [
#            { "type": "command", "command": "/home/youruser/.local/bin/cc-activity-hook.sh PreToolUse" } ] },
#        { "matcher": ".*", "hooks": [
#            { "type": "command", "command": "/home/youruser/.local/bin/cc-autoallow-hook.sh" } ] }
#      ]

# 3. Hooks are frozen at CC process start — restart any live CC session to pick it up.
```

> ⚠ A settings.json hook change only takes effect on the **next** CC session start
> (hooks are read at process start). See `feedback_stale_process_after_env_change`.

## Test

Standalone bash harness (93 cases incl. compound chains, autonomy tiers, push/install
gating, and the destructive/bypass cases that matter most):

```bash
bash docker/scripts/test-cc-autoallow-hook.sh    # PASS=93 FAIL=0
```

Backend flag logic: `dotnet test --filter FullyQualifiedName~AutoAllowFlags`.

Live (scratch-project):
- **SAFE on**: `cat README.md` and `git status && git diff` run with **no** prompt;
  `rm /tmp/x` and `npm run build` **still** prompt.
- **Autonomy on**: `npm run build`, `dotnet test`, `git add -A && git commit -m x`
  run with no prompt; `git push` still prompts (until `.push`), `rm -rf` always prompts.

Each Bash auto-allow writes one `audit_log` row (`action='session.autoallow'`-adjacent
hook audit): tool name + match reason only — never the command, which may hold secrets.

## Deploy (bridge endpoint + PWA toggle)

The `/api/sessions/{projectId}/autoallow` endpoint and the PWA shield toggle ship in
the bridge container image:

```bash
sg docker -c 'docker compose build cc-bridge'
sg docker -c 'docker compose up -d cc-bridge'
```

(`sg docker -c '…'` because the docker group is frozen at shell start — see
`feedback_stale_process_after_env_change`.)

## Disable / uninstall

- **Per project:** toggle OFF in the PWA (removes the flag file) — instant, no restart.
- **Globally:** remove the autoallow entry from `~/.claude/settings.json` PreToolUse
  and restart CC. The endpoint/toggle become inert (flags exist but nothing reads them).
