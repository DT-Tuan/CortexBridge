using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// Guards the heart of the "composer unlocks early" fix: the watchdog must NOT
/// treat a running turn (S2) or an open menu (S1) as a dead latch. Samples are
/// trimmed-but-faithful snapshots of CC's visible tmux screen.
/// </summary>
public class PaneClassifierTests
{
    // ---- Working: a turn is actively running (S2 — no per-tool hooks) ----

    [Fact]
    public void LongToolCall_IsWorking()
    {
        var pane = """
            ● Bash(./scripts/migrate.sh --all)
              ⎿ Running…

            ✶ Crunching… (12s · esc to interrupt)
            """;
        Assert.Equal(PaneClassifier.PaneState.Working, PaneClassifier.Classify(pane));
    }

    [Fact]
    public void LongThink_IsWorking()
    {
        var pane = "✻ Thinking…\n\n  (esc to interrupt · ctrl+t to show todos)";
        Assert.Equal(PaneClassifier.PaneState.Working, PaneClassifier.Classify(pane));
    }

    // ---- Blocked: an interactive prompt is open (S1 — no hook at all) ----

    [Fact]
    public void AskUserQuestion_ReadyToSubmit_IsBlocked()
    {
        var pane = """
            Which approach do you prefer?

              1. Refactor in place
              2. Rewrite the module

            Ready to submit your answers?  ·  Tab to amend
            """;
        Assert.Equal(PaneClassifier.PaneState.Blocked, PaneClassifier.Classify(pane));
    }

    [Fact]
    public void PermissionPrompt_CursorOption_IsBlocked()
    {
        var pane = """
            Do you want to proceed?

            ❯ 1. Yes
              2. Yes, and don't ask again for bash commands
              3. No, tell Claude what to do differently
            """;
        Assert.Equal(PaneClassifier.PaneState.Blocked, PaneClassifier.Classify(pane));
    }

    [Fact]
    public void SelectMenuFooter_IsBlocked()
    {
        var pane = "Select a file\n\n  config.json\n  README.md\n\n↑/↓ Enter to select · Esc to cancel";
        Assert.Equal(PaneClassifier.PaneState.Blocked, PaneClassifier.Classify(pane));
    }

    [Theory]
    [InlineData("ready to submit your answers")] // case-insensitive
    [InlineData("Tab to amend")]
    [InlineData("Enter to select")]
    [InlineData("Esc to cancel")]
    [InlineData("  › 2) Some option here")]
    public void BlockedMarkers_AreCovered(string marker)
        => Assert.Equal(PaneClassifier.PaneState.Blocked, PaneClassifier.Classify(marker));

    [Fact]
    public void DialogHeading_WithPlainNumberedOptions_IsBlocked()
    {
        // Folder-trust style dialog whose options render without the cursor
        // glyph on the visible frame — the heading + option-line pair is the
        // structural evidence.
        var pane = """
            Do you want to create the file?

              1. Yes
              2. No
            """;
        Assert.Equal(PaneClassifier.PaneState.Blocked, PaneClassifier.Classify(pane));
    }

    // ---- Structural hardening (live failure 2026-07-18): a session whose
    // transcript merely DISPLAYS menu-ish text re-armed needsInput every
    // watchdog sweep, re-asking an already-answered AskUserQuestion in the
    // PWA. Ordinary content must never classify Blocked. ----

    [Fact]
    public void MarkdownQuoteNumberedItem_IsIdle()
    {
        // "> 1. …" is a markdown blockquote, not a menu cursor — plain ">"
        // is deliberately not accepted as a cursor glyph any more.
        Assert.Equal(PaneClassifier.PaneState.Idle,
            PaneClassifier.Classify("> 1. Có nên tách module này không?\n> 2. Hay giữ nguyên?"));
    }

    [Fact]
    public void HintWordsInsideLongCodeLine_IsIdle()
    {
        // A session printing THIS classifier's own source (code review, docs)
        // shows the phrases inside long code lines — never a real footer hint.
        var pane =
            "        @\"esc to cancel|enter to select|tab to amend|ready to submit your answers\"\n"
            + "        + @\"|do you want to (?:proceed|allow|create|run|make)\",";
        Assert.Equal(PaneClassifier.PaneState.Idle, PaneClassifier.Classify(pane));
    }

