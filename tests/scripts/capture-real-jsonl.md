# Capture real CC artifacts for fixture validation

Run these once on any machine that has a working `claude` CLI install, to validate (and replace) the synthetic fixtures.

## 1. Capture a real JSONL transcript

```bash
# Pick any project dir that has been used recently with `claude`
ls -lt ~/.claude/projects/*/

# Grab the most-recent .jsonl
LATEST=$(ls -t ~/.claude/projects/*/*.jsonl | head -1)
echo "Capturing: $LATEST"

# Copy to fixtures (replace the synthetic one)
cp "$LATEST" tests/fixtures/jsonl/real-session-redacted.jsonl

# Manually scrub anything sensitive (project paths, code snippets, secrets)
# A short session of ~10 turns is enough; longer is better for edge cases.
```

## 2. Capture a real hook stdin payload

Install a no-op debug hook in `~/.claude/settings.json` for ONE run:

```json
{
  "hooks": {
    "Notification": [
      { "matcher": "", "hooks": [ { "type": "command", "command": "tee /tmp/cc-notify-stdin.json > /dev/null" } ] }
    ],
    "Stop": [
      { "matcher": "", "hooks": [ { "type": "command", "command": "tee /tmp/cc-stop-stdin.json > /dev/null" } ] }
    ]
  }
}
```

Run a CC session that triggers a permission prompt (e.g. ask it to run a Bash command), then:

```bash
cat /tmp/cc-notify-stdin.json
cat /tmp/cc-stop-stdin.json

cp /tmp/cc-notify-stdin.json tests/fixtures/hooks/real-notification.json
cp /tmp/cc-stop-stdin.json   tests/fixtures/hooks/real-stop.json
```

Remove the debug hook from settings.json afterwards.

## 3. Diff and reconcile

```bash
# Compare synthetic vs real
diff tests/fixtures/hooks/notification-payload.json tests/fixtures/hooks/real-notification.json
diff tests/fixtures/hooks/stop-payload.json         tests/fixtures/hooks/real-stop.json
```

If the synthetic and real disagree, update the **spec** if our spec was wrong, or update the **fixture** if we just wrote a slightly different example. Whichever path: keep `docs/specs/03-jsonl-and-hooks.md` and the fixtures in lockstep before parser code lands.
