using CortexBridge.Api.Endpoints;

namespace CortexBridge.Api.Tests;

/// <summary>
/// Guards PromptEndpoint.ExtractAskContext — the recovery of the assistant's
/// analysis prose that sits above a live AskUserQuestion picker. CC bundles that
/// prose and the AskUserQuestion tool_use into one assistant message and does
/// not persist it to JSONL until the question is answered, so the tmux pane
/// scrollback is the only live source of the context the questions refer to.
/// Samples are trimmed-but-faithful snapshots of CC's visible screen.
/// </summary>
public class AskContextTests
{
    // A realistic pane: assistant prose bullet, the AskUserQuestion tool-call
    // bullet + its result line, the full-width divider, then the picker box.
    private const string AskPane = """
        ⏺ Ground truth from the code: the analysis text and the AskUserQuestion
          share one msgId, so CC withholds the whole message until answered.
          Two options matter here: scope tier A vs B.

        ⏺ AskUserQuestion(3 questions)
          ⎿  (waiting for response)

        ────────────────────────────────────────────────────────────
          (1) Scope tier?
          ❯ 1. Tier A
            2. Tier B

          Ready to submit your answers?  ·  Tab to amend
        """;

    [Fact]
    public void RecoversAnalysisProse()
    {
        var ctx = PromptEndpoint.ExtractAskContext(AskPane);
        Assert.NotNull(ctx);
        Assert.Contains("Ground truth from the code", ctx);
        Assert.Contains("scope tier A vs B", ctx);
    }

    [Fact]
    public void ExcludesPickerAndToolCall()
    {
        var ctx = PromptEndpoint.ExtractAskContext(AskPane)!;
        // The tool-call bullet, the picker options, and the footer are NOT prose.
        Assert.DoesNotContain("AskUserQuestion(", ctx);
        Assert.DoesNotContain("Tier A", ctx);
        Assert.DoesNotContain("Ready to submit", ctx);
        Assert.DoesNotContain("waiting for response", ctx);
    }

    [Fact]
    public void StripsBulletAndGutterGlyphs()
    {
        var ctx = PromptEndpoint.ExtractAskContext(AskPane)!;
        Assert.DoesNotContain("⏺", ctx);
        Assert.DoesNotContain("⎿", ctx);
        // The leading 2-space continuation gutter is trimmed.
        Assert.StartsWith("Ground truth", ctx);
    }

    [Fact]
    public void NoProseBullet_ReturnsNull()
    {
        // A picker with no preceding assistant prose (e.g. a bare permission
        // prompt) yields no context rather than scraping unrelated lines.
        var pane = """
            ────────────────────────────────────────────────────────────
              Do you want to proceed?
              ❯ 1. Yes
                2. No
            """;
        Assert.Null(PromptEndpoint.ExtractAskContext(pane));
    }

    [Fact]
    public void EmptyOrNull_ReturnsNull()
    {
        Assert.Null(PromptEndpoint.ExtractAskContext(""));
        Assert.Null(PromptEndpoint.ExtractAskContext("   \n  \n"));
    }
}
