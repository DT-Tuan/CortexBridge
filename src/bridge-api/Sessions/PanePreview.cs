using System.Text.RegularExpressions;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Extracts an EPHEMERAL "what is CC doing right now" tail from a captured
/// tmux pane, for the PWA live-view panel shown only while Processing=true.
///
/// Why this exists: CC writes a JSONL record only when a message/tool block
/// COMPLETES, so the canonical transcript necessarily lags a long generation
/// or tool run. The tmux pane is the single live source. This returns just the
/// last few transcript lines (the streaming text / current tool box / spinner
/// verb) with CC's input-box chrome removed — a peek, NOT a transcript source:
/// it is a 50-row, 200-col snapshot; long output scrolls off; the JSONL record
/// replaces it the moment it lands. Pure + static = unit-testable, no tmux.
/// </summary>
public static class PanePreview
{
    public const int DefaultMaxLines = 10;

    // Same full-width rule as PaneClassifier: CC brackets the input box with
    // a >=20-glyph horizontal. Kept local so this module stays self-contained.
    private static readonly Regex DividerRe =
        new(@"^[ \t]*[─━—═-]{20,}[ \t]*$", RegexOptions.Compiled);

    // A lone input prompt line ("❯", "❯ some half-typed text") that sits
    // between the input-box dividers — not live work, drop it.
    private static readonly Regex InputLineRe =
        new(@"^\s*[❯>]\s.*$|^\s*[❯>]\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The last <paramref name="maxLines"/> non-empty transcript lines ABOVE
    /// CC's bottom input-box block (dividers + ❯ line + footer stripped), each
    /// trimmed of trailing box-padding. Empty array when nothing meaningful is
    /// visible (PWA then shows just the spinner).
    /// </summary>
    public static string[] Tail(string? pane, int maxLines = DefaultMaxLines)
    {
        if (string.IsNullOrWhiteSpace(pane)) return [];
        var lines = pane.Replace("\r", string.Empty).Split('\n');

        // Find the TOP edge of the bottom input-box block. Scanning up from the
        // end, the first divider is the box's BOTTOM rule; a second divider a
        // few lines further up is its TOP rule (the ❯ line sits between them).
        // Cut at the topmost divider of that block so the ❯ line + bottom rule
        // + footer below are all excluded; everything above is live transcript.
        var cut = lines.Length;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!DividerRe.IsMatch(lines[i])) continue;
            cut = i;
            // Absorb an adjacent upper divider of the same box (≤3 lines up,
            // only ❯/blank between) so the whole box is removed.
            for (var j = i - 1; j >= 0 && j >= i - 3; j--)
            {
                if (DividerRe.IsMatch(lines[j])) { cut = j; i = j; break; }
                if (lines[j].Trim().Length != 0 && !InputLineRe.IsMatch(lines[j])) break;
            }
            break;
        }
        // No divider in view (odd/truncated frame): drop only the last line
        // (the footer) so the live tail isn't the footer but the spinner verb
        // / latest work above it is still kept.
        if (cut == lines.Length) cut = Math.Max(0, lines.Length - 1);

        var outp = new List<string>();
        for (var i = cut - 1; i >= 0 && outp.Count < maxLines; i--)
        {
            if (DividerRe.IsMatch(lines[i]) || InputLineRe.IsMatch(lines[i])) continue;
            var c = Clean(lines[i]);
            if (c.Length == 0) continue;
            outp.Add(c);
        }
        outp.Reverse();
        return outp.ToArray();
    }

    private static string Clean(string s)
    {
        s = s.TrimEnd('│', '─', '╌', '╎', ' ', '\t');
        s = s.Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }
}
