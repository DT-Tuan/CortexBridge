using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CortexBridge.Api.Common;
using CortexBridge.Api.Tmux;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// GET /api/sessions/{projectId}/prompt — parse CC's CURRENTLY-VISIBLE
/// interactive menu from the tmux pane so the PWA banner renders the REAL
/// options (true numbers + labels) instead of a 1/2/3 guess.
///
/// AskUserQuestion multi-question note (ADR-017 follow-up, verified 2026-05-18):
/// CC does NOT write the AskUserQuestion tool_use to the session JSONL until
/// it is answered, and the TUI renders only ONE question tab at a time — so a
/// pending multi-question prompt was invisible past Q1 (user couldn't answer
/// Q2…Qn → CC stuck). The questions ARE reachable from the pane by a
/// READ-ONLY tab traversal: Right/Left switch question tabs (proven
/// non-destructive + reversible — they never select; only Space/Enter/Submit
/// would), CC does NOT consistently render a "(K)" prefix (verified 2026-05-18) so
/// position is keyed by the VISIBLE QUESTION TEXT. We walk every tab under the
/// per-project reply lock (Left clamps→tab1, then Right 1..n), capture each,
/// restore to the entry tab by question text, cache. Pure NAVIGATION only; the
/// answer path (compose text → bridge Esc-then-paste) is unchanged.
/// </summary>
public static class PromptEndpoint
{
    public record PromptOption(int Num, string Label);
    public record AskQ(int Index, string? Question, List<PromptOption> Options, bool Multi = false);
    public record PromptResponse(
        bool Found, string? Question, List<PromptOption> Options, bool CanEsc,
        bool IsAsk = false,
        List<string>? AskSections = null,
        int AskAnswered = 0,
        // All questions of a pending multi-question AskUserQuestion, collected
        // by the read-only tab traversal (null when not a multi-Q picker, when
        // traversal was skipped under contention, or when it aborted).
        List<AskQ>? AskQuestions = null,
        // The assistant's analysis prose printed immediately ABOVE the
        // AskUserQuestion picker, scraped from the tmux pane scrollback. CC
        // bundles this text and the AskUserQuestion tool_use into ONE assistant
        // message and does not persist it to JSONL until the question is
        // answered — so during the live window the pane is the ONLY source of
        // the context the questions refer to. Best-effort; null when not an
        // AskUserQuestion or no prose could be recovered.
        string? AskContext = null);

    // Optional cursor glyph (❯ > › » ‣ • * -), then "N." or "N)" then the label.
    private static readonly Regex OptionRe =
        new(@"^\s*[❯>›»‣•\*\-]?\s*(\d{1,2})[.)]\s+(\S.*?)\s*$", RegexOptions.Compiled);

    // CC prefixes the visible AskUserQuestion question with its 1-based tab
    // index, e.g. "(2) Ưu tiên?". This is the ground truth for "which tab am I
    // on" — every traversal move is verified against it.
    private static readonly Regex AskCurRe =
        new(@"^\s*\((\d{1,2})\)\s+(\S.*?)\s*$", RegexOptions.Compiled);

    // One tab segment on the AskUserQuestion tab bar: a checkbox glyph (or
    // [ ]/[x]) then the section label, delimited by ≥2 spaces or end/arrow.
    // Checked glyphs (☑ ☒ ✓ ✔ ●) ⇒ that question already answered.
    private static readonly Regex AskTabRe = new(
        @"([☐☑☒✓✔◯●]|\[[ xX]\])\s*([^\n]*?)(?=\s{2,}(?:[☐☑☒✓✔◯●]|\[[ xX]\])|\s*[→»]\s*$|\s*$)",
        RegexOptions.Compiled);

    private static readonly TimeSpan NavSettle = TimeSpan.FromMilliseconds(300);
    private const int MaxTabs = 8; // bound traversal — AskUserQuestion ≤4 in practice

    // CC's per-message bullet ("⏺ text" for assistant prose, "⏺ Tool(…)" for a
    // tool call). The capture group is the text after the bullet.
    private static readonly Regex BulletRe =
        new(@"^\s*[⏺●]\s+(.*)$", RegexOptions.Compiled);
    // A tool-call bullet payload: CamelCase tool name then "(" (Read(…),
    // AskUserQuestion(…)). Distinguishes a tool line from analysis prose.
    private static readonly Regex ToolCallRe =
        new(@"^[A-Z][A-Za-z0-9_]*\(", RegexOptions.Compiled);
    // A tool-result continuation line ("⎿ …").
    private static readonly Regex ResultRe = new(@"^\s*⎿", RegexOptions.Compiled);
    // Same full-width rule PaneClassifier uses to mark where the live menu box
    // begins; the analysis prose is the assistant block just above it.
    private static readonly Regex CtxDividerRe =
        new(@"^[ \t]*[─━—═-]{20,}[ \t]*$", RegexOptions.Compiled);
    private const int MaxContextScan = 120;   // bound the upward walk
    private const int ContextCap = 1200;      // trim very long prose for the card

