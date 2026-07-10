# Runbook: Deployment to Proxmox VM

Deploy CortexBridge stack vào một Proxmox VM chạy Ubuntu 24.04 LTS. Per ADR-009, the host substrate is a full VM (not LXC) so the dev-VPS double-duty (build/test/deploy) doesn't run into LXC edge cases.

## Prerequisites

- Proxmox host accessible (with virtualization extensions enabled in BIOS — VT-x / AMD-V)
- Netbird account, network configured (for SSH/dev access; mobile uses CF instead)
- Cloudflare account with a domain managed there (for the public Tunnel subdomain; per ADR-012)
- A CF Zero Trust tunnel created in dashboard with a Public Hostname → `http://cc-bridge:3000`
- Ubuntu 24.04 cloud image downloaded into a Proxmox storage (see step 1)

## 1. Provision VM

### 1a. Download the cloud image (one-time, on Proxmox host)

```bash
cd /var/lib/vz/template/iso
wget https://cloud-images.ubuntu.com/releases/24.04/release/ubuntu-24.04-server-cloudimg-amd64.img
sha256sum ubuntu-24.04-server-cloudimg-amd64.img
# Verify against the SHA256SUMS file from the same URL
```

### 1b. Create the VM

```bash
# Adjust ID, storage names (local-lvm vs local-zfs vs your pool), bridge as needed
VMID=200
STORAGE=local-lvm
BRIDGE=vmbr0

qm create $VMID \
  --name cortex-dev \
  --memory 16384 \
  --cores 4 \
  --sockets 1 \
  --cpu host \
  --machine q35 \
  --bios ovmf \
  --efidisk0 $STORAGE:0,efitype=4m \
  --net0 virtio,bridge=$BRIDGE \
  --scsihw virtio-scsi-single \
  --agent enabled=1

# Import cloud image as the VM's boot disk
qm importdisk $VMID /var/lib/vz/template/iso/ubuntu-24.04-server-cloudimg-amd64.img $STORAGE
qm set $VMID --scsi0 $STORAGE:vm-$VMID-disk-1,discard=on,ssd=1
qm set $VMID --boot order=scsi0

# Resize root disk to 200 GB (cloud image ships ~3-4 GB)
qm resize $VMID scsi0 +196G

# Cloud-init drive
qm set $VMID --ide2 $STORAGE:cloudinit
qm set $VMID --serial0 socket --vga serial0

# Cloud-init: user, SSH key, network
# User name is 'youruser' — must be uid 1000 to match the container's 'cortex' user (ADR-006 + ADR-011).
# Cloud-init default creates the first user as uid 1000, so youruser = 1000 automatically.
qm set $VMID --ciuser youruser
qm set $VMID --cipassword "$(openssl rand -base64 18)"  # capture this for first-login fallback
qm set $VMID --sshkeys ~/.ssh/authorized_keys           # your PC's pubkey
qm set $VMID --ipconfig0 ip=dhcp

# Make a template snapshot first (so you can clone for future projects)
# qm template $VMID    # only if you want this VM as a base template

qm start $VMID
```

Resource sizing (recommended starting point — scale up freely on self-built host):

| Workload | vCPU | RAM | Disk |
|---|---|---|---|
| Bridge-only (CortexBridge runtime, 1-2 CC sessions, light builds) | 2 | 8 GB | 100 GB |
| **Dev VPS double-duty** (CortexBridge + 2-3 parallel CC + Docker builds + tests) | **4-8** | **16-32 GB** | **200 GB+** |
| Heavy nested stuff (k3s in VM, browser farms, ML model loading) | 8-16 | 32-64 GB | 500 GB |

### 1c. SSH in

```bash
# Find the assigned IP from the Proxmox UI or:
qm guest cmd $VMID network-get-interfaces
ssh youruser@<vm-ip>
```

If the QEMU guest agent isn't responding yet, give it ~30s for the VM's first cloud-init pass to install `qemu-guest-agent`.

## 2. Inside VM — install Docker + Netbird

