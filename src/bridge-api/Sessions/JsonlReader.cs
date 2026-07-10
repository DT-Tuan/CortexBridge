using System.Text.Json;
using System.Text.RegularExpressions;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Streaming line-by-line JSONL parser. Tolerates malformed lines (logs + skips),
/// unknown record types (passes through as kind:"unknown"), oversized lines (drops > 4MB).
/// Per spec 03 §1.3 / §1.8.
/// </summary>
public class JsonlReader
{
    private const int MaxLineBytes = 4 * 1024 * 1024;
    private readonly ILogger<JsonlReader> _log;

    public JsonlReader(ILogger<JsonlReader> log) => _log = log;

    /// <summary>
    /// Light-weight metadata pass for session list views (spec 04). Does NOT parse the
    /// entire file — extracts only fields needed to display a session card:
    /// firstAt, lastAt, messageCount (approximate = JSONL line count), firstUserText
    /// snippet (first user message text content, truncated), and cwd from the first
    /// parseable record. Reads sequentially in a single pass.
    /// </summary>
    public async Task<(DateTimeOffset? firstAt, DateTimeOffset? lastAt, int messageCount, string? firstUserText, string? cwd)>
        ExtractMetadataAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return (null, null, 0, null, null);

        DateTimeOffset? firstAt = null;
        DateTimeOffset? lastAt = null;
        int count = 0;
        string? firstUserText = null;
        string? cwd = null;
        string? lastTimestampSeen = null;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;
            count++;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var ts = TryString(root, "timestamp");
                if (firstAt is null && ts is not null && DateTimeOffset.TryParse(ts, out var firstParsed))
                    firstAt = firstParsed;
                if (ts is not null) lastTimestampSeen = ts;

                if (cwd is null && root.TryGetProperty("cwd", out var cwdEl)
                    && cwdEl.ValueKind == JsonValueKind.String)
                {
                    cwd = cwdEl.GetString();
                }

