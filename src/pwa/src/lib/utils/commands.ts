// CC writes TWO separate "user" records to JSONL for each slash command in its TUI:
//   1) caveat header — content = "<local-command-caveat>Caveat: ...</local-command-caveat>"
//   2) command body  — content = "<command-name>/clear</command-name>\n<command-message>...
//
// Both should be skipped in the "is CC thinking?" check (no assistant reply expected).
// The caveat record has no useful info → hide it. The command record has the name → badge.

export interface LocalCommand {
	/** Slash command name (e.g. "/clear"). null if this is the caveat-only header record. */
	name: string | null;
	args: string;
}

export function parseLocalCommand(content: unknown): LocalCommand | null {
	if (typeof content !== 'string') return null;
	const hasCaveat = content.includes('<local-command-caveat>');
	const nameMatch = /<command-name>([^<]+)<\/command-name>/.exec(content);
	if (!hasCaveat && !nameMatch) return null;
	const argsMatch = /<command-args>([^<]*)<\/command-args>/.exec(content);
	return {
		name: nameMatch ? nameMatch[1].trim() : null,
		args: (argsMatch?.[1] ?? '').trim()
	};
}
