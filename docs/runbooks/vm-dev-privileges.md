# Runbook: privileged dev work on the VM (sudo / docker) — safely

How Claude Code sessions on `cortex-host` install packages and run containerised tooling **without** unrestricted root, and why the friction you sometimes hit (Playwright system libs, `docker.sock` permission denied) is almost always a **stale process**, not a missing privilege.

## The posture already configured on `cortex-host`

| Mechanism | State | Why it's safe |
|---|---|---|
| `(ALL:ALL) ALL` | present, **password-gated** | Claude has no password → cannot get arbitrary root |
| `NOPASSWD: /usr/bin/apt, /usr/bin/apt-get, /usr/bin/dpkg` | present (`/etc/sudoers.d/`) | Scoped to package managers only; no password to leak into JSONL/transcripts |
| `youruser ∈ docker` group (gid 988) | present | Containerised tooling needs **zero** sudo |

This is a deliberate, good posture for a single-operator dev VM. **Do not** widen it to `NOPASSWD: ALL`. The real containment boundary is (a) Claude Code's per-command permission prompt — the human still approves each `sudo`/`docker` line (and that prompt now answers correctly from the PWA, see the `/choice` fix) — and (b) the VM being a dedicated dev box whose blast radius is itself + the credentials it holds. `NOPASSWD apt-get` and `docker` group are both root-capable in principle (root postinst / `docker run -v /:/h`); accept this consciously, or move genuinely untrusted work into a disposable container/VM.

## Root cause of most "permission" friction: the stale process

> Supplementary groups **and** `~/.claude` hooks are snapshotted when the `claude`
> process starts. Granting a group / changing hooks does **not** affect an
> already-running CC session until it is restarted.

Symptom seen in practice (project-zeta, May 2026): a CC session started *before* `youruser` was added to `docker` had `Groups: 4 24 27 30 46 101 1000` (no `988`) → `permission denied while trying to connect to the docker API at unix:///var/run/docker.sock`, while a fresh shell had `988`. Same mechanism as the lifecycle-hook reload caveat ([feedback memory: hook state machine]).

**Operational rule — after ANY VM env change** (`usermod -aG …`, edits to `/etc/sudoers.d/…`, changes to `~/.claude/settings.json` hooks): **restart the affected CC sessions.** Mirror what the bridge `/api/sessions/{id}/restart` does:

```bash
tmux kill-window -t cc:<projectId>
# resume the newest JSONL for that project (mtime order = what ResolveAsync picks):
uuid=$(ls -t ~/.claude/projects/-home-youruser-workspace-<projectId>/*.jsonl | head -1 \
       | xargs -n1 basename | sed 's/\.jsonl$//')
tmux new-window -t cc -n <projectId> -c ~/workspace/<projectId> "claude --resume $uuid"
```

## Decision order for privileged needs

### Tier 0 — Docker route (0 sudo) — preferred for anything needing system libs

Browsers, Playwright, build toolchains: use an official image that bakes the libs. No `apt`, no escalation. **The image version MUST match the installed package** (browser binaries are version-locked).

```bash
cd ~/workspace/<projectId>
docker run --rm --ipc=host \
  --user "$(id -u):$(id -g)" -e HOME=/tmp \
  -v "$PWD":/work -w /work \
  mcr.microsoft.com/playwright:v$(node -p "require('./node_modules/@playwright/test/package.json').version")-jammy \
  npx playwright test --reporter=line
```

`--user`/`-e HOME=/tmp` keep artifacts owned by you (not root). First run pulls ~1.7 GB (one-time); reruns skip it. Needs only the `docker` group → if it fails with a socket permission error, the CC session is **stale → restart it** (see rule above), don't reach for sudo.

### Tier 1 — `NOPASSWD apt-get` for genuine OS packages

Works **only** in the exact form that matches the sudoers spec:

```bash
sudo apt-get update && sudo apt-get install -y <explicit package list>
```

Pitfall: `npx playwright install --with-deps` runs `sudo sh -c "apt-get …"` → that is `sudo /bin/sh`, **not** `/usr/bin/apt-get` → does **not** match `NOPASSWD` → prompts for a password → fails non-interactively. Fix = split it:

```bash
sudo apt-get install -y <libs>          # matches NOPASSWD
npx playwright install chromium         # NO --with-deps
```

(Prefer Tier 0 over Tier 1 whenever an image exists — it needs no elevation at all.)

### Tier 2 — full sudo (password)

Not available to Claude by design. If a task genuinely needs unrestricted root, the human runs it via Remote-SSH; capture the resulting setup as provisioning in this runbook so it never recurs at runtime.

## Worked example — project-zeta e2e (the "honest debt" closed)

project-zeta had 4 e2e flows written but unrunnable in-session (`libatk-1.0.so.0` missing; `--with-deps` needs sudo). Resolution: restart the project-zeta CC session (inherits `docker` gid 988 + new hooks), then Tier 0 → **4/4 passed in 12.7 s, zero sudo**. The blocker was never a missing privilege — it was a stale process plus the wrong `apt` invocation form.