    /// <summary>
    /// Recover the assistant's analysis prose that sits ABOVE a live
    /// AskUserQuestion picker from a (scrollback-inclusive) pane capture. The
    /// picker box always renders below the last full-width divider, so the
    /// prose is the last assistant <c>⏺</c> text block above that divider
    /// (skipping the AskUserQuestion tool-call bullet + any tool results between
    /// them). Pure + static for unit testing; best-effort, returns null when no
    /// prose block is recoverable. NEVER logged (may contain code/secrets).
    /// </summary>
    public static string? ExtractAskContext(string pane)
    {
        if (string.IsNullOrEmpty(pane)) return null;
        var lines = pane.Replace("\r", "").Split('\n');

        var menuStart = lines.Length;
        for (var i = lines.Length - 1; i >= 0; i--)
            if (CtxDividerRe.IsMatch(lines[i])) { menuStart = i; break; }

        // Last PROSE bullet above the menu (skip "⏺ AskUserQuestion(…)" etc.).
        var bulletIdx = -1;
        var lo = Math.Max(0, menuStart - MaxContextScan);
        for (var i = menuStart - 1; i >= lo; i--)
        {
            var m = BulletRe.Match(lines[i]);
            if (m.Success && !ToolCallRe.IsMatch(m.Groups[1].Value.TrimStart()))
            {
                bulletIdx = i;
                break;
            }
        }
        if (bulletIdx < 0) return null;

        var collected = new List<string>();
        for (var i = bulletIdx; i < menuStart; i++)
        {
            var line = lines[i];
            if (CtxDividerRe.IsMatch(line)) break;
            var m = BulletRe.Match(line);
            if (m.Success)
            {
                var payload = m.Groups[1].Value.TrimStart();
                if (ToolCallRe.IsMatch(payload)) break; // reached the tool call → stop
                collected.Add(payload.TrimEnd());
                continue;
            }
            if (ResultRe.IsMatch(line)) break;          // tool result block → stop
            collected.Add(CleanContextLine(line));
        }

        var text = string.Join("\n", collected).Trim();
        text = Regex.Replace(text, @"\n{3,}", "\n\n");  // collapse blank runs
        if (text.Length == 0) return null;
        return text.Length > ContextCap ? text[..ContextCap] + "…" : text;
    }

    private static string CleanContextLine(string s)
    {
        s = s.TrimEnd();
        if (s.StartsWith("  ")) s = s[2..]; // CC's 2-space continuation gutter
        return s.Trim('│').TrimEnd('│', ' ', '\t');
    }

