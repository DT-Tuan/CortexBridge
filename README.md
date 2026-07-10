# CortexBridge

Điều khiển các phiên **Claude Code** đang chạy trên server của bạn, từ iPhone.

CortexBridge là một PWA + backend .NET 10 làm cầu nối tới các phiên Claude Code chạy trong `tmux` trên một máy chủ Linux. Khi bạn rời khỏi bàn làm việc, bạn vẫn có thể xem toàn bộ transcript, trả lời câu hỏi của Claude, và duyệt các thao tác cần phê duyệt — ngay trên điện thoại, kể cả khi màn hình đang tắt (qua Web Push).

> **Đây là công cụ cá nhân, thiết kế cho một người dùng duy nhất.** Không có multi-tenant, không có RBAC. Auth = một bearer token. Đừng phơi nó ra Internet công cộng.

---

## Nó giải quyết vấn đề gì

Claude Code chạy tốt trong terminal, nhưng terminal thì gắn với cái máy đang mở. Nếu Claude dừng lại giữa chừng để hỏi *"tôi có được chạy lệnh này không?"* trong lúc bạn đang đi ăn trưa, phiên làm việc đứng im cho tới khi bạn quay lại.

CortexBridge biến phiên đó thành thứ truy cập được từ điện thoại:

- **Xem transcript đầy đủ** — đọc trực tiếp từ file JSONL của Claude Code, stream qua SSE.
- **Trả lời bằng tiếng Việt** — nhập qua `<textarea>` HTML thật, không qua terminal emulator (bộ gõ IME hoạt động bình thường).
- **Duyệt thao tác** — các menu permission và `AskUserQuestion` hiện thành thẻ bấm được; bấm là gửi phím thật vào `tmux`.
- **Tự động duyệt lệnh an toàn** — một PreToolUse hook cho phép các tool chỉ-đọc chạy thẳng, để bạn khỏi phải bấm "yes" cho `ls` từ trên điện thoại.
- **Web Push** — Claude cần input → iPhone rung. Bấm vào notification là mở thẳng đúng phiên.
- **Nhiều phiên song song** — dashboard liệt kê mọi project, kèm trạng thái đang chạy / cần trả lời / đã dừng.
- **Tự chuyển chế độ** — khi bạn mở project đó trong VS Code trên PC, bridge tự nhường quyền điều khiển; đóng VS Code thì nó nhận lại.

## Kiến trúc

Điểm mấu chốt: **`claude` CLI và tmux server chạy trên host, không nằm trong container.** Container chỉ chứa bridge process và một tmux *client* nói chuyện với host qua socket được bind-mount.

```
  iPhone (PWA)                      Server Linux
 ┌──────────────┐        ┌──────────────────────────────────────────┐
 │  SvelteKit   │        │  ┌────────────────┐                      │
 │  + Service   │ HTTPS  │  │  cc-bridge     │   Process            │
 │    Worker    │◄──────►│  │  (.NET 10)     │──────────┐           │
 └──────────────┘  SSE   │  │                │          ▼           │
        ▲                │  │  tmux client   │   ┌─────────────┐    │
        │                │  └────────────────┘   │ tmux server │    │
        │ Web Push       │         │  ▲          │  (host)     │    │
        │ (VAPID)        │  EF Core│  │ watch    └─────────────┘    │
        │                │         ▼  │              │ pty          │
        │                │     ┌────────┐            ▼              │
        └────────────────┼─────│ SQLite │      ┌───────────┐        │
                         │     └────────┘      │ claude CLI│        │
                         │          ▲          └───────────┘        │
                         │          │ hooks          │              │
                         │          └────────────────┘              │
                         │      ~/.claude/projects/*.jsonl          │
                         └──────────────────────────────────────────┘
```

| Lớp | Công nghệ |
|---|---|
| Frontend | SvelteKit + Tailwind + PWA + Web Push |
| Backend | ASP.NET Core Minimal API + SSE + EF Core (SQLite) |
| IPC | `tmux send-keys` qua `System.Diagnostics.Process` |
| Nguồn dữ liệu | File JSONL của Claude Code + hooks lifecycle |
| Push | Web Push (VAPID) → service worker |

