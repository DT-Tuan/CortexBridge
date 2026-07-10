# Runbook: Migrate `~/.claude/` from Windows PC to VPS

Implements ADR-011. One-shot migration of Claude Code user state (CLAUDE.md, skills, settings, hooks, MCP, memory, project context) from a Windows dev PC to the Proxmox Ubuntu VM, which becomes the single source of truth.

After this runbook, the Windows PC has **no** local `claude` state and connects to the VPS only via VS Code Remote-SSH.

> **⏱ Time:** ~30–60 min. Most of it is verification + reconnecting MCP/auth, not transfer.
> **⚠ Destructive on PC?** No — the PC `~/.claude` is archived, not deleted. Roll back by restoring the archive.

## Prerequisites

- VPS dev VM provisioned per [deployment.md](deployment.md) and ADR-009.
- Dev user `youruser` on VPS exists with **uid 1000** (required for bridge container bind-mount per ADR-006 + ADR-011). Verify: `id youruser` → `uid=1000`.
- SSH access from PC to VPS works: `ssh youruser@<vps-host>` succeeds.
- VS Code on PC has the **Remote-SSH** extension installed.
- Claude Code CLI installed on VPS: `claude --version` works when SSH'd in.
- Git installed on VPS.
- An OneDrive/local backup of `C:\Users\Tuan\.claude\` already exists (paranoia safety net before touching anything).

## What gets migrated

> Đây là runbook **một lần** cho trạng thái toàn cục. Để chuyển JSONL + memory của **từng project**, xem [migrate-project-to-vps.md](migrate-project-to-vps.md).

| Item | Path on Windows | Path on VPS | Migrate? |
|---|---|---|---|
| User CLAUDE.md | `C:\Users\Tuan\.claude\CLAUDE.md` | `~/.claude/CLAUDE.md` | ✅ Yes |
| User settings (perms, hooks) | `~\.claude\settings.json` | `~/.claude/settings.json` | ✅ Yes — but review for Windows-only paths first |
| User skills | `~\.claude\skills\` | `~/.claude/skills/` | ✅ Yes |
| User commands | `~\.claude\commands\` | `~/.claude/commands/` | ✅ Yes |
| User agents | `~\.claude\agents\` | `~/.claude/agents/` | ✅ Yes |
| MCP server configs | `~\.claude\mcp_servers.json` (or in settings) | same | ✅ Yes — but Windows paths in `command` must be rewritten |
| Keybindings | `~\.claude\keybindings.json` | same | ✅ Yes |
| Project CLAUDE.md (per-project) | `~\.claude\projects\<sanitized>\CLAUDE.md` | `~/.claude/projects/<new-sanitized>/CLAUDE.md` | ✅ Yes — only for projects you migrate |
| Project auto-memory | `~\.claude\projects\<sanitized>\memory\` | `~/.claude/projects/<new-sanitized>/memory/` | ✅ Yes |
| Statusline config | `~\.claude\statusline*` | same | ✅ Yes (small) |
| Machine-specific overrides | `~\.claude\settings.local.json` | — | ❌ No (machine-specific by design) |
| Session JSONL transcripts | `~\.claude\projects\<sanitized>\*.jsonl` | — | ❌ No (bulky, historical only — leave on PC archive) |
| Global history | `~\.claude\history.jsonl` | — | ❌ No (transient) |
| Shell snapshots | `~\.claude\shell-snapshots\` | — | ❌ No (transient) |
| IDE state | `~\.claude\ide\` | — | ❌ No (machine-specific) |
| Todos cache | `~\.claude\todos\` | — | ❌ No (per-session) |
| Auth credentials | `~\.claude\.credentials.json` | — | ❌ No — re-auth on VPS via `claude /login` |

## 1. Inventory on PC (Windows PowerShell)

```powershell
# Open PowerShell on the PC
$src = "$env:USERPROFILE\.claude"
Get-ChildItem $src | Select-Object Name, Mode, LastWriteTime
Get-ChildItem "$src\projects" | Select-Object Name, LastWriteTime | Sort-Object LastWriteTime -Descending
```

Note which **project folders** under `projects\` you actually want to bring over. Old/abandoned project entries can stay on PC archive.

## 2. Stage the transfer bundle on PC

Create a temp folder, copy only the things to migrate, exclude noise:

```powershell
$stage = "$env:TEMP\claude-migrate"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage | Out-Null

