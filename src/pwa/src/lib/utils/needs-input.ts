/**
 * Decide whether a foreground REST reconcile should re-arm the local
 * `needsInput` signal on the session detail page.
 *
 * Background: `needsInput` is normally driven by an SSE status transition. iOS
 * suspends the EventSource in the background, so a prompt that appears while
 * backgrounded can arrive as a single frame the suspend→reconnect drops, leaving
 * the banner/panel un-rendered even though the server holds needsInput=true. On
 * foreground we re-read the authoritative REST status and re-arm from it.
 *
 * The reconcile is deliberately ADDITIVE — it only ever answers "set true":
 *  - never clears (clearing stays with SSE/optimistic, so a REST read can't race
 *    a just-sent reply into a false re-flip),
 *  - never re-flips within the stale-reply guard window (a reply the user just
 *    sent optimistically cleared needsInput; a lagging REST read must not undo that),
 *  - never fires when the banner is already showing.
 */
export function shouldReArmNeedsInput(params: {
	/** Local needsInput right now. */
	current: boolean;
	/** Authoritative needsInput from the REST session status. */
	restNeedsInput: boolean;
	/** ms since the user's last reply (Date.now() - lastReplyAt). */
	msSinceReply: number;
	/** Stale-reply guard window in ms. */
	guardMs: number;
}): boolean {
	const { current, restNeedsInput, msSinceReply, guardMs } = params;
	if (!restNeedsInput) return false;
	if (current) return false;
	if (msSinceReply < guardMs) return false;
	return true;
}