## Yêu cầu

- Một máy chủ Linux luôn bật (VM hoặc bare metal)
- User đăng nhập có **uid 1000** — để khớp user `cortex` trong container và dùng chung socket tmux
- Docker + Docker Compose v2
- Node.js 22 + `claude` CLI cài ở **user prefix** (`~/.local`, không phải `sudo npm -g`)
- Một tài khoản Claude còn hiệu lực (`claude /login`)
- iPhone iOS 16.4+ nếu muốn dùng Web Push

## Bắt đầu

| Kịch bản | Runbook |
|---|---|
| **Tự host, tối giản** — một server Ubuntu bất kỳ, truy cập trong LAN/VPN | [`docs/runbooks/selfhost-quickstart.md`](docs/runbooks/selfhost-quickstart.md) ← **bắt đầu ở đây** |
| Bản đầy đủ — Proxmox VM + VPN mesh + Cloudflare Tunnel | [`docs/runbooks/deployment.md`](docs/runbooks/deployment.md) |

Khác biệt duy nhất đáng kể: **Web Push trên iOS đòi origin phải có chứng chỉ từ CA công cộng.** Cert tự ký hay CA nội bộ sẽ không đăng ký được push. Nếu bạn không cần push, đường tối giản là đủ; nếu cần, guide tối giản có phần thêm Cloudflare Tunnel (miễn phí) ở cuối.

## Mô hình bảo mật

Đọc kỹ trước khi deploy:

- Mọi endpoint yêu cầu bearer token, trừ `/api/health`.
- **Đừng bind port ra IP công cộng.** Hãy để trong LAN, sau VPN (Netbird/Tailscale/WireGuard), hoặc sau Cloudflare Tunnel. Bridge chạy được lệnh tuỳ ý qua `tmux` — nó *là* shell của bạn.
- `~/.claude` và `/workspace` chứa credential và mã nguồn; chúng được bind-mount, không bao giờ bake vào image.
- Bridge không log nội dung message của Claude Code (có thể chứa code/secret) — chỉ log session id + loại event.
- Hooks chỉ nhận kết nối từ loopback, xác thực bằng token sinh ra lúc bridge khởi động.

## Cấu trúc

```
├── src/bridge-api/      # .NET 10 Minimal API — SSE, tmux IPC, hooks, Web Push
├── src/pwa/             # SvelteKit PWA
├── docker/              # Dockerfile, Caddyfile, hook scripts (cài trên HOST)
├── docs/runbooks/       # Thủ tục setup / deploy / backup / validate
├── tests/               # xUnit (backend) + Vitest (frontend)
└── .github/workflows/   # CI: backend, PWA (+ bundle-size gate), docker build
```

## Về các tham chiếu `ADR-NNN`

Mã nguồn và runbook trích dẫn quyết định kiến trúc theo số (`ADR-013`, `ADR-025`, …). Nhật ký quyết định đầy đủ nằm trong repo phát triển riêng tư và không được publish. Các chú thích ở đây đã tự chứa đủ lý do — hãy coi những mã đó là nhãn ổn định, không phải link.

## Phát triển

```bash
# Backend
cd src/bridge-api && dotnet run
cd tests/BridgeApi.Tests && dotnet test

# Frontend
cd src/pwa && pnpm install && pnpm dev
cd src/pwa && pnpm check && pnpm test && pnpm build

# Cổng bảo mật của hook auto-allow (bash)
bash docker/scripts/test-cc-autoallow-hook.sh
```

## Trạng thái & phạm vi

Đang chạy thật hằng ngày, kiểm chứng trên iOS 26. Đây là công cụ phục vụ nhu cầu cá nhân của tác giả và được phát triển theo hướng đó: không có bản phát hành đóng gói, không cam kết tương thích ngược, và **chưa gắn license** — nếu bạn muốn dùng lại, hãy hỏi trước.
