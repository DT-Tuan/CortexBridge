using CortexBridge.Api.Endpoints;

namespace CortexBridge.Api.Tests;

/// <summary>
/// Guards PromptEndpoint.ParseAskTabs against transcript-content spoofing.
/// isAsk must require real AskUserQuestion tab-bar chrome (shape + live-region
/// gates), never a bare "…Submit…" substring in prose/code. Samples use
/// realistic ≥2-space tab separators for true bars.
/// </summary>
public class PromptEndpointTests
{
    // ---- MUST be isAsk == false (spoof / no real bar) ----

    [Fact]
    public void Spoof_ProseWithSubmitMidSentence_IsNotAsk()
    {
        var lines = new[]
        {
            "  ... vocabulary (e.g. ☒ Test ✔ Submit, or this very code review), isAsk",
        };
        var (isAsk, sections, _) = PromptEndpoint.ParseAskTabs(lines);
        Assert.False(isAsk);
        Assert.Empty(sections);
    }

    [Fact]
    public void Spoof_MarkdownTableCellWithSubmit_IsNotAsk()
    {
        var lines = new[]
        {
            "| `Enter` #2 (on Submit tab) | actually submits — checkbox ☑ |",
        };
        var (isAsk, _, _) = PromptEndpoint.ParseAskTabs(lines);
        Assert.False(isAsk);
    }

    [Fact]
    public void Spoof_ProseAfterSubmit_IsNotAsk()
    {
        var lines = new[]
        {
            "the tab bar is ☒ Test ✔ Submit at the bottom",
        };
        var (isAsk, _, _) = PromptEndpoint.ParseAskTabs(lines);
        Assert.False(isAsk);
    }

    [Fact]
    public void PermissionPrompt_NoSubmitTab_IsNotAsk()
    {
        var lines = new[]
        {
            "Do you want to proceed?",
            "❯ 1. Yes",
            "  2. Yes, and don't ask again",
            "  3. No",
        };
        var (isAsk, sections, answered) = PromptEndpoint.ParseAskTabs(lines);
        Assert.False(isAsk);
        Assert.Empty(sections);
        Assert.Equal(0, answered);
    }

    [Fact]
    public void SubmitWithoutCheckboxGlyph_IsNotAsk()
    {
        var lines = new[]
        {
            "Ready to Submit your answers",
            "  Submit",
        };
        var (isAsk, _, _) = PromptEndpoint.ParseAskTabs(lines);
        Assert.False(isAsk);
    }

    // ---- MUST be isAsk == true (real bars) ----

    [Fact]
    public void RealSingleQuestionBar_IsAsk()
    {
        var lines = new[]
        {
            "❯ 1. A",
            "  2. B",
            "☐ Câu hỏi  ✔ Submit",
        };
        var (isAsk, sections, answered) = PromptEndpoint.ParseAskTabs(lines);
        Assert.True(isAsk);
        Assert.Equal(new[] { "Câu hỏi" }, sections);
        Assert.Equal(0, answered);
    }

    [Fact]
    public void RealMultiQuestionBar_TracksAnswered()
    {
        var lines = new[]
        {
            "☑ Q1  ☐ Q2  ✔ Submit",
        };
        var (isAsk, sections, answered) = PromptEndpoint.ParseAskTabs(lines);
        Assert.True(isAsk);
        Assert.Equal(new[] { "Q1", "Q2" }, sections);
        Assert.Equal(1, answered);
    }

    [Fact]
    public void SpoofAboveDivider_PermissionPromptBelow_IsNotAsk()
    {
        // The exact live incident: a session reviewing AskUserQuestion code shows a
        // prose line with "…✔ Submit…" in scrollback, while a real PERMISSION prompt
        // (no tab bar) sits at the bottom. isAsk must stay false so the prompt keeps
        // its one-tap /choice path (B1 scopes below the divider → permission region
        // has no Submit tab; B2 rejects the prose line's trailing text).
        var lines = new[]
        {
            "prose (e.g. ☒ Test ✔ Submit, or this very code review), isAsk",
            "──────────────────────────────",
            "Do you want to proceed?",
            "❯ 1. Yes",
            "  2. No",
        };
        var (isAsk, sections, _) = PromptEndpoint.ParseAskTabs(lines);
        Assert.False(isAsk);
        Assert.Empty(sections);
    }

    [Fact]
    public void RealBarUnderDivider_IgnoresSpoofAbove()
    {
        // B1 scopes below the last divider; B2 rejects the spoof-shaped line
        // above it. The real bar at the bottom must still win.
        var lines = new[]
        {
            "prose ☒ Test ✔ Submit, or whatever",
            "──────────────────────────────",
            "❯ 1. A",
            "  2. B",
            "☐ Ưu tiên  ✔ Submit",
        };
        var (isAsk, sections, answered) = PromptEndpoint.ParseAskTabs(lines);
        Assert.True(isAsk);
        Assert.Equal(new[] { "Ưu tiên" }, sections);
        Assert.Equal(0, answered);
    }
}