```bash
ssh youruser@<vm-ip>

# Update + tools
sudo apt update && sudo apt upgrade -y
sudo apt install -y curl ca-certificates git ufw qemu-guest-agent rclone tmux jq

# Docker (rootful — fine for single-user dev VM)
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER
newgrp docker

# Netbird
curl -fsSL https://pkgs.netbird.io/install.sh | sudo sh
sudo netbird up --setup-key <SETUP_KEY>

# Node.js 22 + claude CLI (per ADR-013, claude lives on the HOST — not in container)
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo bash -
sudo apt install -y nodejs
sudo npm install -g @anthropic-ai/claude-code
claude --version    # one-time `claude /login` later in a fresh terminal

# Verify
ip addr show wt0   # should show 100.x.x.x
docker --version
```

### 2b. Host tmux session + workspace symlink (ADR-013)

The bridge container talks to a tmux server running on this host via a bind-mounted socket. Set up the session as a systemd user service so it survives reboot, and add a `/workspace` symlink so `tmux -c /workspace/<proj>` resolves on host the same way it does inside the container.

```bash
# Symlink so tmux -c /workspace/<proj> works on host
sudo ln -s /home/youruser/workspace /workspace

# Persistent tmux 'cc' session via systemd user service
mkdir -p ~/.config/systemd/user
cat > ~/.config/systemd/user/cortex-tmux.service << 'EOF'
[Unit]
Description=CortexBridge tmux session 'cc'
After=default.target

[Service]
Type=forking
ExecStart=/usr/bin/tmux new-session -d -s cc -x 200 -y 50 'sleep infinity'
ExecStop=/usr/bin/tmux kill-session -t cc
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
EOF

systemctl --user daemon-reload
systemctl --user enable --now cortex-tmux.service

# Linger so the user service stays up across SSH logout + reboot
sudo loginctl enable-linger youruser

# Verify
tmux ls         # should list `cc: 1 windows`
ls -la /tmp/tmux-1000/default   # socket file
```

## 3. Clone repo + bootstrap

Per ADR-011: single source of truth on VPS. The bridge source code lives in `~/workspace/CortexBridge/` alongside any other project; `docker-compose.yml` runs from there. Container-only state (sqlite, caddy) lives in `/opt/cortex/data/` so it's outside the user's workspace.

```bash
# Verify uid alignment with container's cortex user (must be 1000)
id youruser   # expect: uid=1000(youruser) ...

# User-home: workspace + .claude (will be bind-mounted into the bridge container)
mkdir -p ~/workspace ~/.claude

# Clone the bridge repo into workspace
cd ~/workspace
git clone <remote> CortexBridge      # or your chosen name
cd CortexBridge

# Container-only persistent state (NOT user data — kept out of ~/workspace)
sudo mkdir -p /opt/cortex/data/{sqlite,caddy}
sudo chown -R youruser:youruser /opt/cortex/data

# Generate VAPID keypair for Web Push (one-time)
npx --yes web-push generate-vapid-keys
# Capture the public + private key strings — paste into .env below.

# Set up env
cp .env.example .env
nano .env
# Fill in:
#   BRIDGE_ADMIN_SECRET=<openssl rand -hex 32>
#   BRIDGE_HOSTNAME=cortex-host.vpn.example       (Netbird FQDN of this host)
#   BRIDGE_NETBIRD_IP=<your Netbird IP, from `ip a show wt0`>
#   BRIDGE_PUBLIC_URL=https://<your-cf-subdomain>  (must match the CF Tunnel hostname)
#   CLOUDFLARE_TUNNEL_TOKEN=<from CF Zero Trust dashboard>
#   VAPID_PUBLIC_KEY=<from npx output>
#   VAPID_PRIVATE_KEY=<from npx output>
#   VAPID_SUBJECT=mailto:<your-email>
```

> **First-time install of CC on VPS:** before `docker compose up`, run `claude /login` once as `youruser` to authenticate the host-side CC CLI (used when you SSH into the VM and run `claude` directly, or when VS Code Remote-SSH spawns its CC extension). The bridge container has its own CC binary and re-uses the same `~/.claude/` via bind mount — auth is shared.

