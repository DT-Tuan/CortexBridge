# Runbook: Chuyển một project từ PC lên dev VPS

Đưa **một project cụ thể** — mã nguồn *và* trạng thái Claude Code của nó (lịch sử hội thoại + memory) — từ máy PC lên VPS, để tiếp tục làm việc qua Bridge hoặc VS Code Remote-SSH.

Runbook này bổ khuyết cho [migrate-claude-config-to-vps.md](migrate-claude-config-to-vps.md): file kia chuyển trạng thái **toàn cục** của `~/.claude/` (settings, skills, MCP config) và chỉ chạy **một lần**; runbook này chạy **cho từng project** và lặp lại được.

## Hai phần tách rời của một project

| Phần | Công cụ chuyển | Vì sao |
|---|---|---|
| **Mã nguồn** | `git push/pull` (hoặc `rsync` nếu không phải git) | Đây là việc của git, đừng gộp vào script khác cho rối. |
| **Trạng thái Claude Code** | 3 script trong [`scripts/migrate/`](../../scripts/migrate/) | Nằm ở `~/.claude/projects/<encoded-path>/` — không theo repo. |

Trạng thái Claude Code gồm:
- **JSONL transcript** (`*.jsonl`) — toàn bộ lịch sử hội thoại của project
- **memory dir** (`memory/`) — auto-memory `MEMORY.md` + các file liên kết

## Vấn đề cốt lõi: encoded-path

Claude Code lưu trạng thái mỗi project trong một thư mục **mã hoá từ đường dẫn tuyệt đối** của project:

```
/home/youruser/workspace/foo   →   ~/.claude/projects/-home-youruser-workspace-foo/
```

PC và VPS có đường dẫn tuyệt đối **khác nhau** (`E:\projects\foo` vs `/home/youruser/workspace/foo`), nên tên thư mục mã hoá cũng khác hoàn toàn. Chuyển file đòi hỏi **đổi tên thư mục chứa** — đó là việc chính mà `install.sh` làm.

## Yêu cầu

- Một kênh chuyển file PC → VPS: `scp` / `rsync` qua VPN (Netbird/Tailscale/WireGuard), hoặc bất kỳ đám mây nào bạn tin.
- `git` ở cả hai đầu; `python3` + `jq` trên VPS.
- **Đừng dùng cho project đang mở phiên.** `/exit` trước để có snapshot sạch (`bundle.sh` sẽ cảnh báo nếu thấy JSONL vừa sửa trong 5 phút).

---

## 1. Chuyển mã nguồn (PC → VPS)

Nếu project là git, cách sạch nhất là qua remote:

```bash
# Trên VPS
cd ~/workspace
git clone <remote-url> foo
```

Nếu chưa có remote chung, dùng `rsync` qua VPN (loại trừ artifact build — nếu không sẽ chuyển thừa hàng trăm MB):

```bash
# Trên PC (Git Bash / WSL), gửi lên VPS
rsync -avz --exclude-from=- ./foo/ youruser@vps:~/workspace/foo/ <<'EOF'
bin/
obj/
publish/
dist/
build/
out/
target/
node_modules/
__pycache__/
.next/
.svelte-kit/
.turbo/
.parcel-cache/
.angular/
*.dll
*.exe
*.pdb
*.so
*.dylib
EOF
```

> Bộ exclude này quan trọng: một thư mục `publish/` .NET đơn lẻ có thể lên tới hàng trăm MB. Với dự án .NET, chỉ chuyển **source**, build lại trên VPS.

## 2. Đóng gói trạng thái Claude Code (trên PC)

```bash
# Đường dẫn TUYỆT ĐỐI của project trên PC (dạng WSL/Git-Bash POSIX)
bash scripts/migrate/bundle.sh /home/youruser/projects/foo
```

Sinh trong thư mục hiện tại:
- `migrate-foo-<UTC>.tar.gz` — JSONL + memory
- `migrate-foo-<UTC>.manifest.json` — encoded path, danh sách UUID, byte-count để verify

`bundle.sh` in sẵn lệnh chuyển và công thức mã hoá. **JSONL là plaintext và có thể chứa key/token bạn từng paste** — mã hoá trước khi truyền nếu không đi qua kênh tin cậy:

```bash
age -p -o migrate-foo-<UTC>.tar.gz.age migrate-foo-<UTC>.tar.gz
```

## 3. Chuyển bundle sang VPS

