using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// PanePreview.Tail feeds the ephemeral PWA live-view panel. It must show the
/// live transcript tail (streaming text / current tool box / spinner verb) and
/// strip CC's input-box chrome (dividers, the ❯ line, the footer).
/// </summary>
public class PanePreviewTests
{
    private static readonly string Div = new('─', 80);

    [Fact]
    public void RunningTurn_ReturnsTranscriptTail_NotInputChrome()
    {
        var pane =
            "● Read(big-file.cs)\n"
            + "● Bash(npm test)\n"
            + "  ⎿  12 passed, 0 failed\n"
            + "✶ Working… (esc to interrupt)\n"
            + Div + "\n"
            + "❯ \n"
            + Div + "\n"
            + "  esc to interrupt            ✗ Auto-update failed";
        var tail = PanePreview.Tail(pane);

        Assert.Contains("● Bash(npm test)", tail);
        Assert.Contains("✶ Working… (esc to interrupt)", tail);            // live verb kept
        Assert.DoesNotContain(tail, l => l.Contains("? for shortcuts"));
        Assert.DoesNotContain(tail, l => l.StartsWith("❯"));               // input line dropped
        Assert.DoesNotContain(tail, l => l.Contains("Auto-update failed")); // footer dropped
        Assert.DoesNotContain(tail, l => l.All(c => c is '─' or ' '));     // no divider rules
    }

    [Fact]
    public void RespectsMaxLines_ReturnsTheLastN()
    {
        var body = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}"));
        var pane = body + "\n" + Div + "\n❯ \n" + Div + "\n  ? for shortcuts";
        var tail = PanePreview.Tail(pane, 10);

        Assert.Equal(10, tail.Length);
        Assert.Equal("line 21", tail[0]);
        Assert.Equal("line 30", tail[^1]);
    }

    [Fact]
    public void IdlePane_ChromeOnly_ReturnsEmpty()
    {
        // Just the input box + footer, no transcript above → nothing to preview.
        var pane = Div + "\n❯ \n" + Div + "\n  ? for shortcuts        ✗ Auto-update failed";
        Assert.Empty(PanePreview.Tail(pane));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData(null)]
    public void BlankPane_ReturnsEmpty(string? pane)
        => Assert.Empty(PanePreview.Tail(pane));

    [Fact]
    public void NoDivider_DropsFooterTail_KeepsWork()
    {
        // Odd frame with no input box rendered: still must not surface the
        // last footer-ish line as "work".
        var pane =
            "● Editing config.json\n"
            + "  ⎿  applied hunk 2/3\n"
            + "✻ Churning…\n"
            + "  esc to interrupt";
        var tail = PanePreview.Tail(pane);

        Assert.Contains("● Editing config.json", tail);
        Assert.Contains("✻ Churning…", tail);
        Assert.DoesNotContain(tail, l => l.Contains("esc to interrupt")); // last 2 lines dropped
    }

    [Fact]
    public void StripsTrailingBoxPadding()
    {
        var pane =
            "● Doing the thing                                   │\n"
            + Div + "\n❯ \n" + Div + "\n  esc to interrupt";
        var tail = PanePreview.Tail(pane);
        Assert.Equal("● Doing the thing", tail.Single());
    }

    // ---- PickerView: the OPPOSITE of Tail — it must KEEP the picker chrome
    // (cursor/checkbox/tab-bar/footer) that Tail strips, so the PWA raw-TUI
    // remote can show CC's real picker as the user drives it (2026-07-18). ----

    private static readonly string PickerPane =
        "● Finished the analysis.\n"
        + "  ⎿  4 skills available\n"
        + Div + "\n"
        + "←  ☒ Test  ✔ Submit  →\n\n"
        + "Chọn các mục test?\n\n"
        + "❯ 1. [✔] Alpha\n"
        + "  2. [ ] Beta\n"
        + "  3. [ ] Gamma\n"
        + Div + "\n"
        + "Enter to select · ↑/↓ to navigate · Esc to cancel";

    [Fact]
    public void PickerView_KeepsCursorCheckboxAndTabBar()
    {
        var view = string.Join("\n", PanePreview.PickerView(PickerPane));
        Assert.Contains("❯ 1. [✔] Alpha", view);   // cursor + checked box
        Assert.Contains("2. [ ] Beta", view);       // unchecked box
        Assert.Contains("☒ Test  ✔ Submit", view);  // tab bar
        Assert.Contains("Enter to select", view);   // footer hint
    }

    [Fact]
    public void PickerView_TailWouldHaveStrippedThePicker()
    {
        // Guard the reason PickerView exists: Tail cuts at the bottom box's top
        // divider, so the picker options/tab/footer are GONE from Tail's output.
        var tail = string.Join("\n", PanePreview.Tail(PickerPane));
        Assert.DoesNotContain("[✔] Alpha", tail);
        Assert.DoesNotContain("Enter to select", tail);
    }

    [Fact]
    public void PickerView_DropsBlankPaddingAndCaps()
    {
        var view = PanePreview.PickerView("a\n\n   \nb\n\n\nc");
        Assert.Equal(new[] { "a", "b", "c" }, view);
        // maxLines bound honoured (tail-most kept).
        var many = string.Join("\n", System.Linq.Enumerable.Range(1, 40).Select(i => "line" + i));
        Assert.Equal(24, PanePreview.PickerView(many).Length);
        Assert.Equal("line40", PanePreview.PickerView(many)[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void PickerView_EmptyOnBlank(string? pane)
        => Assert.Empty(PanePreview.PickerView(pane));
}
