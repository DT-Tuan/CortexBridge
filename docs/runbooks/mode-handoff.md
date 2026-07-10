# Runbook: Mode A ↔ Mode B handoff

> **SUPERSEDED by ADR-017 (2026-05-18).**
> Handoff is now **automatic** (`ModeWatcher`, driven by the Anthropic
> `~/.claude/ide/<port>.lock`). The CortexBridge companion extension is
> **removed**; there is no manual Bridge/PC toggle. Normal operation:
> - **At PC:** open VS Code + Anthropic CC ext → bridge auto-kills its tmux
>   claude → owner=`pc` (PWA composer locks). *Nothing to do.*
> - **Leave PC:** close the VS Code window / drop Remote-SSH → the lockfile
>   disappears → after ≥45 s provably-gone, bridge auto-resumes tmux →
>   owner=`tmux` (PWA composer unlocks). *Nothing to do.*
> - **Recovery only:** if the PWA stays locked on `pc` after you closed VS
>   Code (stale lock, or VS Code left open just for file edits), the PWA
>   shows one **"Tiếp quản"** button that *enables itself once the bridge
>   proves the PC side is gone*. Tap it — it can never force an unsafe switch.
>
> The manual procedure below is kept for **emergency** (bridge/ModeWatcher
> down) and to document the underlying invariant.