```bash
scp migrate-foo-<UTC>.tar.gz migrate-foo-<UTC>.manifest.json youruser@vps:~/
# nếu đã mã hoá, chuyển file .age rồi giải mã trên VPS:
#   age -d -o migrate-foo-<UTC>.tar.gz migrate-foo-<UTC>.tar.gz.age
```

## 4. Cài đặt (trên VPS)

```bash
bash scripts/migrate/install.sh \
     migrate-foo-<UTC>.tar.gz \
     /home/youruser/workspace/foo      # đường dẫn MỚI trên VPS
```

Script sẽ: đọc manifest → tính encoded-path đích → **từ chối** nếu thư mục đích đã tồn tại và không rỗng (trừ khi thêm `--force`) → giải nén, đổi tên thư mục sang dạng mã hoá của VPS → **verify byte-count** khớp manifest → liệt kê MCP cần re-auth.

> `install.sh` không tự claim thành công nếu byte-count lệch — đây là thao tác dễ hỏng, nó dựa trên bằng chứng chứ không đoán.

## 5. Viết lại đường dẫn nhúng trong JSONL (BẮT BUỘC, trên VPS)

Mỗi record JSONL vẫn mang đường dẫn PC nhúng trong các field như `cwd`. CortexBridge lấy tên project từ `cwd`, nên nếu để nguyên `E:\projects\foo`, dashboard sẽ hiện đúng chuỗi Windows thô đó (Linux không tách `\`).

```bash
python3 scripts/migrate/rewrite-paths.py \
    ~/.claude/projects/-home-youruser-workspace-foo/ \
    --map 'e:\projects\foo=/home/youruser/workspace/foo' \
    --map 'E:\projects\foo=/home/youruser/workspace/foo' \
    --map 'C:\Users\you\.claude\projects\e--projects-foo=/home/youruser/.claude/projects/-home-youruser-workspace-foo' \
    --suppress-claude-vscode-entrypoint
```

**Trước khi soạn `--map`, hãy xem chuỗi `cwd` thật trong JSONL** (`grep -o '"cwd":"[^"]*"' <file>.jsonl | head`). Tên thư mục trên PC **không** đảm bảo bằng đường dẫn trong record — hai cái bẫy hay gặp:
- Sai hoa/thường (thư mục `Foo` nhưng record ghi `foo`).
- Thư mục dùng gạch nối nhưng đường dẫn thật dùng dấu cách (`Time-Attendance-System` ↔ `Time Attendance System`).

Truyền mọi biến thể prefix mà JSONL có thể dùng (ổ đĩa hoa + thường, cộng đường dẫn `~/.claude/projects/<encoded>` phía PC cho các tham chiếu memory-dir).

**`--suppress-claude-vscode-entrypoint` gần như luôn cần** nếu PC dùng extension VS Code chính chủ của Anthropic. Nếu bỏ qua, record cuối mang `entrypoint:"claude-vscode"` khiến bridge phân loại chủ sở hữu là PC; do trên VPS không có ide-lock cho project này, ModeWatcher kích hoạt auto-resume và spawn `claude --resume` cho một phiên bạn chỉ định chuyển làm snapshot — sinh ra tmux window mồ côi + banner "cần trả lời" giả.

## 6. Smoke-test

```bash
cd ~/workspace/foo          # repo PHẢI có mặt ở đây trước
claude --resume <uuid-trong-manifest>
```

Tải lại PWA — project xuất hiện trên dashboard dưới đúng đường dẫn VPS. Nếu resume lỗi, kiểm tra repo có thật ở `~/workspace/foo` không (encoded-path suy ra từ `cwd` lúc claude khởi động).

## Những gì runbook này KHÔNG làm

- **Không chuyển repo giùm bạn** — đó là `git`/`rsync` ở bước 1.
- **Không re-auth MCP** — luồng OAuth (Gmail/Drive/Slack…) gắn với thiết bị, phải làm tay. `install.sh` liệt kê server nào cần.
- **Không chuyển trạng thái `~/.claude/` toàn cục** (settings, skills, MCP config) — đó là [migrate-claude-config-to-vps.md](migrate-claude-config-to-vps.md), chạy MỘT lần; runbook này chạy cho TỪNG project.
- **Không giữ JSONL transcript trong bundle mã hoá giùm bạn** — bundle là `.tar.gz` thô; tự mã hoá trước khi truyền.
