# Runbook: Development Setup

Setup môi trường dev cho CortexBridge.

## Recommended workflow — develop on the VPS via VS Code Remote-SSH

Per ADR-011, the dev VM is the single source of truth for `~/.claude/` and `/workspace/`. The PC is a thin Remote-SSH client.

**Day-to-day:**

1. On PC: VS Code → `Remote-SSH: Connect to Host` → pick the VPS.
2. Open folder `/home/youruser/workspace/CortexBridge` on the VM.
3. Backend dev (`dotnet run`, `dotnet test`), frontend dev (`pnpm dev`), Docker builds — all run on the VM, not on PC.
4. Port-forward `5173` (Vite) and `3000` (bridge API) via VS Code's automatic port forwarding to browse from PC localhost.
5. Test on iPhone over Netbird → already pointed at the VM.

Skills, agents, MCPs, hooks, memory — all loaded from the VM's `~/.claude/`, not from PC.

**Do not install `claude` CLI on Windows for these projects.** Migration from Windows-side `~/.claude/` is documented in [migrate-claude-config-to-vps.md](migrate-claude-config-to-vps.md).

---

## Alternative — local PC stack (unit testing the bridge in isolation)

Use this **only** when iterating on bridge backend/frontend code in pure unit-test mode where you don't need any real CC session. For anything end-to-end, use the VPS workflow above.

### Prerequisites

| Tool | Version | Verify |
|------|---------|--------|
| .NET SDK | 10.0+ | `dotnet --version` |
| Node.js | 22+ | `node -v` |
| pnpm (or npm) | latest | `pnpm -v` |
| Docker Desktop | latest | `docker --version` |
| Git | 2.40+ | `git --version` |
| tmux | 3.3+ | `tmux -V` (Windows: WSL or Git Bash) |
| Claude Code CLI | latest | `claude --version` |

## Steps

### 1. Clone repo
```bash
git clone <remote-url> CortexBridge
cd CortexBridge
```

### 2. Backend setup
```bash
cd src/bridge-api
dotnet restore
dotnet build
dotnet run --launch-profile dev
# Listens on http://localhost:3000
```

### 3. Frontend setup (separate terminal)
```bash
cd src/pwa
pnpm install
pnpm dev
# Listens on http://localhost:5173 (proxies /api → :3000)
```

### 4. Local CC session for testing
```bash
# In a new terminal — use a real CC session or a fake JSONL writer for quick iteration
mkdir -p ~/.claude/projects/test-project
cd ~/.claude/projects/test-project
claude
```

### 5. Open PWA
- Browser: `http://localhost:5173`
- Login: paste a token issued from `POST /api/auth/issue` (see `src/bridge-api/CLAUDE.md` for admin secret)

### 6. (Optional) Test mobile UI on real iPhone
- Connect iPhone to same Wi-Fi
- Find PC IP: `ipconfig` (Windows)
- Browse `http://<PC-IP>:5173` from iPhone Safari
- Add to Home Screen for PWA install
- Note: ntfy push won't work in dev (needs full Docker stack — use `docker compose up` instead, see `deployment.md`)

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `dotnet run` fails on port 3000 | Port in use | `netstat -ano \| findstr :3000` then kill |
| SvelteKit dev "Cannot proxy /api" | Backend not running | Start backend first |
| FileSystemWatcher not firing on Windows | Path with non-ASCII chars | Use ASCII path for `~/.claude/projects/*` test sessions |
| `claude` CLI not found | Not on PATH | `npm install -g @anthropic-ai/claude-code` |
| iPhone can't reach `http://<PC-IP>:5173` | Windows Firewall | Allow Vite port 5173 inbound |

## Daily commands

```bash
# Backend test watch
cd src/bridge-api && dotnet watch test

# Frontend test
cd src/pwa && pnpm test

# Lint
cd src/pwa && pnpm lint && pnpm check

# Build full Docker image (validates production path)
docker compose -f docker-compose.yml build cc-bridge
```
