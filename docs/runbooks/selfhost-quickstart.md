# Runbook: Self-host CortexBridge (tối giản)

Dựng CortexBridge trên **một máy chủ Ubuntu bất kỳ**, truy cập trong LAN hoặc qua VPN. Không cần Proxmox, không cần Netbird. Nếu bạn muốn tái tạo đúng hạ tầng của tác giả, đọc [deployment.md](deployment.md) thay vì file này.

Thời gian: ~30 phút, phần lớn là chờ `docker build`.

> **Web Push trên iOS cần chứng chỉ từ CA công cộng.** Cert tự ký hoặc CA nội bộ sẽ *không* đăng ký được push (ADR-012). Toàn bộ phần còn lại — transcript, trả lời, duyệt thao tác — chạy tốt trên HTTP trong LAN. Nếu bạn muốn push, làm thêm [phần 9](#9-tuỳ-chọn--web-push-trên-iphone).

---

## 0. Điều kiện tiên quyết

| Yêu cầu | Vì sao | Cách kiểm tra |
|---|---|---|
| Ubuntu 22.04/24.04 (hoặc tương đương), luôn bật | Phiên CC phải sống khi bạn rời máy | — |
| User đăng nhập có **uid 1000** | Socket tmux chỉ owner đọc được; user `cortex` trong container là uid 1000 | `id -u` → `1000` |
| Docker + Compose v2 | | `docker compose version` |
| Node.js 22 | `claude` CLI | `node -v` |
| Tài khoản Claude còn hiệu lực | | |

Nếu `id -u` **không** ra 1000, dừng lại. Sửa uid hoặc tạo user mới có uid 1000 — mọi bind-mount trong repo giả định điều này.

## 1. Cài Docker, Node, claude CLI

```bash
sudo apt update && sudo apt install -y curl git jq tmux ca-certificates

# Docker (rootful — ổn cho máy một người dùng)
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker "$USER"
newgrp docker    # hoặc đăng xuất/đăng nhập lại

# Node.js 22
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo bash -
sudo apt install -y nodejs
```

Cài `claude` vào **prefix của user**, tuyệt đối không `sudo npm install -g`:

```bash
npm config set prefix "$HOME/.local"
echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.bashrc
export PATH="$HOME/.local/bin:$PATH"

npm install -g @anthropic-ai/claude-code
which claude    # phải ra ~/.local/bin/claude, KHÔNG phải /usr/bin/claude
```

> **Tại sao user prefix:** nếu `claude` nằm ở thư mục thuộc root, cơ chế tự cập nhật của nó thất bại trong im lặng. CLI dần cũ đi và cuối cùng ném lỗi API khó hiểu (`400 role "system" not supported`). Lỗi này rất khó truy vết ngược về nguyên nhân cài đặt.

## 2. Đăng nhập CC + tắt onboarding wizard

```bash
claude          # làm theo luồng OAuth, rồi thoát bằng /exit
```

Sau đó **bắt buộc** làm bước này:

```bash
# Sao lưu trước
cp ~/.claude.json ~/.claude.json.bak

python3 - <<'PY'
import json, pathlib
p = pathlib.Path.home() / ".claude.json"
d = json.loads(p.read_text())
d["hasCompletedOnboarding"] = True
d.setdefault("theme", "dark")
p.write_text(json.dumps(d, indent=2))
print("hasCompletedOnboarding =", d["hasCompletedOnboarding"])
PY
```

> **Tại sao:** bridge khởi động phiên bằng `claude --resume <uuid>` trần. Nếu `hasCompletedOnboarding` là `false`, lệnh đó chạy **toàn bộ wizard lần-đầu** (chọn cách đăng nhập → chọn theme → …) *dù OAuth của bạn hoàn toàn hợp lệ*. Extension VS Code bỏ qua wizard này nên bạn sẽ không bao giờ thấy nó khi làm việc trên PC — chỉ các phiên chạy trong tmux mới dính. Kết quả: PWA hiện "cần đăng nhập" trên một phiên đã đăng nhập rồi. Rất tốn thời gian nếu không biết trước.

## 3. tmux server trên host

Bridge nói chuyện với một tmux server chạy trên **host** (ADR-013), qua socket được bind-mount. Container chỉ chứa tmux *client*.

```bash
# Symlink để `tmux -c /workspace/<proj>` phân giải giống nhau ở host và trong container
sudo ln -s "$HOME/workspace" /workspace

mkdir -p ~/workspace ~/.claude ~/.config/systemd/user

cat > ~/.config/systemd/user/cortex-tmux.service <<'EOF'
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

# Giữ service của user sống qua logout + reboot
sudo loginctl enable-linger "$USER"

# Kiểm tra
tmux ls                      # cc: 1 windows
ls -l /tmp/tmux-$(id -u)/default   # socket phải tồn tại
```

## 4. Lấy mã nguồn + cấu hình

```bash
cd ~/workspace
git clone git@github.com:DT-Tuan/CortexBridge.git CortexBridge
cd CortexBridge

# State riêng của bridge — để ngoài workspace
sudo mkdir -p /opt/cortex/data/sqlite
sudo chown -R "$USER:$USER" /opt/cortex/data

# Sinh cặp khoá VAPID (một lần)
npx --yes web-push generate-vapid-keys

cp .env.selfhost.example .env
```

Sửa `.env`. Tối thiểu:

```bash
CORTEX_WORKSPACE=/home/<user>/workspace
CORTEX_CLAUDE_DIR=/home/<user>/.claude
CORTEX_DATA_DIR=/opt/cortex/data/sqlite
CORTEX_UID=1000

# IP mà bridge lắng nghe. Đặt IP LAN để mở PWA từ điện thoại.
BRIDGE_BIND_IP=192.168.1.50
BRIDGE_PUBLIC_URL=http://192.168.1.50:3000

BRIDGE_ADMIN_SECRET=<openssl rand -hex 32>
VAPID_PUBLIC_KEY=<từ output của npx>
VAPID_PRIVATE_KEY=<từ output của npx>
VAPID_SUBJECT=mailto:<email của bạn>
```

> **Đừng đặt `BRIDGE_BIND_IP` là `0.0.0.0` trên máy có IP public.** Bridge chạy được lệnh tuỳ ý qua tmux — về bản chất nó *là* shell của bạn. LAN, VPN, hoặc Cloudflare Tunnel. Không có lựa chọn thứ tư.

## 5. Build + chạy

```bash
docker compose -f docker-compose.selfhost.yml up -d --build

docker compose -f docker-compose.selfhost.yml ps        # cc-bridge phải healthy
docker compose -f docker-compose.selfhost.yml logs -f cc-bridge
```

Smoke test:

```bash
curl -s http://127.0.0.1:3000/api/health
# {"status":"ok", ...}
```

Trong log bootstrap bạn sẽ thấy `host tmux reachable`. Nếu thấy `WARN: host tmux not reachable`, quay lại bước 3 — socket chưa đúng hoặc uid lệch.

## 6. Cài CC hooks (trên host)

Hook báo cho bridge biết khi CC cần input, khi nó dừng, và khi nào tự động duyệt các thao tác chỉ-đọc. Chúng chạy **trên host**, vì `claude` chạy trên host.

```bash
cd ~/workspace/CortexBridge

install -m 0755 -D -t ~/.local/bin \
  docker/scripts/cc-notify-hook.sh \
  docker/scripts/cc-stop-hook.sh \
  docker/scripts/cc-activity-hook.sh \
  docker/scripts/cc-autoallow-hook.sh

# Sinh khối hooks với đường dẫn thật
sed "s|__HOME__|$HOME|g" docker/templates/claude-settings.json > /tmp/cc-hooks.json
```

Gộp khoá `hooks` từ `/tmp/cc-hooks.json` vào `~/.claude/settings.json`. Nếu file chưa tồn tại thì chép thẳng; nếu đã có, gộp bằng `jq`:

```bash
if [ -f ~/.claude/settings.json ]; then
  cp ~/.claude/settings.json ~/.claude/settings.json.bak
  jq -s '.[0] * .[1]' ~/.claude/settings.json /tmp/cc-hooks.json > /tmp/merged.json \
    && mv /tmp/merged.json ~/.claude/settings.json
else
  cp /tmp/cc-hooks.json ~/.claude/settings.json
fi
```

Kiểm tra token và đường đi:

```bash
TOKEN=$(< /opt/cortex/data/sqlite/bridge-hook-token)
curl -fsS -X POST http://127.0.0.1:3000/internal/hooks/notification \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"projectId":"test","session_id":"x","transcript_path":"/tmp/x.jsonl","cwd":"/workspace/test","hook_event_name":"Notification","message":"smoke"}'
```

Kỳ vọng HTTP 202.

> Hook được đọc **lúc process khởi động**. Mọi phiên CC đang chạy phải khởi động lại thì cấu hình mới có hiệu lực. Đây là nguồn gốc của rất nhiều lần "sao hook không chạy".

## 7. Cấp token cho điện thoại

```bash
curl -s -X POST http://127.0.0.1:3000/api/auth/issue \
  -H "X-Admin-Secret: $(grep '^BRIDGE_ADMIN_SECRET=' .env | cut -d= -f2-)" \
  -H "Content-Type: application/json" \
  -d '{"deviceName":"iPhone"}'
# → {"token":"cb_..."}
```

Trên iPhone (cùng LAN): mở `http://192.168.1.50:3000` bằng Safari → dán token → **Đăng nhập**. Bấm Share → **Add to Home Screen** để cài PWA.

## 8. Onboard project đầu tiên

Có một cái bẫy: một thư mục trống **không thể** khởi động từ PWA. `claude` chạy lần đầu trong thư mục lạ sẽ treo ở prompt "do you trust this folder?", và vì chưa có file JSONL nào nên project không xuất hiện trên dashboard.

Cách chắc chắn nhất là chấp nhận trust trước:

```bash
cd ~/workspace && git clone <repo-cua-ban> myproject

python3 - <<'PY'
import json, pathlib, os
p = pathlib.Path.home() / ".claude.json"
d = json.loads(p.read_text())
path = str(pathlib.Path.home() / "workspace" / "myproject")
d.setdefault("projects", {}).setdefault(path, {})["hasTrustDialogAccepted"] = True
p.write_text(json.dumps(d, indent=2))
print("trusted:", path)
PY

# Mở phiên CC trong tmux session 'cc' của host
tmux new-window -t cc -n myproject -c ~/workspace/myproject "bash -lc 'exec claude'"
```

Tải lại PWA — `myproject` sẽ hiện trên dashboard. Từ đây, bridge tự resume phiên này mỗi lần container khởi động lại.

## 9. Tuỳ chọn — Web Push trên iPhone

iOS chỉ đăng ký Web Push trên origin `https` có cert từ CA công cộng. Cách rẻ nhất là Cloudflare Tunnel (miễn phí, không cần mở port).

1. Cloudflare Zero Trust → Networks → Tunnels → tạo tunnel, copy token.
2. Thêm **Public Hostname**: subdomain tuỳ chọn, Service = `HTTP` → `cc-bridge:3000`.
3. Trong `.env`:
   ```bash
   CLOUDFLARE_TUNNEL_TOKEN=<token>
   BRIDGE_PUBLIC_URL=https://cortex.example.com   # phải khớp Public Hostname
   ```
4. Khởi động lại kèm profile `tunnel`:
   ```bash
   docker compose -f docker-compose.selfhost.yml --profile tunnel up -d
   ```
5. Trên iPhone, mở PWA từ **domain mới** (cài lại Add to Home Screen), vào Settings → "Thông báo đẩy" → Bật → Allow.

`BRIDGE_PUBLIC_URL` phải khớp chính xác origin mà trình duyệt nhìn thấy — hook dùng nó để dựng URL click-through cho notification.

## Xử lý sự cố

| Triệu chứng | Nguyên nhân |
|---|---|
| PWA báo "cần đăng nhập" trên phiên đã login | `hasCompletedOnboarding=false` → bước 2. Sửa trên host, **kill các tmux window đang kẹt**, rồi restart bridge. |
| Log bootstrap: `host tmux not reachable` | Socket sai hoặc uid ≠ 1000. `ls -l /tmp/tmux-$(id -u)/default` và `id -u`. |
| `permission denied … /var/run/docker.sock` | Process của bạn còn giữ group cũ. Chạy `sg docker -c "docker …"` hoặc đăng nhập lại. |
| Hook không chạy | Phiên CC đang chạy đọc hook lúc khởi động. Restart phiên. |
| Project không hiện trên dashboard | Chưa có JSONL, hoặc thư mục không nằm trực tiếp dưới `CORTEX_WORKSPACE`. → bước 8. |
| `claude` báo lỗi `400 role "system" not supported` | CLI cũ vì cài bằng `sudo npm -g`. Cài lại vào `~/.local` → bước 1. |
| iPhone không nhận push | Origin không có cert công cộng, hoặc `BRIDGE_PUBLIC_URL` không khớp origin. → bước 9. |
| Phiên đã dừng lại tự sống dậy sau rebuild | tmux server sống trên host nên window vẫn còn. Dừng phiên qua PWA (ghi `owner=none`), đừng chỉ `docker compose down`. |

## Những gì runbook này **không** làm

- Không dựng Caddy. Bạn nói chuyện HTTP trực tiếp với bridge trong LAN, hoặc HTTPS qua Cloudflare Tunnel.
- Không cấu hình backup. Xem [backup-onedrive.md](backup-onedrive.md) nếu cần.
- Không cài `cortexplexus` MCP hay các skill riêng của tác giả — chúng là tuỳ chọn.
- Không có đường "chạy thử trên Docker Desktop". Bản cũ dựa trên ntfy và đã bị gỡ cùng ADR-012; dùng chính runbook này với `BRIDGE_BIND_IP=127.0.0.1` để chạy cục bộ.