How to move a single Claude Code session between **Mode A** (tmux-backed, driven from the iPhone PWA) and **Mode B** (Anthropic's native *Claude Code for VS Code* extension on the PC), without two `claude` processes fighting over one session UID.

Design rationale: ADR-015. Contract: spec 05.

## The invariant (read this first)

> **One `claude` process per session UID at a time.**

A session UID is the filename of an append-only JSONL. If two processes both `claude --resume <uid>` against it, you get interleaved writes and two independent API conversations — a *shared transcript log*, not a shared session. Mode A and Mode B are therefore **mutually exclusive**; switching is an explicit handoff, never simultaneous.

| | Mode A | Mode B |
|---|---|---|
| `claude` runs in | tmux window on the VM (session `cc`, window = projectId) | child process of Anthropic's native extension on the PC |
| Driven from | companion ext webview + iPhone PWA | native extension panel |
| `session_owner` (bridge) | `tmux` | `pc` |
| iPhone reply works? | yes | no (read-only banner) |

## Prerequisites

- Phase 1 stack deployed per [deployment.md](deployment.md): `cc-bridge` + `caddy` + `cloudflared` healthy.
- (Historical) A companion VS Code extension used to be required here. ADR-017 withdrew it: Mode A/B now switches automatically off the Anthropic IDE lockfile, and the extension was removed.
- Anthropic's *Claude Code for VS Code* extension installed on the PC (only needed for Mode B).
- A bearer token signed into both the companion ext and the PWA.

## A. Mode A → Mode B (hand the session to the PC)

| Step | Action | Expected |
|------|--------|----------|
| 1 | In the companion webview owner banner (or PWA), tap **Bàn giao cho PC ↗** — or run `CortexBridge: Hand off session to PC` | Confirm modal |
| 2 | Confirm | Bridge sends `/exit` to the tmux window, polls ≤3s, **force `tmux kill-window` if still alive** |
| 3 | — | `session_owner` flips to `pc`; SSE `owner_change` fires; companion + PWA show the read-only **Mode B** banner; composer disabled |
| 4 | On the PC, open Anthropic's native CC extension → resume the same session: `claude --resume <uuid>` (or its session picker) | Native panel loads the transcript; you keep working on the PC |

`<uuid>` is shown in the companion status bar (`⚡ <uuid> · …`) and the MiniHeader session picker. Because `~/.claude` is the single source of truth (ADR-011), the native extension on the VM sees the same JSONL.

> **This direction is robust** — the bridge *owns* the tmux window it spawned, and tmux does **not** respawn `claude`. The graceful `/exit` is delivered via the paste-buffer path, so if the tmux claude is sitting in a permission **menu** the paste can be eaten (same paste-vs-menu issue as the `/choice` fix) and `/exit` is lost — but the unconditional **force `kill-window` after 3 s** is the guaranteed backstop, so the tmux process always dies. (Optional hardening: send `/exit` via raw `send-keys` instead of paste; not urgent given the hard fallback.) Implemented in `HandoffEndpoint.HandoffToPc`.

## B. Mode B → Mode A (take the session back)

> **This is the hard, asymmetric direction.** The bridge **cannot** stop the PC-side claude — it is a child of the Anthropic extension, a different owner (ADR-015). And the Anthropic extension **supervises one `claude --resume <uid>` per OPEN chat session tab and respawns it**: `pkill`-ing the process while the chat tab is still open just makes the extension spawn a fresh one (the pid changes). The clean, lightweight release is therefore **closing the chat session tab/window itself** — *not* `pkill`, *not* disabling the whole extension. The GUI extension has **no `/exit`** slash command (that is CLI-only); do not rely on one.

| Step | Action | Expected |
|------|--------|----------|
| 1 | On the PC, **close the Anthropic CC chat session tab/window** for this session (close the editor tab, not just minimise the panel) | The PC-side `claude` for this UID exits and does **not** respawn — verify: `ps -eo args \| grep 'resume <uuid>' \| grep anthropic.claude-code` returns nothing |
| 2 | In the companion banner (or PWA), tap **Tiếp quản về tmux ↗** — or run `CortexBridge: Take over session from PC` | Confirm modal |
| 3 | Confirm | Bridge `tmux kill-window` (stale) then spawns `claude --resume <uuid>`, writes the timestamped `tmux` marker (`SetTmuxAsync`) |
| 4 | — | `session_owner` → `tmux`; SSE `owner_change`; banner clears; composer re-enabled on both surfaces |

If you skip Step 1 the bridge still spawns tmux's `claude` — and now **two** processes write the same JSONL (interleaved records, `[Request interrupted by user for tool use]`, stuck "thinking"). The confirm modal exists to prevent exactly this; honor it. Future automation (ADR-016): the companion ext closes the Anthropic chat tab itself via VS Code's `window.tabGroups` API — `pkill` is **not** a viable automation here because of the respawn.

## C. Recovery / edge cases

| Symptom | Cause | Fix |
|---|---|---|
| Banner stuck on **Mode B** after closing PC panel | `session_owner` row still `pc` (no auto-detect) | Tap **Tiếp quản** (Take over) once; or `POST /api/sessions/<proj>/handoff {"to":"tmux","confirmed":true}` |
| Take over returns `409 handoff.manual_action_required` | `confirmed` flag not sent | Use the banner button (sends `confirmed:true`) or add it to the API body |
| tmux window didn't die on hand-off | `/exit` not consumed (CC mid-tool) | Bridge force-kills after 3s; if not, `docker exec cc-bridge tmux kill-window -t cc:<proj>` then retry |
| Both surfaces show stale transcript | SSE dropped during handoff | Pull-to-refresh (PWA) / reload webview (`CortexBridge: Reload webview`) — JSONL is the source of truth, nothing lost |
| Two `claude` ran briefly against one UID | Took over without closing PC chat tab | Pick the surviving process; the JSONL has interleaved records but is not corrupt — `/compact` or start a fresh task session per ADR-014 |
| PC-side claude keeps coming back after you `pkill` it (pid changes) | Anthropic ext respawns one claude per **open chat tab** | Don't `pkill` — **close the chat session tab/window**; it then stays gone. Disabling the whole extension also works but is heavier and unnecessary |
| Every bridge/PWA/ext reply on a session returns `[Request interrupted]` + stuck thinking, but an isolated test session is fine | Two processes on one UID (e.g. native ext + a tmux `--resume` both live) — contention, not the paste bug | Enforce one owner: close the Anthropic chat tab (Mode B side) **or** `tmux kill-window` (Mode A side), leaving exactly one `claude` on the UID |

## D. Verify ownership any time

```bash
# from the VM
curl -s -H "Authorization: Bearer $TOKEN" \
  https://cortex.example.com/api/sessions/<projectId>/owner
# → {"owner":"tmux|pc|none","sessionUuid":"…","sinceUtc":"…"}

# tmux truth
docker exec cc-bridge tmux list-windows -t cc -F '#W'
```

`owner` is derived by `SessionOwnershipRegistry.Derive` from the **last JSONL record's `entrypoint`** + timestamped explicit markers — NOT "a tmux window exists" (a stale/parked tmux claude can coexist with the PC one). Priority: explicit `pc` marker → explicit `tmux` marker *iff newer than the last record* → `entrypoint=="claude-vscode"` ⇒ `pc` → tmux window alive ⇒ `tmux` → `none`. Consequence: after closing the Anthropic chat tab, owner may still read `pc` until a new record is written (or the `tmux` marker set) — tapping **Tiếp quản** (which writes `SetTmuxAsync`) is what flips it. The audit log records every handoff (`action=session_handoff`).
