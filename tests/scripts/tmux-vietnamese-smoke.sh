#!/usr/bin/env bash
# Wedge 1 smoke test — confirm tmux load-buffer/paste-buffer flow handles
# Vietnamese tone marks + multi-line correctly before we commit to it in code.
#
# Run on Linux/WSL/macOS with tmux >= 3.0.
#
# Expected: capture-pane shows the exact text, including newlines, with all
# Vietnamese diacritics intact. Bracketed-paste mode means CC sees ONE atomic
# input then ONE Enter — no character-by-character keystrokes.

set -euo pipefail

SESSION="cb-smoke-$$"
WINDOW="test"
BUF="cb-smoke-buf-$$"

# Multi-line UTF-8 with full Vietnamese tone-mark coverage
read -r -d '' PAYLOAD <<'EOF' || true
Xin chào! Đây là một thử nghiệm.

Các dấu thanh: à á ả ã ạ
Có dấu ô: ố ồ ổ ỗ ộ
Có dấu ư: ứ ừ ử ữ ự

Multi-line with `code` and "quotes" and 'apostrophes' and special: ; ! C-c
EOF

cleanup() {
  tmux kill-session -t "$SESSION" 2>/dev/null || true
  tmux delete-buffer -b "$BUF" 2>/dev/null || true
}
trap cleanup EXIT

echo "[1/5] Starting detached tmux session..."
tmux new-session -d -s "$SESSION" -n "$WINDOW" 'cat > /tmp/tmux-smoke-output.txt'

echo "[2/5] Loading payload into buffer (via stdin)..."
printf '%s' "$PAYLOAD" | tmux load-buffer -b "$BUF" -

echo "[3/5] Pasting buffer into pane..."
tmux paste-buffer -d -b "$BUF" -t "$SESSION:$WINDOW"

echo "[4/5] Sending Enter (tmux key token, not user-controlled)..."
tmux send-keys -t "$SESSION:$WINDOW" Enter

# Give cat a moment to flush
sleep 0.3

echo "[5/5] Comparing pane output to original payload..."
RECEIVED=$(cat /tmp/tmux-smoke-output.txt)

if [[ "$RECEIVED" == "$PAYLOAD" ]]; then
  echo
  echo "PASS — round-trip exact match. Strategy is sound."
  echo "Vietnamese tone marks, newlines, and special chars all preserved."
  exit 0
else
  echo
  echo "FAIL — output diverged from input."
  echo "--- expected ---"
  printf '%s\n' "$PAYLOAD"
  echo "--- got ---"
  printf '%s\n' "$RECEIVED"
  echo "--- diff ---"
  diff <(printf '%s' "$PAYLOAD") <(printf '%s' "$RECEIVED") || true
  exit 1
fi
