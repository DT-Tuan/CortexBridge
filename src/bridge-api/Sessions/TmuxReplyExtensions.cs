using CortexBridge.Api.Tmux;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Safe paste pipeline for every PWA→tmux send path. Lives in the Sessions
/// namespace because it depends on PaneClassifier; TmuxClient stays a thin
/// argv-only process wrapper.
///
/// Two orthogonal gates every paste path must honour
/// ([[bridge-bug-bracketed-paste-into-picker]] — live caught 2026-06-07 on project-epsilon):
///
///   gate 1: SessionStateRegistry.Processing == false  (the caller's job)
///   gate 2: pane is NOT in PaneClassifier.PaneState.Blocked  (this helper)
///
/// When CC ends a turn by opening a permission picker / AskUserQuestion,
/// Processing flips false (releasing the caller's gate) but the pane is
/// Blocked. A bracketed paste straight into the picker is eaten as "cancel"
/// → "[Request interrupted by user for tool use]" → the user's message is
/// SILENTLY LOST (no error returns; the failure is inside CC, not tmux).
///
/// History: ReplyEndpoints.PostReply had this dance from day one. SubArc 2
/// (queue, commit df044cc) introduced 2 more paste sites without it →
/// silent regression on project-epsilon → fix landed in commit 8f0fc7e. Option C
/// (this file) extracts the dance so every paste path shares it and a 4th
/// callsite can't "forget" the same gate. Adding a NEW endpoint or
/// BackgroundService that pastes? Call SendReplyWithPickerDismissAsync,
/// NOT TmuxClient.SendReplyAsync.
/// </summary>
public static class TmuxReplyExtensions
{
    /// <summary>
    /// Capture pane → if Blocked, send Escape + wait 250 ms to dismiss the
    /// picker → SendReplyAsync. Best-effort: a CapturePane / SendKey hiccup
    /// falls through to the paste anyway (PaneClassifier path is the safety
    /// belt; a flaky capture shouldn't strand the caller forever).
    /// Returns true iff the picker dismiss actually fired (caller may log).
    /// </summary>
    public static async Task<bool> SendReplyWithPickerDismissAsync(
        this TmuxClient tmux, string windowName, string text, CancellationToken ct)
    {
        var dismissed = false;
        try
        {
            var pane = await tmux.CapturePaneAsync(windowName, ct);
            if (PaneClassifier.Classify(pane) == PaneClassifier.PaneState.Blocked)
            {
                await tmux.SendKeyAsync(windowName, "Escape", ct);
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                dismissed = true;
            }
        }
        catch (TmuxException) { /* couldn't read/Esc — proceed with paste anyway */ }

        await tmux.SendReplyAsync(windowName, text, ct);
        return dismissed;
    }
}
