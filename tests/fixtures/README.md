# Test Fixtures

## `jsonl/` — CC transcript samples

| File | Purpose |
|------|---------|
| `sample-session-001.jsonl` | Happy-path: summary + user → assistant (with tool_use) → tool_result (as user) → assistant text. Used by parser golden test. |
| `edge-cases.jsonl` | Robustness: Vietnamese with tone marks, sidechain, system record, multi-line code with embedded backticks/newlines, unknown record type, recovery after unknown. |
| `cortex-usage.jsonl` | ADR-025 Slice 1: assistant records with `mcp__cortexplexus__{recall_memory,get_callers}` tool_use blocks interleaved with normal `Edit`/`Bash`/text + a tool_result. Drives `CortexUsageTests` (count by tool, lastUsedAt = latest matching record, non-cortexplexus tools ignored). |

These are **synthetic but schema-faithful** — written from the spec in the JSONL + hooks spec (private decision log) §1. They MUST be re-validated against a real CC session before Wedge 3 (parser implementation) is signed off — see `tests/scripts/capture-real-jsonl.md`.

## `hooks/` — CC hook stdin samples

| File | Source | Purpose |
|------|--------|---------|
| `notification-payload.json` | Modeled per spec 03 §2.2 | Notification hook stdin contract |
| `stop-payload.json` | Modeled per spec 03 §2.3 | Stop hook stdin contract |

Same caveat: validate against real CC before signing off Wedge 6.

## Validation checklist before Wedge 3 / Wedge 6

- [ ] Run `tests/scripts/capture-real-jsonl.md` against a live CC session — confirm field names, value shapes, presence/absence of optional fields match these fixtures.
- [ ] If real CC differs, update fixtures (preferred) or update spec (if our spec was wrong).
- [ ] Diff against `sample-session-001.jsonl`: any new top-level field? Any rename?