## 4. Build + start stack

```bash
docker compose build
docker compose up -d
docker compose ps   # all 3 should be healthy
docker compose logs -f cc-bridge
```

## 4a. Two ingress paths

| Path | Hostname | TLS | For |
|---|---|---|---|
| **Netbird (private)** | e.g. `cortex-host.vpn.example` | Caddy local CA (`tls internal`) | SSH, dev clients via VS Code Remote-SSH, desktop browser on Netbird |
| **Cloudflare Tunnel (public)** | e.g. `<sub>.<your-domain>` | CF auto-issued (Google Trust Services) | Mobile PWA, Web Push registration (Apple requires public CA per ADR-012) |

**Netbird side:** Caddy serves from the container's `cortex` network and binds host ports only on the Netbird IP — see `docker/caddy/Caddyfile` + the `caddy.ports` block in `docker-compose.yml`.

**Cloudflare side:** `cloudflared` (in `docker-compose.yml`) connects outbound to CF edge using `CLOUDFLARE_TUNNEL_TOKEN`. The Public Hostname route is configured in CF Zero Trust dashboard:

1. Zero Trust → Networks → Tunnels → your tunnel → **Public Hostname** → Add a public hostname
2. Subdomain: `<your-chosen>`; Domain: `<your-cf-domain>`; Path: blank
3. Service: Type=`HTTP`, URL=`cc-bridge:3000`
4. Save. CF pushes the config to `cloudflared` automatically — no VM-side restart.

Verify both paths after `docker compose up -d`:

```bash
# Netbird path (uses Caddy local CA — cert not trusted by default outside this host)
curl -sk --resolve $BRIDGE_HOSTNAME:443:$BRIDGE_NETBIRD_IP https://$BRIDGE_HOSTNAME/api/health

# CF Tunnel path (publicly trusted cert)
curl -s $BRIDGE_PUBLIC_URL/api/health
```

Both should return `{"status":"ok",...}`. Browser-side: Netbird path will show "not trusted" warning unless you install Caddy's root cert into Trusted Roots (optional, only for desktop clients that need cleaner UX on the Netbird path).

## 5. Issue bearer token for iPhone

```bash
curl -X POST http://localhost:3000/api/auth/issue \
  -H "X-Admin-Secret: $BRIDGE_ADMIN_SECRET" \
  -d '{"deviceName":"iPhone"}'
# → { "token": "cb_xxx..." }
```

Copy token. Open `$BRIDGE_PUBLIC_URL` on iPhone Safari → paste token → Add to Home Screen → open from icon → Settings → "Thông báo đẩy" → Bật → Allow on iOS prompt.

## 7. Onboard first project

Per ADR-011, clone projects on the host (not inside the container). The bind mount makes them visible to the bridge immediately.

```bash
# As youruser on the VPS:
cd ~/workspace && git clone <project-repo>

# Start a CC window in the bridge's tmux session:
docker exec -it cc-bridge bash -lc '
  cd /workspace/<project-name>
  tmux new-window -t cc -n <project-name> "claude"
'
```

Reload PWA — project should appear in dashboard.

Future edits to the project source: do them via VS Code Remote-SSH into the VM (or `cd ~/workspace/<project-name>` directly). The bridge container sees changes through the bind mount — no `git pull` round-trip needed.

## 8. Install CC hooks

CC hooks POST to bridge's loopback `/internal/hooks/*` endpoints — see 03-jsonl-and-hooks.md §2 for the full payload contract and reference scripts.

Install them **on the host**, not in the container: per ADR-013 the `claude` process runs on the host, so it executes the host's `~/.claude/settings.json` and the host's copy of the scripts.

```bash
# As youruser on the VPS, from ~/workspace/CortexBridge:
install -m 0755 -D -t ~/.local/bin \
  docker/scripts/cc-notify-hook.sh \
  docker/scripts/cc-stop-hook.sh \
  docker/scripts/cc-activity-hook.sh \
  docker/scripts/cc-autoallow-hook.sh

# Merge the hook block into ~/.claude/settings.json (back it up first if it exists)
sed "s|__HOME__|$HOME|g" docker/templates/claude-settings.json > /tmp/cc-hooks.json
# then merge /tmp/cc-hooks.json's "hooks" key into ~/.claude/settings.json
```