$src = "$env:USERPROFILE\.claude"

# Top-level small configs
Copy-Item "$src\CLAUDE.md"           $stage\ -ErrorAction SilentlyContinue
Copy-Item "$src\settings.json"       $stage\ -ErrorAction SilentlyContinue
Copy-Item "$src\mcp_servers.json"    $stage\ -ErrorAction SilentlyContinue
Copy-Item "$src\keybindings.json"    $stage\ -ErrorAction SilentlyContinue
Copy-Item "$src\statusline*"         $stage\ -ErrorAction SilentlyContinue

# Directories worth copying
foreach ($dir in @("skills","commands","agents")) {
    if (Test-Path "$src\$dir") {
        Copy-Item -Recurse "$src\$dir" "$stage\$dir"
    }
}

# Project-level CLAUDE.md + memory only (NOT JSONL transcripts — they're huge and historical)
New-Item -ItemType Directory -Path "$stage\projects" | Out-Null
Get-ChildItem "$src\projects" -Directory | ForEach-Object {
    $projDest = Join-Path "$stage\projects" $_.Name
    New-Item -ItemType Directory -Path $projDest -Force | Out-Null
    Copy-Item "$($_.FullName)\CLAUDE.md" $projDest\ -ErrorAction SilentlyContinue
    if (Test-Path "$($_.FullName)\memory") {
        Copy-Item -Recurse "$($_.FullName)\memory" "$projDest\memory"
    }
}