                if (firstUserText is null
                    && TryString(root, "type") == "user"
                    && root.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.Object
                    && msg.TryGetProperty("content", out var contentEl))
                {
                    var extracted = contentEl.ValueKind switch
                    {
                        JsonValueKind.String => contentEl.GetString(),
                        JsonValueKind.Array => FirstTextBlock(contentEl),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        firstUserText = extracted.Length > 80
                            ? extracted[..80] + "…"
                            : extracted;
                    }
                }
            }
            catch (JsonException) { /* skip malformed line */ }
        }

        if (lastTimestampSeen is not null && DateTimeOffset.TryParse(lastTimestampSeen, out var lastParsed))
            lastAt = lastParsed;

        return (firstAt, lastAt, count, firstUserText, cwd);
    }

    private static string? FirstTextBlock(JsonElement contentArray)
    {
        foreach (var block in contentArray.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (block.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String
                && t.GetString() == "text"
                && block.TryGetProperty("text", out var txt)
                && txt.ValueKind == JsonValueKind.String)
            {
                return txt.GetString();
            }
        }
        return null;
    }

    /// <summary>
    /// Reads all messages currently in the file from the byte offset, returns parsed messages
    /// and the new offset. The file is opened with FileShare.ReadWrite so CC can keep appending.
    /// </summary>
    public async Task<(List<SessionMessage> messages, long newOffset)> ReadFromOffsetAsync(
        string filePath,
        long fromOffset,
        string projectId,
        CancellationToken ct)
    {
        var messages = new List<SessionMessage>();

        if (!File.Exists(filePath))
            return (messages, fromOffset);

        using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (fs.Length < fromOffset)
        {
            _log.LogWarning("File {Path} shrank from {From} to {To} — caller should handle reset",
                filePath, fromOffset, fs.Length);
            return (messages, 0);
        }

        if (fromOffset > 0) fs.Seek(fromOffset, SeekOrigin.Begin);

        using var reader = new StreamReader(fs, leaveOpen: true);
        long lineStart = fromOffset;
        long currentOffset = fromOffset;
        int lineNumber = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            lineNumber++;

            // Track byte offset (approximate — UTF-8 bytes for line + newline)
            var lineByteCount = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
            currentOffset = lineStart + lineByteCount;
            lineStart = currentOffset;

            if (line.Length == 0) continue;

            if (lineByteCount > MaxLineBytes)
            {
                _log.LogWarning("Line {Line} exceeds {Max} bytes in {Path}, dropped",
                    lineNumber, MaxLineBytes, filePath);
                continue;
            }

            try
            {
                var msg = ParseLine(line, projectId);
                if (msg is not null) messages.Add(msg);
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Malformed JSONL at line {Line} in {Path}, skipping",
                    lineNumber, filePath);
            }
        }

        // Use the actual stream position as the authoritative offset
        return (messages, fs.Position);
    }

    /// <summary>
    /// Reads only the LAST <paramref name="limit"/> records without parsing the
    /// whole file — streams lines into a ring buffer (bounded memory) and parses
    /// just the tail. For huge transcripts (a 37 MB / 13.8k-record session
    /// produced a 57 MB transcript payload) the PWA only needs the recent tail on
    /// open; "load full history" re-fetches without a limit. Returns the parsed
    /// tail (chronological order), the total line count, and whether older
    /// records exist beyond the window.
    /// </summary>
    public async Task<(List<SessionMessage> messages, int totalLines, bool truncated, long tailOffset)> ReadTailAsync(
        string filePath, string projectId, int limit, CancellationToken ct)
    {
        var messages = new List<SessionMessage>();
        if (!File.Exists(filePath) || limit <= 0) return (messages, 0, false, 0);

        // Ring buffer keeps only the last `limit` lines while we scan forward —
        // O(file) I/O but O(limit) memory + O(limit) JSON parses (not O(file)).
        // tailOffset accumulates the SAME way ReadFromOffsetAsync does
        // (UTF-8 bytes + 1 newline per line) so a later ReadFromOffsetAsync(since=
        // tailOffset) seeks to an exact line boundary — the zero-gap SSE handshake.
        var ring = new string?[limit];
        var total = 0;
        long tailOffset = 0;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                ring[total++ % limit] = line;
                tailOffset += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
            }
        }

        var kept = Math.Min(total, limit);
        var startIdx = total <= limit ? 0 : total % limit;   // oldest slot of the kept window
        for (var k = 0; k < kept; k++)
        {
            var line = ring[(startIdx + k) % limit];
            if (string.IsNullOrEmpty(line)) continue;
            if (System.Text.Encoding.UTF8.GetByteCount(line) + 1 > MaxLineBytes) continue;
            try
            {
                var msg = ParseLine(line, projectId);
                if (msg is not null) messages.Add(msg);
            }
            catch (JsonException) { /* skip malformed tail line */ }
        }
        return (messages, total, total > limit, tailOffset);
    }

    private static SessionMessage? ParseLine(string line, string projectId)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        var type = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;

        var uuid = TryString(root, "uuid");
        var parentUuid = TryString(root, "parentUuid");
        var sessionUuid = TryString(root, "sessionId");
        var ts = TryString(root, "timestamp");
        var userType = TryString(root, "userType");
        var isSidechain = root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;
        var rawClone = root.Clone();

        return type switch
        {
            "summary" => new SessionMessage(
                Kind: "message",
                Uuid: uuid ?? TryString(root, "leafUuid"),
                ParentUuid: null,
                SessionUuid: sessionUuid,
                ProjectId: projectId,
                Timestamp: ts,
                Role: "summary",
                UserType: null,
                IsSidechain: false,
                Content: null,
                Text: TryString(root, "summary"),
                Raw: rawClone),

            "user" => BuildMessageRecord(
                root, projectId, uuid, parentUuid, sessionUuid, ts, "user", userType, isSidechain, rawClone),

            "assistant" => BuildMessageRecord(
                root, projectId, uuid, parentUuid, sessionUuid, ts, "assistant", userType, isSidechain, rawClone),

            "system" => BuildSystemRecord(root, projectId, uuid, parentUuid, sessionUuid, ts, rawClone),

            null => new SessionMessage(
                Kind: "unknown",
                Uuid: uuid,
                ParentUuid: parentUuid,
                SessionUuid: sessionUuid,
                ProjectId: projectId,
                Timestamp: ts,
                Role: null,
                UserType: null,
                IsSidechain: false,
                Content: null,
                Text: null,
                Raw: rawClone),

            _ => new SessionMessage(
                Kind: "unknown",
                Uuid: uuid,
                ParentUuid: parentUuid,
                SessionUuid: sessionUuid,
                ProjectId: projectId,
                Timestamp: ts,
                Role: type,
                UserType: null,
                IsSidechain: false,
                Content: null,
                Text: null,
                Raw: rawClone),
        };
    }

    /// <summary>
    /// CC writes many internal "system" subtypes (stop_hook_summary, turn_duration,
    /// local_command stdout wrappers) that have no user-facing content. Mark these
    /// as kind="unknown" so the PWA's existing internal-noise filter hides them.
    /// Show only system records that actually carry meaningful text.
    /// </summary>
    private static SessionMessage BuildSystemRecord(
        JsonElement root,
        string projectId,
        string? uuid,
        string? parentUuid,
        string? sessionUuid,
        string? ts,
        JsonElement rawClone)
    {
        var subtype = TryString(root, "subtype");
        var content = TryString(root, "content");

        // /compact writes a `system / subtype:compact_boundary` marker carrying
        // compactMetadata{trigger,preTokens,postTokens}. Surface it as a
        // dedicated kind="compact" so the PWA renders a one-line divider
        // instead of a vague "ⓘ Conversation compacted" line — and the paired
        // huge isCompactSummary user record is suppressed in BuildMessageRecord.
        // Text is a stable backend-owned "trigger|preTokens|postTokens" tuple;
        // the PWA owns number formatting + i18n (no coupling to CC raw fields).
        if (string.Equals(subtype, "compact_boundary", StringComparison.Ordinal))
        {
            var trigger = "?";
            int preTokens = 0, postTokens = 0;
            if (root.TryGetProperty("compactMetadata", out var cm)
                && cm.ValueKind == JsonValueKind.Object)
            {
                trigger = TryString(cm, "trigger") ?? "?";
                preTokens = GetIntOrZero(cm, "preTokens");
                postTokens = GetIntOrZero(cm, "postTokens");
            }
            return new SessionMessage(
                Kind: "compact",
                Uuid: uuid,
                ParentUuid: parentUuid,
                SessionUuid: sessionUuid,
                ProjectId: projectId,
                Timestamp: ts,
                Role: "compact",
                UserType: null,
                IsSidechain: false,
                Content: null,
                Text: $"{trigger}|{preTokens}|{postTokens}",
                Raw: rawClone);
        }

        // local_command stdout/stderr records wrap their payload in
        // <local-command-stdout>…</local-command-stdout> (or -stderr). Showing
        // that raw XML wrapper in the transcript is ugly; strip it to the inner
        // text so it renders as a clean "ⓘ <text>" line — and an empty wrapper
        // (the common /exit, /clear no-output case) collapses to whitespace and
        // is hidden as noise. Done here so both surfaces benefit, one place.
        var display = string.Equals(subtype, "local_command", StringComparison.Ordinal)
            ? CleanLocalCmdOutput(content)
            : content;

        bool isNoise = subtype switch
        {
            "stop_hook_summary" => true,
            "turn_duration" => true,
            "local_command" => string.IsNullOrWhiteSpace(display),
            _ => string.IsNullOrWhiteSpace(content)
        };

        return new SessionMessage(
            Kind: isNoise ? "unknown" : "message",
            Uuid: uuid,
            ParentUuid: parentUuid,
            SessionUuid: sessionUuid,
            ProjectId: projectId,
            Timestamp: ts,
            Role: isNoise ? null : "system",
            UserType: null,
            IsSidechain: false,
            Content: null,
            Text: isNoise ? null : display,
            Raw: rawClone);
    }

    /// <summary>
    /// Strip the outer &lt;local-command-stdout&gt;/&lt;local-command-stderr&gt;
    /// wrapper CC writes around local-command output, returning the trimmed
    /// inner text (empty ⇒ "" so the caller hides it). No regex (per module
    /// rule) — exact known-tag prefix/suffix slice.
    /// </summary>
    private static string StripLocalCmdWrapper(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var c = content.Trim();
        foreach (var tag in new[] { "stdout", "stderr" })
        {
            var open = $"<local-command-{tag}>";
            var close = $"</local-command-{tag}>";
            if (c.StartsWith(open, StringComparison.Ordinal)
                && c.EndsWith(close, StringComparison.Ordinal)
                && c.Length >= open.Length + close.Length)
            {
                return c[open.Length..^close.Length].Trim();
            }
        }
        return c;
    }

    // CC's /compact + some shells write SGR colour codes into local-command
    // output (e.g. the dimmed "Compacted (ctrl+o…)"). Strip ANSI CSI escapes
    // so the transcript shows clean text, not raw \e[2m… sequences. Operates
    // on an already-extracted string value (not a JSONL line), so the
    // module's "no regex for JSONL parsing" rule does not apply.
    private static readonly Regex AnsiCsi = new("\u001b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private static string CleanLocalCmdOutput(string? content) =>
        AnsiCsi.Replace(StripLocalCmdWrapper(content), string.Empty).Trim();

    private static SessionMessage BuildMessageRecord(
        JsonElement root,
        string projectId,
        string? uuid,
        string? parentUuid,
        string? sessionUuid,
        string? ts,
        string role,
        string? userType,
        bool isSidechain,
        JsonElement raw)
    {
        JsonElement? content = null;
        TokenUsage? usage = null;

        // CC injects records on resume/continuation that have no user-facing
        // value and that Anthropic's own VS Code extension hides:
        //   - user record with isMeta:true            → "Continue from where you left off."
        //   - assistant record with model:"<synthetic>" → "No response requested."
        //   - user record with isCompactSummary:true  → the huge /compact
        //       context-seed summary (re-injected as a role:user message so the
        //       model can continue). The compaction is shown by the dedicated
        //       kind="compact" divider (from the compact_boundary record), so
        //       the multi-KB summary bubble is pure noise here.
        // Classify all as kind:"unknown" so PWA + companion ext (which already
        // drop unknown) hide them — one fix, both surfaces, no client change.
        var isMeta = root.TryGetProperty("isMeta", out var metaEl)
            && metaEl.ValueKind == JsonValueKind.True;
        var synthetic = root.TryGetProperty("message", out var mProbe)
            && mProbe.ValueKind == JsonValueKind.Object
            && mProbe.TryGetProperty("model", out var modelProbe)
            && modelProbe.ValueKind == JsonValueKind.String
            && modelProbe.GetString() == "<synthetic>";
        var isCompactSummary = root.TryGetProperty("isCompactSummary", out var csEl)
            && csEl.ValueKind == JsonValueKind.True;
        if (isMeta || synthetic || isCompactSummary)
        {
            return new SessionMessage(
                Kind: "unknown",
                Uuid: uuid,
                ParentUuid: parentUuid,
                SessionUuid: sessionUuid,
                ProjectId: projectId,
                Timestamp: ts,
                Role: null,
                UserType: null,
                IsSidechain: false,
                Content: null,
                Text: null,
                Raw: raw);
        }

        // CC writes local-command OUTPUT as a role:"user" record whose content
        // is a STRING wrapped in <local-command-stdout>…</local-command-stdout>
        // (or -stderr): the /exit "Goodbye!/Bye!/See ya!" echo, the /compact
        // "Compacted (ctrl+o…)" echo (ANSI-coloured), "Error: Compaction
        // cancelled" etc. It is command output, NOT user speech — render it as
        // the same subtle "ⓘ <text>" system line as the system/subtype:
        // local_command path (empty ⇒ hidden noise), ANSI stripped. Records
        // whose content is <command-name>/<local-command-caveat> are left
        // untouched so the PWA's command-badge / caveat-hide logic still works.
        if (role == "user"
            && root.TryGetProperty("message", out var lcMsg)
            && lcMsg.ValueKind == JsonValueKind.Object
            && lcMsg.TryGetProperty("content", out var lcEl)
            && lcEl.ValueKind == JsonValueKind.String)
        {
            var s = lcEl.GetString() ?? string.Empty;
            var st = s.TrimStart();
            // Harness-injected background-task events (Task tool run_in_background)
            // are delivered as role:user string records wrapped in
            // <task-notification>…</task-notification> (task id, output-file path,
            // status, summary). Pure orchestration noise — CC's own UI never shows
            // them and the user can't act on them, but they leaked into the PWA
            // transcript as user bubbles. Hide as kind:"unknown" (PWA + ext both
            // drop unknown) — one fix for every project + REST + SSE.
            if (st.StartsWith("<task-notification>", StringComparison.Ordinal))
            {
                return new SessionMessage("unknown", uuid, parentUuid, sessionUuid,
                    projectId, ts, null, null, false, null, null, raw);
            }
            if (st.StartsWith("<local-command-stdout>", StringComparison.Ordinal)
                || st.StartsWith("<local-command-stderr>", StringComparison.Ordinal))
            {
                var inner = CleanLocalCmdOutput(s);
                return string.IsNullOrWhiteSpace(inner)
                    ? new SessionMessage("unknown", uuid, parentUuid, sessionUuid,
                        projectId, ts, null, null, false, null, null, raw)
                    : new SessionMessage("message", uuid, parentUuid, sessionUuid,
                        projectId, ts, "system", null, false, null, inner, raw);
            }
        }

        if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
        {
            if (msg.TryGetProperty("content", out var contentEl))
            {
                // content can be a string OR an array of content blocks
                content = contentEl.Clone();
            }
            // Token usage on assistant records — used by PWA to show context size
            // and warn user when to /compact. CC writes usage per assistant turn.
            if (role == "assistant" && msg.TryGetProperty("usage", out var usageEl)
                && usageEl.ValueKind == JsonValueKind.Object)
            {
                var model = msg.TryGetProperty("model", out var mEl)
                    && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() : null;
                usage = new TokenUsage(
                    InputTokens: GetIntOrZero(usageEl, "input_tokens"),
                    OutputTokens: GetIntOrZero(usageEl, "output_tokens"),
                    CacheCreationInputTokens: GetIntOrZero(usageEl, "cache_creation_input_tokens"),
                    CacheReadInputTokens: GetIntOrZero(usageEl, "cache_read_input_tokens"),
                    Model: model);
            }
        }

        return new SessionMessage(
            Kind: "message",
            Uuid: uuid,
            ParentUuid: parentUuid,
            SessionUuid: sessionUuid,
            ProjectId: projectId,
            Timestamp: ts,
            Role: role,
            UserType: userType,
            IsSidechain: isSidechain,
            Content: content,
            Text: null,
            Raw: raw,
            Usage: usage);
    }

    private static int GetIntOrZero(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el)) return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var i) => i,
            JsonValueKind.Number when el.TryGetInt64(out var l) => (int)Math.Min(l, int.MaxValue),
            _ => 0,
        };
    }

    private static string? TryString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Null => null,
            _ => null,
        };
    }
}
