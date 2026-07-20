using System.Text.RegularExpressions;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Classifies a captured tmux pane (CC's visible screen) into the session's
/// REAL turn state, so <see cref="ProcessingWatchdog"/> can tell a genuinely
/// dead Processing latch (safe to clear → composer unlocks) apart from a turn
/// that is legitimately running or blocked on the user.
///
/// Why this exists: the watchdog used to clear Processing purely on
/// "no activity hook for 120s". Two silent-but-busy states stay quiet past
/// 120s yet are NOT dead, so they were cleared early and the PWA/ext composer
/// unlocked mid-turn (the chronic "composer unlocks early" bug):
///   S1 — stuck AskUserQuestion: fires NEITHER an activity hook NOR the
///        Notification hook; the human just hasn't answered yet.
///   S2 — one long tool-call / long think: emits ZERO PreToolUse/PostToolUse
///        for minutes.
///
/// CRITICAL — classify only the LIVE PROMPT REGION, never the whole capture.
/// CC renders the entire conversation transcript INSIDE the visible pane, so a
/// PAST turn's "esc to interrupt" spinner line stays on screen ABOVE the live
/// prompt (capture-pane -p already excludes the history buffer, but the
/// visible screen itself still carries that stale line). Matching the whole
/// pane mis-fired Working while CC was actually Blocked/Idle → Processing
/// latched true → composer gated → user "treo" (observed LIVE on a session
/// with processing history; an early version that matched the whole pane only
/// passed tests because the test pane was scrollback-clean). The authoritative
/// live state is the bottom block only: everything AFTER the LAST full-width
/// divider rule (CC brackets the input box / precedes an open menu with one).
///
/// Pure + static = unit-testable with no tmux. Matching within the live region
/// is conservative: only an explicit busy/menu marker keeps Processing;
/// anything else is Idle — the watchdog's original dead-latch case (/clear,
/// /compact, /exit, crash-respawn) lands on a clean prompt with no markers.
/// </summary>
public static class PaneClassifier
{
    public enum PaneState
    {
        /// <summary>A turn is actively running ("esc to interrupt" footer). Keep Processing.</summary>
        Working,
        /// <summary>An interactive menu/prompt is open, waiting on the user. Surface needsInput.</summary>
        Blocked,
        /// <summary>Clean empty prompt (or unrecognised). Genuine dead latch — clear Processing.</summary>
        Idle,
    }

    // CC renders this footer CONTINUOUSLY while a turn runs (spinner +
    // "(esc to interrupt)"), including a single multi-minute tool call or a
    // long think that emits zero PreToolUse/PostToolUse — that is exactly S2.
    private static readonly Regex WorkingRe =
        new("esc to interrupt", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // An interactive menu/prompt is OPEN and waiting on the user:
    //   - permission prompt + folder-trust + AskUserQuestion all render a
    //     cursor-marked numbered option ("❯ 1.")
    //   - AskUserQuestion adds "Ready to submit your answers?" / "Tab to amend"
    //   - select-menu footer "Enter to select" / "Esc to cancel"
    // None of these fire the activity hook, and AskUserQuestion fires no
    // Notification hook either — that is S1, so the watchdog must SURFACE it
    // (needsInput) rather than clear it.
    //
    // Every marker is STRUCTURAL, not free-text (live failure 2026-07-18: a
    // session whose transcript merely DISPLAYED these words — printed source
    // code, a markdown "> 1." quote, prose discussing permission prompts —
    // re-armed needsInput every watchdog sweep, re-asking an already-answered
    // AskUserQuestion in the PWA):
    //   - cursor options accept only CC's real cursor glyphs, never plain ">"
    //     (markdown blockquotes);
    //   - footer hint phrases count only on a SHORT standalone line — real
    //     hints ("↑/↓ Enter to select · Esc to cancel") never exceed it,
    //     transcript prose/code lines containing the words do;
    //   - a "Do you want to …" heading counts only with a numbered option
    //     line within the next few lines, as a real dialog always renders.

    // CC's real menu cursor glyphs. Plain ">" is deliberately absent.
    private static readonly Regex CursorOptionRe =
        new(@"^\s*[❯›»‣]\s*\d{1,2}[.)]\s", RegexOptions.Compiled | RegexOptions.Multiline);

    // A numbered option line, with or without the cursor ("  2. Yes, and …").
    private static readonly Regex OptionLineRe =
        new(@"^\s*(?:[❯›»‣]\s*)?\d{1,2}[.)]\s\S", RegexOptions.Compiled);

    private static readonly Regex HintPhraseRe = new(
        @"esc to cancel|enter to select|tab to amend|ready to submit your answers",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Real footer hints are short standalone lines — the longest observed on
    // this setup is "Ready to submit your answers?  ·  Tab to amend" (46).
    // Anything longer is transcript content that happens to contain the words
    // (e.g. a session printing THIS file's regex source, 74 chars trimmed).
    // Pickers additionally carry the cursor-option signal, so a hypothetical
    // over-long real footer still would not go undetected.
    private const int HintLineMaxLen = 60;

    private static readonly Regex DialogHeadRe = new(
        @"do you want to (?:proceed|allow|create|run|make)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // How far below a "Do you want to …" heading a real dialog's first
    // numbered option can sit (blank line + option block).
    private const int DialogOptionLookahead = 5;

    // Positive confirmation of CC's clean empty composer (post /clear,
    // /compact, /exit, or crash-respawn). Used only to distinguish a
    // CONFIDENT idle from an unrecognised frame in diagnostics — the clear
    // decision itself is simply "not Working and not Blocked".
    private static readonly Regex IdleRe = new(
        @"type something|chat about this|\? for shortcuts",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A run of >=20 box-drawing horizontals (optionally space-padded) — CC's
    // full-width rule that brackets the live input box and precedes an open
    // menu. Far longer than any "----" that appears in transcript code, so it
    // reliably marks where the LIVE prompt region begins.
    private static readonly Regex DividerRe =
        new(@"^[ \t]*[─━—═-]{20,}[ \t]*$", RegexOptions.Compiled);

    private const int FallbackTailLines = 15;

    /// <summary>
    /// The LIVE prompt region only: every line AFTER the last full-width
    /// divider rule. If the visible pane has no divider (full-screen menu,
    /// truncated/odd frame), fall back to the last <see cref="FallbackTailLines"/>
    /// non-empty lines. Either way a PAST turn's "esc to interrupt" sitting in
    /// the transcript above is excluded. Public for direct unit testing.
    /// </summary>
    public static string LiveRegion(string? pane)
    {
        if (string.IsNullOrEmpty(pane)) return string.Empty;
        var lines = pane.Replace("\r", string.Empty).Split('\n');

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (DividerRe.IsMatch(lines[i]))
                return string.Join("\n", lines.Skip(i + 1));
        }

        // No divider in view — keep only the tail so stale scrollback above is
        // still dropped (best-effort; the divider path is the common one).
        var tail = new List<string>();
        for (var i = lines.Length - 1; i >= 0 && tail.Count < FallbackTailLines; i--)
            if (lines[i].Trim().Length > 0) tail.Add(lines[i]);
        tail.Reverse();
        return string.Join("\n", tail);
    }

    /// <summary>
    /// Working/Blocked require an explicit marker IN THE LIVE REGION;
    /// everything else (including a blank or unrecognised frame) is Idle.
    /// Working is checked first: while a menu is open CC replaces the
    /// "esc to interrupt" footer, so within the live region the two markers
    /// don't co-occur.
    /// </summary>
    public static PaneState Classify(string? pane)
    {
        var region = LiveRegion(pane);
        if (string.IsNullOrWhiteSpace(region)) return PaneState.Idle;
        if (WorkingRe.IsMatch(region)) return PaneState.Working;
        if (DescribeBlocked(region) is not null) return PaneState.Blocked;
        return PaneState.Idle;
    }

    /// <summary>
    /// The STRUCTURAL Blocked evidence found in an already-extracted live
    /// region, as a short marker name for diagnostics ("cursor-option",
    /// "hint:esc to cancel", "dialog+options") — or null when none. The
    /// marker never contains pane content, so it is safe to log.
    /// </summary>
    public static string? DescribeBlocked(string region)
    {
        if (CursorOptionRe.IsMatch(region)) return "cursor-option";

        var lines = region.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.Length <= HintLineMaxLen)
            {
                var hint = HintPhraseRe.Match(trimmed);
                if (hint.Success) return "hint:" + hint.Value.ToLowerInvariant();
            }

            if (DialogHeadRe.IsMatch(trimmed))
            {
                var hi = Math.Min(lines.Length, i + 1 + DialogOptionLookahead);
                for (var j = i + 1; j < hi; j++)
                    if (OptionLineRe.IsMatch(lines[j])) return "dialog+options";
            }
        }
        return null;
    }

    /// <summary>
    /// Diagnostic wrapper for callers holding a raw pane: the Blocked marker
    /// in its live region, or null. See <see cref="DescribeBlocked(string)"/>.
    /// </summary>
    public static string? BlockedMarker(string? pane)
    {
        var region = LiveRegion(pane);
        return string.IsNullOrWhiteSpace(region) || WorkingRe.IsMatch(region)
            ? null : DescribeBlocked(region);
    }

    /// <summary>
    /// True iff the LIVE REGION positively shows CC's clean idle composer (vs.
    /// an unrecognised frame that also defaults to Idle). Diagnostics only —
    /// lets the watchdog log a confident clear apart from a best-effort one.
    /// </summary>
    public static bool IsConfidentIdle(string? pane)
    {
        var region = LiveRegion(pane);
        return !string.IsNullOrWhiteSpace(region)
            && IdleRe.IsMatch(region)
            && !WorkingRe.IsMatch(region)
            && DescribeBlocked(region) is null;
    }
}