# Verify
Get-ChildItem -Recurse $stage | Measure-Object -Property Length -Sum
```

Bundle should be small (typically < 5 MB if no media in skills/). If it's much larger, re-check that JSONL transcripts were excluded.

## 3. Review configs for Windows-isms

**Before** transferring, edit these in `$stage` and rewrite any Windows-specific paths:

### `settings.json` — hook commands

```jsonc
// BEFORE (Windows)
"hooks": {
  "Stop": [{ "command": "C:\\Users\\Tuan\\.claude\\scripts\\notify.ps1" }]
}
// AFTER (Linux)
"hooks": {
  "Stop": [{ "command": "/home/youruser/.claude/scripts/notify.sh" }]
}
```

Also strip `.local` overrides that you intentionally left out. Check for `C:\\`, `%USERPROFILE%`, `pwsh`, `powershell` references.

### `mcp_servers.json` — server commands

Most MCPs use `npx`/`uvx`/`node`/`python` which exist on Linux too. Watch for:
- Absolute Windows paths in `command` or `args`
- `cwd` pointing to `C:\...`
- Server endpoints pointing to `localhost` services that only ran on PC

Rewrite each entry, or comment them out and re-add after migration once the MCP works on Linux.

### `skills/*/SKILL.md` and shell scripts in skills

`grep` (via PowerShell):

```powershell
Get-ChildItem -Recurse "$stage\skills" -Include *.md,*.sh,*.ps1 |
  Select-String -Pattern 'C:\\|powershell\.exe|pwsh\.exe|\$env:'
```

For each hit, decide: rewrite to portable form, or drop the skill if it's PC-only.

### Project CLAUDE.md files

These often have project-specific paths. They're per-project so review individually — if a project lives at `e:\projects\Foo` on PC and `/home/youruser/workspace/Foo` on VPS, anything quoting the old path needs an update.

## 4. Decide the project-folder naming scheme on VPS

Claude Code derives a project folder name under `~/.claude/projects/` by sanitizing the absolute workspace path. The encoding rule (path separator → `-`, drive colon → `-`):

| Workspace path | Folder under `~/.claude/projects/` |
|---|---|
| `e:\projects\CortexBridge` (Windows) | `e--projects-CortexBridge` |
| `/home/tuan/workspace/CortexBridge` (Linux) | `-home-tuan-workspace-CortexBridge` |

> **Note:** Exact Linux encoding may vary by CC version. Don't pre-create the folder; let CC create it on first run, then copy your CLAUDE.md + memory in.

So in this migration the **folder names will change**. Plan: keep the old-name folders inside `$stage\projects\` untouched, then after CC creates the new-name folders on VPS, manually move CLAUDE.md + memory across. Step 7 handles this.

## 5. Transfer to VPS

From PC PowerShell:

```powershell
# Use scp (built into Windows 10+) or rsync via WSL
scp -r $env:TEMP\claude-migrate youruser@<vps-host>:/tmp/claude-migrate
```

If scp is slow over a big skills/ directory, archive first:

```powershell
Compress-Archive -Path "$env:TEMP\claude-migrate\*" -DestinationPath "$env:TEMP\claude-migrate.zip"
scp $env:TEMP\claude-migrate.zip youruser@<vps-host>:/tmp/
ssh youruser@<vps-host> "mkdir -p /tmp/claude-migrate && cd /tmp/claude-migrate && unzip /tmp/claude-migrate.zip && rm /tmp/claude-migrate.zip"
```

## 6. Place files on VPS

SSH into the VPS as the dev user (NOT root):

```bash
ssh youruser@<vps-host>
```

Then:

```bash
set -e

SRC=/tmp/claude-migrate
DEST=$HOME/.claude

# Backup whatever already exists (likely nothing, but defensive)
if [ -d "$DEST" ]; then
    mv "$DEST" "$DEST.pre-migrate-$(date +%Y%m%d-%H%M%S)"
fi
mkdir -p "$DEST"

# Top-level configs
for f in CLAUDE.md settings.json mcp_servers.json keybindings.json; do
    [ -f "$SRC/$f" ] && cp "$SRC/$f" "$DEST/"
done
cp "$SRC"/statusline* "$DEST/" 2>/dev/null || true

# Directories
for d in skills commands agents; do
    [ -d "$SRC/$d" ] && cp -r "$SRC/$d" "$DEST/"
done

# Ensure executable bits on scripts (Windows zip strips these)
find "$DEST/skills" "$DEST/commands" "$DEST/agents" \
    -name '*.sh' -exec chmod +x {} \; 2>/dev/null || true

# projects/ — leave Windows-named folders aside for now; we'll merge in step 7
mkdir -p "$DEST/projects-windows-archive"
[ -d "$SRC/projects" ] && cp -r "$SRC/projects"/* "$DEST/projects-windows-archive/" || true

ls -la "$DEST"
```

## 7. Initialize new project folders on VPS

For each project you want to bring over:

```bash
# 1. Clone (or move) the project source into ~/workspace/ on VPS
cd ~/workspace
git clone <remote-url> CortexBridge          # or: git clone <remote-url> <project-name>

# 2. Open it once with claude so CC creates the new project folder under ~/.claude/projects/
cd ~/workspace/CortexBridge
claude    # type /exit immediately after the splash — we only needed CC to register the folder

# 3. Find the freshly-created folder
NEW_PROJ=$(ls -td ~/.claude/projects/*-home-* | head -1)
echo "New project folder: $NEW_PROJ"

# 4. Copy CLAUDE.md + memory from the Windows-archive folder
OLD_PROJ=~/.claude/projects-windows-archive/e--projects-CortexBridge   # adjust per project
[ -f "$OLD_PROJ/CLAUDE.md" ] && cp "$OLD_PROJ/CLAUDE.md" "$NEW_PROJ/CLAUDE.md"
[ -d "$OLD_PROJ/memory" ] && cp -r "$OLD_PROJ/memory" "$NEW_PROJ/memory"

# 5. Repeat steps 1-4 for each project
```

Note: if the project's own `CLAUDE.md` is **committed to the repo** (most CortexBridge-style projects keep it under version control), step 4 may overwrite the repo's copy with a stale Windows-side copy. **Diff them first:**

```bash
diff "$OLD_PROJ/CLAUDE.md" "$NEW_PROJ/CLAUDE.md"
```

If the repo copy is the canonical one (preferred), skip CLAUDE.md and only copy `memory/`.

## 8. Re-authenticate Claude Code on VPS

```bash
claude /login
```

Follow the browser-OAuth flow (paste URL into a browser on PC; paste the code back).

Verify auth:

```bash
claude --version
claude /status      # should show authenticated
```

## 9. Reconnect MCP servers

For each MCP entry in `mcp_servers.json`:

```bash
# Test the MCP launches correctly
claude mcp list
claude mcp test <server-name>     # if available in your CC version
```

If a server fails (Windows path, missing binary), edit `~/.claude/mcp_servers.json` and re-test. For complex MCPs, refer to `~/.claude/docs/project-master-guide.md` if it exists, or the MCP vendor docs.

## 10. Smoke-test from VS Code Remote-SSH

On PC:

1. Open VS Code.
2. `Ctrl+Shift+P` → "Remote-SSH: Connect to Host" → pick the VPS.
3. Open folder `~/workspace/CortexBridge` on the VPS.
4. Open the Claude Code panel in VS Code — it should boot using the VPS's `~/.claude/`, not anything on PC.
5. Verify:
   - User-level CLAUDE.md loads (visible in the system prompt context — or `/help` shows your custom rules).
   - Project CLAUDE.md loads.
   - Auto-memory (MEMORY.md) entries are recalled when asked ("what do you remember about this project?").
   - A user-level skill from `~/.claude/skills/` invokes (`/<skill-name>`).
   - MCP servers connect (`/mcp` or `claude mcp list`).

## 11. Smoke-test the bridge container

After [docker-compose.yml](../../docker-compose.yml) is updated per ADR-011 to bind-mount `~/workspace` and `~/.claude`:

```bash
cd ~/workspace/CortexBridge      # or wherever the bridge repo lives
docker compose up -d cc-bridge
docker exec -it cc-bridge ls /workspace        # should list your projects
docker exec -it cc-bridge ls /home/cortex/.claude   # should list CLAUDE.md, skills/, etc.
docker exec -it cc-bridge tmux new-session -d -s cc -c /workspace/CortexBridge
docker exec -it cc-bridge tmux send-keys -t cc 'claude' Enter
docker exec -it cc-bridge tmux attach -t cc      # check CC started with the migrated context
```

If you see permission denied on `/workspace` or `/home/cortex/.claude`, the dev user's uid on the host doesn't match the container's `cortex` uid (1000). Either:
- Re-create the dev user with `usermod -u 1000` (and `chown -R 1000:1000 ~`), or
- Rebuild the image with the host user's uid (less clean — see ADR-011 consequences).

## 12. Archive Windows side

Once the VPS side is fully verified (give it a week of real use before this step):

```powershell
# On PC — archive, don't delete
Move-Item "$env:USERPROFILE\.claude" "$env:USERPROFILE\.claude-archived-$(Get-Date -Format 'yyyyMMdd')"
```

This way, if anything was missed, the original is still recoverable. After ~30 days of clean VPS-only operation, delete the archive.

**Do not install `claude` CLI on Windows again.** If you find yourself reaching for it, that's a signal that VS Code Remote-SSH is unstable or too slow — fix the connection rather than reintroducing a divergent `~/.claude/` on PC.

## Rollback

If something on VPS is broken and you need to fall back to PC for an emergency:

```powershell
Move-Item "$env:USERPROFILE\.claude-archived-<date>" "$env:USERPROFILE\.claude"
# CC on PC will resume with the pre-migration state
```

Then debug the VPS side at your own pace.

## Post-migration checklist

- [ ] `claude` on VPS starts and loads user CLAUDE.md
- [ ] Project CLAUDE.md loads when CC is run inside `~/workspace/<proj>`
- [ ] At least one skill invokes (`/skill-name`)
- [ ] At least one auto-memory entry recalls correctly
- [ ] At least one MCP server connects (`/mcp`)
- [ ] VS Code Remote-SSH from PC opens the workspace and the CC panel works against VPS state
- [ ] Bridge container can read both `/workspace` and `/home/cortex/.claude` (bind mounts working)
- [ ] CortexBridge PWA shows transcripts from sessions started on VPS (end-to-end test per [iphone-validation.md](iphone-validation.md))
- [ ] Windows `~/.claude` archived, not deleted (for 30-day grace period)

## See also

- ADR-011 — why this migration exists
- ADR-009 — VPS as dev host
- [deployment.md](deployment.md) — VPS provisioning (uid 1000, user setup)
- [backup-onedrive.md](backup-onedrive.md) — offsite backup of the new VPS-side `~/.claude/` (run this AFTER migration)