    // Per-project cache: traverse once per distinct picker, not every poll
    // (the PWA polls /prompt repeatedly). Keyed fingerprint = sections + N;
    // invalidated by fingerprint change or age.
    private static readonly ConcurrentDictionary<string, (string Fp, List<AskQ> Qs, DateTimeOffset At)>
        _askCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);

    private static (bool isAsk, List<string> sections, int answered) ParseAskTabs(string[] lines)
    {
        foreach (var raw in lines)
        {
            var line = raw;
            if (line.IndexOf("Submit", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!line.Contains('☐') && !line.Contains('☑') && !line.Contains('☒') && !line.Contains('✔')
                && !line.Contains('✓') && !line.Contains('●') && !line.Contains('◯')
                && !line.Contains("[ ]") && !line.Contains("[x]") && !line.Contains("[X]"))
                continue;

            var sections = new List<string>();
            var answered = 0;
            foreach (Match m in AskTabRe.Matches(line.Replace("←", "  ").Replace("»", "  ")))
            {
                var mark = m.Groups[1].Value;
                var label = m.Groups[2].Value.Trim().TrimEnd('→', ' ', '\t');
                if (label.Length == 0
                    || label.Equals("Submit", StringComparison.OrdinalIgnoreCase)) continue;
                var isChecked = mark is "☑" or "☒" or "✓" or "✔" or "●" || mark is "[x]" or "[X]";
                if (isChecked) answered++;
                sections.Add(label.Length > 40 ? label[..40] + "…" : label);
            }
            if (sections.Count > 0) return (true, sections, answered);
        }
        return (false, new List<string>(), 0);
    }

    /// <summary>
    /// Parse the currently-visible menu from a pane capture: the active
    /// numbered-option block (last block starting at "1."), the question line
    /// above it, and — for AskUserQuestion — the "(K)" current tab index.
    /// </summary>
    private static (string? question, List<PromptOption> options, int? askK, bool multi) ParseVisible(string pane)
    {
        var multi = false;
        var lines = pane.Replace("\r", "").Split('\n');

        var hits = new List<(int idx, int num, string label)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = OptionRe.Match(lines[i]);
            if (m.Success) hits.Add((i, int.Parse(m.Groups[1].Value), m.Groups[2].Value));
        }

        var start = -1;
        for (var k = hits.Count - 1; k >= 0; k--)
            if (hits[k].num == 1) { start = k; break; }

        var opts = new List<PromptOption>();
        var firstLine = -1;
        if (start >= 0)
        {
            firstLine = hits[start].idx;
            var prevIdx = hits[start].idx;
            var prevNum = 0;
            for (var k = start; k < hits.Count; k++)
            {
                var h = hits[k];
                if (h.num != prevNum + 1) break;
                if (k != start && h.idx - prevIdx > 6) break;
                if (OptBoxRe.IsMatch(h.label)) multi = true;
                opts.Add(new PromptOption(h.num, Clean(h.label)));
                prevIdx = h.idx;
                prevNum = h.num;
            }
        }

        string? question = null;
        int? askK = null;
        if (firstLine > 0)
        {
            for (var i = firstLine - 1; i >= 0 && i >= firstLine - 6; i--)
            {
                var t = lines[i].Trim();
                if (t.Length == 0 || OptionRe.IsMatch(lines[i])) continue;
                var am = AskCurRe.Match(lines[i]);
                if (am.Success)
                {
                    askK = int.Parse(am.Groups[1].Value);
                    question = Clean(am.Groups[2].Value);
                }
                else question = Clean(t);
                break;
            }
        }
        return (question, opts, askK, multi);
    }

    public static void MapPrompt(this IEndpointRouteBuilder app)
        => app.MapGet("/api/sessions/{projectId}/prompt", Handler);

    private static async Task<IResult> Handler(
        string projectId, TmuxClient tmux, ProjectReplyMutex replyMutex,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Prompt");
        if (projectId.Contains('/') || projectId.Contains('\\') || projectId.Contains(".."))
            return ResultsHelpers.Error(400, "project.bad_id",
                "projectId must be a single directory name");

        var none = new PromptResponse(false, null, new List<PromptOption>(), false);

        if (!await tmux.WindowExistsAsync(projectId, ct))
            return Results.Json(none, Json.Default);

        string pane;
        try { pane = await tmux.CapturePaneAsync(projectId, ct); }
        catch (TmuxException) { return Results.Json(none, Json.Default); }

        var lines = pane.Replace("\r", "").Split('\n');
        var (question, opts, _, _) = ParseVisible(pane);
        var canEsc = Regex.IsMatch(pane, "esc to cancel", RegexOptions.IgnoreCase)
            || opts.Exists(o => o.Label.Contains("(esc)", StringComparison.OrdinalIgnoreCase));
        var (isAsk, sections, answered) = ParseAskTabs(lines);

        // Recover the analysis prose above the picker from the pane scrollback
        // (the visible capture often has it scrolled off the top). The prose is
        // absent from JSONL until the question is answered, so this is the only
        // live source of the context the questions refer to.
        string? askContext = null;
        if (isAsk)
        {
            try
            {
                var deep = await tmux.CapturePaneAsync(projectId, ct, historyLines: 200);
                askContext = ExtractAskContext(deep);
            }
            catch (TmuxException) { /* best-effort — degrade to no context */ }
        }

        List<AskQ>? askQuestions = null;
        if (isAsk && sections.Count > 1)
        {
            var fp = string.Join("|", sections) + "#" + sections.Count;
            if (_askCache.TryGetValue(projectId, out var c)
                && c.Fp == fp && DateTimeOffset.UtcNow - c.At < CacheTtl)
            {
                askQuestions = c.Qs; // already traversed this picker
            }
            else
            {
                askQuestions = await TryCollectAllAsync(
                    projectId, tmux, replyMutex, sections.Count, question, log, ct);
                if (askQuestions is not null)
                    _askCache[projectId] = (fp, askQuestions, DateTimeOffset.UtcNow);
            }
        }
        else if (!isAsk)
        {
            _askCache.TryRemove(projectId, out _); // picker gone
        }

        return Results.Json(
            new PromptResponse(
                Found: opts.Count > 0 || isAsk,
                Question: question,
                Options: opts,
                CanEsc: canEsc,
                IsAsk: isAsk,
                AskSections: isAsk ? sections : null,
                AskAnswered: answered,
                AskQuestions: askQuestions,
                AskContext: askContext),
            Json.Default);
    }

    /// <summary>
    /// Read-only tab traversal: under the per-project reply lock, walk to tab
    /// 1, capture tabs 1..N, restore the original tab. Every Right/Left is
    /// verified against the "(K)" marker; any anomaly aborts (best-effort
    /// restore) and returns null so the caller degrades to single-question.
    /// Only Left/Right are ever sent — proven non-destructive.
    /// </summary>
    // Strip a leading checkbox token CC renders for multiSelect options
    // ("1. [ ] Foo" → label "[ ] Foo" → "Foo") and stray cursor/box glyphs.
    private static readonly Regex OptLeadRe =
        new(@"^\s*(?:\[[ xX]\]|[☐☑☒✓✔◯●❯])\s*", RegexOptions.Compiled);

    // A multiSelect question renders its options with a checkbox token
    // ("1. [ ] Foo" / "1. ☐ Foo"); single-select has none. Detected on the
    // RAW option label (before Clean strips it) → AskQ.Multi.
    private static readonly Regex OptBoxRe =
        new(@"^\s*(?:\[[ xX]\]|[☐☑☒])", RegexOptions.Compiled);

    /// <summary>
    /// Collect every question of a pending multi-question AskUserQuestion via a
    /// read-only tab walk. CC does NOT consistently render the "(K)" tab index
    /// (single-select-first / some versions omit it), so position is keyed by
    /// the VISIBLE QUESTION TEXT, not "(K)". Left clamps at the leftmost tab, so
    /// (n-1) Lefts from anywhere lands on tab 1 (extra are harmless no-ops);
    /// then capture tab 1..n going Right; restore to the original tab by
    /// matching the question text captured at entry. Only Left/Right are sent.
    /// </summary>
    private static async Task<List<AskQ>?> TryCollectAllAsync(
        string projectId, TmuxClient tmux, ProjectReplyMutex replyMutex,
        int n, string? originQ, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(originQ) || n < 2 || n > MaxTabs)
        {
            log.LogDebug(
                "Prompt: {Project} AskUserQuestion traversal not run (n={N}, originQ empty={Empty})",
                projectId, n, string.IsNullOrWhiteSpace(originQ));
            return null;
        }

        using var lease = replyMutex.TryAcquire(projectId);
        if (lease is null)
        {
            log.LogDebug("Prompt: {Project} reply in flight — skip AskUserQuestion traversal", projectId);
            return null;
        }

        async Task<(string? q, List<PromptOption> o, bool m)> Cap()
        {
            try
            {
                var (q, o, _, mm) = ParseVisible(await tmux.CapturePaneAsync(projectId, ct));
                return (q, o, mm);
            }
            catch (TmuxException) { return (null, new List<PromptOption>(), false); }
        }
        async Task<bool> Send(string dir)
        {
            try { await tmux.SendKeyAsync(projectId, dir, ct); }
            catch (TmuxException) { return false; }
            await Task.Delay(NavSettle, ct);
            return true;
        }
        async Task GoToQ(string target)
        {
            for (var k = 0; k < n; k++)
            {
                var (cq, _, _) = await Cap();
                if (cq == target) return;
                if (!await Send("Left")) return;
            }
        }

        try
        {
            // Reach tab 1 (Left clamps at leftmost).
            for (var i = 0; i < n - 1; i++)
                if (!await Send("Left"))
                {
                    log.LogWarning("Prompt: {Project} AskUserQuestion nav Left failed — abort", projectId);
                    await GoToQ(originQ);
                    return null;
                }

            var collected = new List<AskQ>();
            var seen = new List<string>();
            for (var i = 1; i <= n; i++)
            {
                var (q, o, mm) = await Cap();
                if (string.IsNullOrWhiteSpace(q))
                {
                    log.LogWarning("Prompt: {Project} AskUserQuestion tab {I} unreadable — abort", projectId, i);
                    await GoToQ(originQ);
                    return null;
                }
                collected.Add(new AskQ(i, q, o, mm));
                seen.Add(q!);
                if (i < n && !await Send("Right"))
                {
                    log.LogWarning("Prompt: {Project} AskUserQuestion nav Right failed — abort", projectId);
                    await GoToQ(originQ);
                    return null;
                }
            }

            // Restore to the entry tab by question text (we are at tab n now).
            var originIdx = seen.FindIndex(x => x == originQ);
            if (originIdx < 0) originIdx = seen.Count - 1;
            for (var p = n; p > originIdx + 1; p--)
                if (!await Send("Left")) break;
            var (vq, _, _) = await Cap();
            if (vq != originQ)
                log.LogWarning(
                    "Prompt: {Project} AskUserQuestion restore mismatch — picker may be off the entry tab",
                    projectId);

            log.LogInformation(
                "Prompt: {Project} collected all {N} AskUserQuestion tabs via read-only traversal",
                projectId, n);
            return collected;
        }
        catch (OperationCanceledException)
        {
            return null; // request aborted mid-traversal — retried next poll
        }
    }

    private static string Clean(string s)
    {
        s = s.Replace("❯", "").Trim();
        s = OptLeadRe.Replace(s, "");
        s = s.TrimEnd('│', '─', '╌', ' ', '\t');
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}
