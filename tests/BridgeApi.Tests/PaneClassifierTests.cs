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
    [InlineData("Do you want to create the file?")]
    [InlineData("  > 2) Some option here")]
    public void BlockedMarkers_AreCovered(string marker)
        => Assert.Equal(PaneClassifier.PaneState.Blocked, PaneClassifier.Classify(marker));

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
