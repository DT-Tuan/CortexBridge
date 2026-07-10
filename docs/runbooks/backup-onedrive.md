# Runbook: Offsite backup to OneDrive (rclone + crypt)

Implements ADR-010. Offsite backup of memory + skills + audit DB to OneDrive (M365 Developer subscription), encrypted client-side via rclone crypt.

Run inside the dev VM as the user `youruser`, NOT as root and NOT on the Proxmox host.

## Prerequisites

- M365 Developer Program account active (verify at <https://developer.microsoft.com/microsoft-365/dev-program>)
- OneDrive for Business in that tenant has free space (typically 1+ TB)
- `rclone` installed on the dev VM (`sudo apt install rclone` from [deployment.md §2](deployment.md))
- A long, randomly-generated **crypt passphrase**. Store this in your password manager **outside the dev VM** — losing it = backups unreadable forever.
- A long, randomly-generated **crypt salt** (separate from passphrase). Same storage rule.

Generate both:

```bash
# In a scratch terminal — DON'T paste these into shell history; type them into your password manager directly
openssl rand -base64 32     # passphrase
openssl rand -base64 16     # salt
```

## 1. Configure rclone — base OneDrive remote

```bash
rclone config
```

Interactive prompts:

- `n` — new remote
- name: `onedrive-raw`
- storage: `onedrive` (look up the number; it varies by rclone version)
- `client_id`: leave blank (use rclone's default app — OK for personal use; create a tenant-specific Azure AD app for production hardening)
- `client_secret`: leave blank
- region: `global`
- Edit advanced config: `n`
- Use auto config: `y` (rclone will print a URL)
  - **If running headless on the VM**: pick `n`, copy the URL, paste in a browser on a machine with a display, finish OAuth, paste the resulting code back into the VM terminal
- Choose drive: `1` (OneDrive Personal/Business — pick your M365 Dev tenant when prompted)
- Confirm the config — `y`
- Quit — `q`

Verify:

```bash
rclone lsd onedrive-raw:
# Should list folders in your OneDrive root, no errors
```

## 2. Configure rclone — crypt remote on top

```bash
rclone config
```

- `n` — new remote
- name: `onedrive-crypt`
- storage: `crypt`
- remote: `onedrive-raw:cortex-backup`  ← this is the path on OneDrive where encrypted blobs land
- filename encryption: `standard`
- directory name encryption: `true`
- password: paste the **passphrase** from step 0
- salt: paste the **salt** from step 0
- Edit advanced: `n`
- Confirm — `y`
- Quit — `q`

Verify:

```bash
echo "test" | rclone rcat onedrive-crypt:smoke-test.txt
rclone cat onedrive-crypt:smoke-test.txt    # should print "test"
rclone delete onedrive-crypt:smoke-test.txt
```

If both work: client-side encryption is functional.

## 3. The backup script

Create `/home/youruser/bin/cortex-backup.sh`:

```bash
#!/usr/bin/env bash
# Weekly offsite backup to OneDrive via rclone crypt.
# Spec: ADR-010
set -euo pipefail

DATE=$(date +%Y-%m-%d)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

LOG=/home/youruser/cortex-backup.log
exec >>"$LOG" 2>&1
echo "=== $(date -Is) START backup-$DATE ==="

# Selective tarball — see ADR-010 §"Backup contents per layer"
TARBALL="$TMP/cortex-$DATE.tar.zst"

# Excludes (build cache, volatile files, credentials)
EXCLUDES=(
  --exclude='node_modules'
  --exclude='bin'
  --exclude='obj'
  --exclude='target'
  --exclude='__pycache__'
  --exclude='.next'
  --exclude='.svelte-kit'
  --exclude='dist'
  --exclude='build'
  --exclude='.cache'
  --exclude='.npm'
  --exclude='*.log'
  --exclude='.credentials.json'      # OAuth state — rotate-able, don't back up
  --exclude='shell-snapshots'
  --exclude='telemetry'
)

tar --use-compress-program='zstd -T0 -19' \
    -cf "$TARBALL" \
    "${EXCLUDES[@]}" \
    -C /home/youruser .claude/projects \
    -C /home/youruser .claude/skills \
    -C /home/youruser .claude/settings.json \
    -C /home/youruser .claude/CLAUDE.md \
    -C / data/cortexbridge.db \
    || true   # CLAUDE.md or workspace dirs may not exist; tar warns but we want to push what we have

# Optional: include workspace WIP. Uncomment if you want untracked-to-git files backed up too.
# tar --append --use-compress-program='zstd -T0 -19' \
#     -f "$TARBALL" \
#     "${EXCLUDES[@]}" \
#     -C / workspace

# Upload to OneDrive (encrypted by crypt remote)
rclone copy "$TARBALL" onedrive-crypt:weekly/ \
  --log-level INFO \
  --transfers 1 \
  --tpslimit 4 \
  --retries 5

# Retention: keep last 12 weekly tarballs
rclone delete onedrive-crypt:weekly/ \
  --min-age 84d \
  --log-level INFO

echo "=== $(date -Is) DONE backup-$DATE ==="
```

```bash
chmod +x /home/youruser/bin/cortex-backup.sh
```

## 4. Cron schedule

`crontab -e` for the `youruser` user:

```cron
# Weekly offsite backup, Sunday 03:15
15 3 * * 0  /home/youruser/bin/cortex-backup.sh
```

Local Proxmox snapshot is configured separately — see [deployment.md](deployment.md#backup) §Backup.

## 5. First-run smoke test

```bash
/home/youruser/bin/cortex-backup.sh
tail -50 /home/youruser/cortex-backup.log
rclone ls onedrive-crypt:weekly/
# Should show one tarball with today's date
```

## Restore procedure

When you need to recover (host died, accidental rm -rf, want to compare against a past state):

```bash
# 1. Install rclone, copy your saved rclone.conf OR run `rclone config` and reconstruct
#    'onedrive-raw' + 'onedrive-crypt' remotes with the SAME passphrase + salt as before.
mkdir -p ~/.config/rclone
# (paste rclone.conf if you have it backed up to your password manager;
#  otherwise re-run `rclone config` from §1-2)

# 2. List available backups
rclone ls onedrive-crypt:weekly/

# 3. Pull the desired tarball
rclone copy onedrive-crypt:weekly/cortex-2026-04-26.tar.zst /tmp/

# 4. Inspect contents before restoring
tar --use-compress-program='zstd -d' -tvf /tmp/cortex-2026-04-26.tar.zst | head -20

# 5. Restore selectively to a scratch location FIRST
mkdir -p /tmp/restore
tar --use-compress-program='zstd -d' -xf /tmp/cortex-2026-04-26.tar.zst -C /tmp/restore

# 6. Diff against current state, then copy individual files where needed
diff -r /tmp/restore/.claude/projects/ ~/.claude/projects/ | head
# ... cherry-pick what to restore. Usually you don't bulk-overwrite.
```

**DO NOT** untar directly over `~/.claude/` — you may overwrite newer state. Always restore to scratch first.

## Quarterly verification drill

The most common backup failure is silent — backup runs but produces unreadable output. Verify quarterly:

```bash
# 1. Pull the latest backup to a scratch dir
rclone copy "$(rclone lsf onedrive-crypt:weekly/ | tail -1 | xargs -I{} echo onedrive-crypt:weekly/{})" /tmp/dr-test/

# 2. Decrypt and decompress
LATEST=$(ls -t /tmp/dr-test/*.tar.zst | head -1)
mkdir -p /tmp/dr-test/extracted
tar --use-compress-program='zstd -d' -xf "$LATEST" -C /tmp/dr-test/extracted

# 3. Sanity check — directory tree, key files present, SQLite opens
ls -la /tmp/dr-test/extracted/.claude/
ls -la /tmp/dr-test/extracted/data/
sqlite3 /tmp/dr-test/extracted/data/cortexbridge.db "SELECT count(*) FROM bearer_tokens;"

# 4. Cleanup
rm -rf /tmp/dr-test
```

If any step fails: diagnose immediately. A missing weekly run means cron broken. A decrypt failure means crypt remote misconfigured. A SQLite open failure means the DB was captured mid-write — adjust backup script to use `sqlite3 .backup` instead of raw file copy.

Schedule a calendar reminder on the **first Sunday of every quarter** to run this drill. Skipping = silent backup death.

## Failure modes + recovery

| Symptom | Cause | Recovery |
|---|---|---|
| `rclone copy` returns 401/403 | OAuth token expired / refresh expired | Re-run `rclone config reconnect onedrive-raw:` (interactive browser flow once) |
| `quota exceeded` | OneDrive full (unlikely with 1+ TB) or M365 Dev sub lapsed | Check sub status; if lapsed, renew or migrate to alternative offsite (ADR amendment) |
| Backups run but tarballs are 0 bytes | `tar` silently failed; permission issue or path missing | Check `cortex-backup.log`; the script's `\|\| true` after tar will mask errors — temporarily remove it to debug |
| Decrypt fails on restore | Wrong passphrase OR crypt salt drift | The salt + passphrase MUST match the originals exactly. Recover from password manager. **No way to brute-force without the originals.** |
| OneDrive admin disabled the rclone OAuth app for the tenant | Tenant policy change | Register a tenant-specific Azure AD app: see <https://rclone.org/onedrive/#getting-your-own-client-id-and-key> |

## Hardening (optional, do later)

- **Tenant-specific Azure AD app** instead of rclone's shared client: avoids being affected if Microsoft revokes rclone's default registration.
- **Append-only access**: configure OneDrive to only let rclone write/list — never delete. Reduces blast radius if VM compromised.
- **Multiple destinations**: in addition to OneDrive, push the same tarball to a USB drive that you rotate physically. ADR-010 explicitly puts this out of scope for v1; revisit if value > complexity.
- **GPG-sign tarballs** before upload: extra integrity check beyond rclone crypt.

These are nice-to-haves; the basic two-layer setup above is enough for the dev-VPS scale.