The bridge writes its hook token to `/data/bridge-hook-token` inside the container, which is the bind-mounted `/opt/cortex/data/sqlite/bridge-hook-token` on the host (mode 0600). The scripts read it from there and POST to `http://127.0.0.1:3000`, which the container publishes on the host's loopback. Bridge validates the bearer header + loopback origin.

Restart any running CC session — hooks are read at process start.

Smoke test (from the host):

```bash
TOKEN=$(< /opt/cortex/data/sqlite/bridge-hook-token)
curl -fsS -X POST http://127.0.0.1:3000/internal/hooks/notification \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"projectId":"test","session_id":"x","transcript_path":"/tmp/x.jsonl","cwd":"/workspace/test","hook_event_name":"Notification","message":"smoke test"}'
```

Expect HTTP 202 and a notification on the iPhone.

## Rollback

```bash
cd ~/workspace/CortexBridge
git checkout <previous-tag>
docker compose down
docker compose build
docker compose up -d
```

## Update procedure

```bash
cd ~/workspace/CortexBridge
git pull
docker compose build cc-bridge
docker compose up -d --no-deps cc-bridge   # restart only bridge, keep caddy/cloudflared up
```

## Backup

Two-layer strategy per ADR-010: local Proxmox snapshot + offsite OneDrive (M365 Dev subscription).

**Layer 1 — Local Proxmox snapshot (cron on Proxmox host):**

```bash
0 2 * * * vzdump 200 --storage backup --mode snapshot --compress zstd --maxfiles 7
```

**Layer 2 — Offsite OneDrive (cron inside dev VM):** see [backup-onedrive.md](backup-onedrive.md) for setup, schedule, and restore procedure. Selective tarball (memory + skills + audit DB), client-side encrypted via rclone crypt, weekly upload.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Cloud-init didn't apply user/key | First boot still finishing or bad ciuser config | `qm monitor $VMID; info status`; reboot the VM, watch `/var/log/cloud-init-output.log` inside |
| `docker compose up` fails on cgroup | virtio mount missing or kernel mismatch | Verify `q35` machine + cloud image kernel; `dmesg` for cgroup errors |
| Caddy fails to bind to 100.x.x.x | Netbird not up | `systemctl status netbird`, `sudo netbird up` |
| Web Push never arrives | PWA not opted-in OR running on Netbird hostname (Apple won't subscribe through local CA) | iPhone PWA must be opened from the CF Tunnel hostname (`$BRIDGE_PUBLIC_URL`), then Settings → Bật |
| `pushManager.subscribe()` hangs on iPhone | Origin not publicly trusted (cert is Caddy local CA) | Reinstall PWA via the CF Tunnel hostname, not the Netbird one. ADR-012 explains why. |
| CF Tunnel returns 502 | `cc-bridge` service name unreachable from cloudflared | Both must be on the `cortex` Docker network. `docker compose ps` should show cloudflared `Up` and depending on cc-bridge healthy. |
| PWA shows "needs input" but no push | Hooks not installed in CC | Re-run step 8 |
| `git pull` fails inside container | Missing deploy key | Prefer running `git pull` on the host as `youruser` (uses SSH agent forwarding from your VS Code Remote-SSH). Container-side git only needed if you wire deploy keys explicitly. |
| Permission denied on `/workspace` or `/home/cortex/.claude` inside container | Host user uid ≠ 1000 | `id youruser` must show `uid=1000`. If not, `sudo usermod -u 1000 youruser && sudo chown -R youruser:youruser ~youruser` (per ADR-011) |
| tmux session disappears | Container restarted without volume | Verify `/home/youruser/.claude` (host) bind-mounts to `/home/cortex/.claude` (container) and `/home/youruser/workspace` → `/workspace` in `docker-compose.yml` |