    [Fact]
    public void DialogHeadingInProse_WithoutOptions_IsIdle()
    {
        // A report SAYING "Do you want to proceed" with no option lines below.
        var pane = "Tóm lại: khi CC hỏi Do you want to proceed thì bridge sẽ push về PWA.\n"
            + "Chi tiết xem mục 3 phía trên.";
        Assert.Equal(PaneClassifier.PaneState.Idle, PaneClassifier.Classify(pane));
    }

    [Fact]
    public void NumberedReportWithToolOutputBars_IdleComposer_IsIdle()
    {
        // The exact 2026-07-18 shape: grok-agent tool output prints its own
        // 60-char '─' bars (spoof dividers) and the assistant's report is a
        // numbered list; the real composer + idle footer sit at the bottom.
        var bar = new string('─', 60);
        var pane =
            bar + "\n"
            + "grok-agent · model grok-4.5-build · turns 3 · stop EndTurn · 38s\n"
            + bar + "\n"
            + "Kết quả:\n"
            + "1. Cài đặt xong wrapper\n"
            + "2. Hook routing đã đăng ký\n"
            + "3. Smoke-test PASS\n"
            + Div + "\n"
            + "❯ \n"
            + Div + "\n"
            + "  ? for shortcuts";
        Assert.Equal(PaneClassifier.PaneState.Idle, PaneClassifier.Classify(pane));
        Assert.True(PaneClassifier.IsConfidentIdle(pane));
    }

    [Fact]
    public void SpoofDividerAsLastDivider_NumberedProseBelow_IsIdle()
    {
        // Worst case: a tool-output bar IS the last divider in view, so the
        // "live region" is transcript prose with numbered lines and hint words
        // inside a long sentence. Still must not classify Blocked.
        var bar = new string('─', 60);
        var pane =
            "● Bash(grok-agent -q ...)\n"
            + bar + "\n"
            + "1. Finder góc A đã chạy xong và trả về 3 candidate cần verify thêm\n"
            + "2. Nhớ kiểm tra lại vì grok in preamble lẫn trước JSON, esc to cancel không liên quan gì ở đây\n";
        Assert.Equal(PaneClassifier.PaneState.Idle, PaneClassifier.Classify(pane));
    }

    [Fact]
    public void BlockedMarker_ReportsStructuralReason()
    {
        Assert.Equal("cursor-option", PaneClassifier.BlockedMarker("❯ 1. Yes\n  2. No"));
        Assert.Equal("hint:esc to cancel", PaneClassifier.BlockedMarker("  Esc to cancel"));
        Assert.Equal("dialog+options",
            PaneClassifier.BlockedMarker("Do you want to proceed?\n\n  1. Yes\n  2. No"));
        Assert.Null(PaneClassifier.BlockedMarker("> 1. markdown quote"));
        Assert.Null(PaneClassifier.BlockedMarker("✶ Working… (esc to interrupt)"));
    }

    // ---- Idle: clean prompt or unrecognised → genuine dead latch ----

