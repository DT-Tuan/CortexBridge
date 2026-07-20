using CortexBridge.Api.Endpoints;
using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// Guards PromptEndpoint.ParseVisible — the numbered-option parser — against
/// transcript-content spoofing.
///
/// <para>ADR-026 hardened the SIBLING parser (ParseAskTabs) with live-region
/// scoping and left this one scanning the whole pane. Live failure 2026-07-20:
/// a session running a <c>for</c> loop printed numbered lines into the
/// transcript, ParseVisible adopted them as the option block, and the PWA
/// rendered a phantom picker — taps did nothing (the digit reached a session
/// with no menu open) and the card cleared only on cancel.</para>
///
/// <para>Every sample below builds a pane the way tmux hands one over: a long
/// transcript, CC's full-width divider, then the live input box. Dirty
/// scrollback is the POINT — a clean pane gives false confidence.</para>
/// </summary>
public class PromptEndpointParseVisibleTests
{
    private const string Divider = "────────────────────────────────────────────────────────────";

    /// <summary>Transcript noise that must never be mistaken for a live menu.</summary>
    private static string[] ForLoopScrollback() =>
    [
        "  $ for f in *.cs; do echo \"$f\"; done",
        "  Processing files:",
        "  1. Program.cs",
        "  2. PromptEndpoint.cs",
        "  3. PaneClassifier.cs",
        "  4. TmuxClient.cs",
        "  done.",
    ];

    // ---- MUST NOT be adopted as options ----

    [Fact]
    public void ForLoopOutput_AboveDivider_YieldsNoOptions()
    {
        var pane = string.Join("\n", [
            .. ForLoopScrollback(),
            Divider,
            "  > ",                                  // empty composer — nothing pending
            "  ? for shortcuts",
        ]);

        var (_, options, _, _) = PromptEndpoint.ParseVisible(pane);

        Assert.Empty(options);
    }

    [Fact]
    public void MarkdownNumberedList_InTranscript_YieldsNoOptions()
    {
        var pane = string.Join("\n", [
            "  Here is the plan:",
            "  1. Add the live-region gate",
            "  2. Write the regression tests",
            "  3. Commit on a branch",
            Divider,
            "  > ",
        ]);

        var (_, options, _, _) = PromptEndpoint.ParseVisible(pane);

        Assert.Empty(options);
    }

    /// <summary>
    /// The premise behind the Handler's Working gate: while a turn runs, CC
    /// keeps the "esc to interrupt" footer, so the pane classifies Working and
    /// the endpoint must surface no card at all — regardless of what the
    /// transcript above happens to look like.
    /// </summary>
    [Fact]
    public void RunningTurn_WithNumberedOutput_ClassifiesWorking()
    {
        var pane = string.Join("\n", [
            .. ForLoopScrollback(),
            Divider,
            "  ✻ Running… (12s · esc to interrupt)",
        ]);

        Assert.Equal(PaneClassifier.PaneState.Working, PaneClassifier.Classify(pane));
    }

    // ---- MUST still be parsed (regression guards) ----
    // Narrowing the scan must not cost the two menus the PWA exists to answer.

    [Fact]
    public void RealPermissionPrompt_BelowDivider_IsStillParsed()
    {
        var pane = string.Join("\n", [
            .. ForLoopScrollback(),                  // dirty scrollback, deliberately
            Divider,
            "  Do you want to proceed?",
            "  ❯ 1. Yes",
            "    2. Yes, and don't ask again",
            "    3. No, and tell Claude what to do differently (esc)",
        ]);

        var (question, options, _, _) = PromptEndpoint.ParseVisible(pane);

        Assert.Equal(3, options.Count);
        Assert.Equal("Yes", options[0].Label);
        Assert.Equal(1, options[0].Num);
        Assert.Equal("Do you want to proceed?", question);
    }

    [Fact]
    public void RealAskPicker_BelowDivider_IsStillParsed()
    {
        var pane = string.Join("\n", [
            "  earlier assistant prose that mentions 1. and 2. inline",
            Divider,
            "  Which approach should we take?",
            "  ❯ 1. Spool + host agent",
            "    2. Mount the vault into the container",
        ]);

        var (question, options, _, _) = PromptEndpoint.ParseVisible(pane);

        Assert.Equal(2, options.Count);
        Assert.Equal("Spool + host agent", options[0].Label);
        Assert.Equal("Which approach should we take?", question);
    }

    /// <summary>
    /// No divider in view (short pane / fresh window) — LiveRegion falls back to
    /// the tail, so a menu at the bottom must still parse.
    /// </summary>
    [Fact]
    public void RealPrompt_NoDividerInView_IsStillParsed()
    {
        var pane = string.Join("\n", [
            "  Do you want to proceed?",
            "  ❯ 1. Yes",
            "    2. No",
        ]);

        var (_, options, _, _) = PromptEndpoint.ParseVisible(pane);

        Assert.Equal(2, options.Count);
    }

    /// <summary>
    /// Boundary: a multiSelect picker renders checkbox glyphs in the labels.
    /// Scoping must not disturb the multi flag the PWA uses to pick the card.
    /// </summary>
    [Fact]
    public void MultiSelectPicker_SetsMultiFlag()
    {
        var pane = string.Join("\n", [
            .. ForLoopScrollback(),
            Divider,
            "  Pick the checks to run",
            "  ❯ 1. [ ] build",
            "    2. [x] tests",
        ]);

        var (_, options, _, multi) = PromptEndpoint.ParseVisible(pane);

        Assert.Equal(2, options.Count);
        Assert.True(multi);
    }

    [Fact]
    public void EmptyPane_YieldsNoOptions()
    {
        var (question, options, _, _) = PromptEndpoint.ParseVisible(string.Empty);

        Assert.Empty(options);
        Assert.Null(question);
    }
}
