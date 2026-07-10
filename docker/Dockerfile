# syntax=docker/dockerfile:1.7
# Slim cc-bridge image: ASP.NET 10 runtime + tmux CLIENT only.
# Per ADR-013, the tmux SERVER and `claude` CLI live on the VPS host — this
# container is a thin .NET service that talks to host tmux via a bind-mounted
# socket (/tmp/tmux-1000/). No Node, no claude CLI baked in.

# ---------- Stage 1: build .NET backend ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY src/bridge-api/*.csproj ./bridge-api/
RUN dotnet restore ./bridge-api/
COPY src/bridge-api/ ./bridge-api/
RUN dotnet publish ./bridge-api/ -c Release -o /out/api \
    --no-restore \
    /p:UseAppHost=false

# ---------- Stage 2: build SvelteKit static ----------
FROM node:22-alpine AS pwa-build
WORKDIR /src
RUN corepack enable
COPY src/pwa/package.json src/pwa/pnpm-lock.yaml* ./
RUN pnpm install --frozen-lockfile || pnpm install
COPY src/pwa/ ./
RUN pnpm build

# ---------- Stage 3: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# OS deps:
#  - tmux: CLIENT only — sends commands to host server via /tmp/tmux-1000 bind
#  - git: used by /api/sessions/:id/sync-pull (git pull --ff-only)
#  - jq: hook scripts parse JSON payloads
#  - sqlite3: bootstrap reads session_ownership to skip auto-resume of
#             user-stopped projects (owner=none/pc) — see cortex-bootstrap.sh
#  - curl, ca-certificates: healthcheck + diagnostics
#  - bash: hook scripts
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        curl ca-certificates tmux git bash jq sqlite3 \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

# Non-root user; uid 1000 must match VPS host youruser for bind-mounted tmux
# socket + ~/.claude permissions (ADR-011 + ADR-013).
RUN if id ubuntu >/dev/null 2>&1; then userdel -r ubuntu 2>/dev/null || true; fi \
 && useradd -m -u 1000 -s /bin/bash cortex \
 && mkdir -p /workspace /data /home/cortex/.claude \
 && chown -R cortex:cortex /workspace /data /home/cortex

# Hooks execute on the HOST (claude runs there per ADR-013), so ~/.claude/settings.json
# points at ~/.local/bin/*.sh — nothing in this image ever invokes them. They are shipped
# to /app/hooks purely so a deploy can `docker cp` them out onto the host.
COPY docker/scripts/cc-notify-hook.sh    /app/hooks/cc-notify-hook.sh
COPY docker/scripts/cc-stop-hook.sh      /app/hooks/cc-stop-hook.sh
COPY docker/scripts/cc-activity-hook.sh  /app/hooks/cc-activity-hook.sh
COPY docker/scripts/cc-autoallow-hook.sh /app/hooks/cc-autoallow-hook.sh
RUN chmod 0755 /app/hooks/*.sh

# Bridge templates
COPY docker/templates /app/templates

USER cortex
WORKDIR /app

COPY --from=dotnet-build --chown=cortex:cortex /out/api/        /app/
COPY --from=pwa-build    --chown=cortex:cortex /src/build/      /app/wwwroot/

ENV ASPNETCORE_URLS=http://0.0.0.0:3000 \
    BRIDGE_DATA_DIR=/data \
    BRIDGE_WORKSPACE_ROOT=/workspace \
    BRIDGE_CC_PROJECTS_ROOT=/home/cortex/.claude/projects \
    BRIDGE_HOOK_TOKEN_FILE=/data/bridge-hook-token

VOLUME ["/workspace", "/home/cortex/.claude", "/data"]

EXPOSE 3000

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://localhost:3000/api/health || exit 1

# Bootstrap script: connects to host tmux via socket, optionally auto-resumes
# per-project sessions, then starts bridge process.
COPY docker/scripts/cortex-bootstrap.sh /usr/local/bin/cortex-bootstrap.sh
USER root
RUN chmod 0755 /usr/local/bin/cortex-bootstrap.sh
USER cortex
ENTRYPOINT ["/bin/bash", "/usr/local/bin/cortex-bootstrap.sh"]