    [Theory]
    [InlineData("│ > Type something                                            │")]
    [InlineData("Chat about this session")]
    [InlineData("  ? for shortcuts")]
    public void CleanComposer_IsIdle(string pane)
        => Assert.Equal(PaneClassifier.PaneState.Idle, PaneClassifier.Classify(pane));

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData(null)]
    public void BlankOrUnknown_IsIdle(string? pane)
        => Assert.Equal(PaneClassifier.PaneState.Idle, PaneClassifier.Classify(pane));

    // ---- Precedence + diagnostics ----

    [Fact]
    public void Working_WinsOverIncidentalDigits()
    {
        // A running turn whose output happens to contain a "1." line must still
        // classify Working — the live "esc to interrupt" footer is decisive.
        var pane = "● Read(plan.md)\n  1. step one\n  2. step two\n\n✶ Working… (esc to interrupt)";
        Assert.Equal(PaneClassifier.PaneState.Working, PaneClassifier.Classify(pane));
    }

    [Fact]
    public void IsConfidentIdle_TrueOnlyForRecognisedCleanPrompt()
    {
        Assert.True(PaneClassifier.IsConfidentIdle("│ > Type something │"));
        Assert.False(PaneClassifier.IsConfidentIdle("garbled unrecognised frame"));
        Assert.False(PaneClassifier.IsConfidentIdle("✶ working (esc to interrupt)"));
        Assert.False(PaneClassifier.IsConfidentIdle(null));
    }

    // ---- Stale-scrollback defect (observed LIVE): CC renders the transcript
    // inside the visible pane, so a finished turn's "esc to interrupt" sits
    // ABOVE the live prompt. Whole-pane matching mis-fired Working while CC
    // was actually Blocked/Idle → Processing latched → composer gated → "treo".
    // Classification must use the LIVE REGION (after the last divider) only.

    // CC's full-width input/menu rule (real char: U+2500 BOX DRAWINGS LIGHT
    // HORIZONTAL), spanning the pane width.
    private static readonly string Div = new('─', 80);

    [Fact]
    public void AskUserQuestion_WithStaleEscToInterruptAboveDivider_IsBlocked()
    {
        var pane =
            "● Earlier step of the turn\n"
            + "✶ Crunched (8s · esc to interrupt)\n"   // STALE — finished turn
            + "● Finished the analysis.\n"
            + Div + "\n"
            + "Which approach do you prefer?\n\n"
            + "  1. Refactor in place\n"
            + "❯ 2. Rewrite the module\n\n"
            + "Ready to submit your answers?  ·  Tab to amend";
        Assert.Equal(PaneClassifier.PaneState.Blocked, PaneClassifier.Classify(pane));
    }

    [Fact]
    public void PermissionPrompt_WithStaleEscToInterruptAboveDivider_IsBlocked()
    {
        var pane =
            "● Bash(./scripts/migrate.sh --all)\n"
            + "  ⎿  … long output …\n"
            + "✶ Finalizing… (esc to interrupt)\n"      // STALE — finished turn
            + Div + "\n"
            + "Do you want to proceed?\n\n"
            + "❯ 1. Yes\n"
            + "  2. Yes, and don't ask again for bash commands\n"
            + "  3. No, tell Claude what to do differently\n\n"
            + "  Esc to cancel";
        Assert.Equal(PaneClassifier.PaneState.Blocked, PaneClassifier.Classify(pane));
    }

    [Fact]
    public void IdlePrompt_WithStaleEscToInterruptAboveDivider_IsIdle()
    {
        // The exact "treo" root: a finished long turn left "esc to interrupt"
        // on screen; the live prompt is idle. Must NOT stay Working.
        var pane =
            "✶ Did a long thing… (esc to interrupt)\n"  // STALE
            + "● All done. Stopping here.\n"
            + Div + "\n"
            + "❯ \n"
            + Div + "\n"
            + "  ? for shortcuts                 ✗ Auto-update failed";
        Assert.Equal(PaneClassifier.PaneState.Idle, PaneClassifier.Classify(pane));
        Assert.True(PaneClassifier.IsConfidentIdle(pane));
    }

    [Fact]
    public void RunningTurn_LiveFooterBelowLastDivider_IsWorking()
    {
        // Genuine running turn: the live "esc to interrupt" is the footer
        // BELOW the last divider — must still be detected as Working.
        var pane =
            "● Read(big-file.cs)\n"
            + "● Editing…\n"
            + Div + "\n"
            + "❯ \n"
            + Div + "\n"
            + "  esc to interrupt                ✗ Auto-update failed";
        Assert.Equal(PaneClassifier.PaneState.Working, PaneClassifier.Classify(pane));
    }

    [Fact]
    public void LiveRegion_ReturnsOnlyTextAfterLastDivider()
    {
        var pane = "STALE esc to interrupt line\n" + Div + "\nLIVE ? for shortcuts";
        var region = PaneClassifier.LiveRegion(pane);
        Assert.DoesNotContain("STALE", region);
        Assert.DoesNotContain("esc to interrupt", region);
        Assert.Contains("? for shortcuts", region);
    }

    [Fact]
    public void NoDivider_FallsBackToTail_DropsStaleHead()
    {
        // No divider in view → fallback keeps only the last 15 non-empty
        // lines, so a stale "esc to interrupt" far up is still dropped.
        var sb = new System.Text.StringBuilder();
        sb.Append("✶ old turn (esc to interrupt)\n");      // STALE, line 1
        for (var i = 0; i < 20; i++) sb.Append($"transcript filler line {i}\n");
        sb.Append("Do you want to proceed?\n");
        sb.Append("❯ 1. Yes\n  2. No\n");
        sb.Append("Esc to cancel");
        Assert.Equal(PaneClassifier.PaneState.Blocked, PaneClassifier.Classify(sb.ToString()));
    }
}
