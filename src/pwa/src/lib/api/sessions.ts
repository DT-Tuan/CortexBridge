import { api } from './client';
import type {
	SessionListItem,
	TranscriptResponse,
	StreamTokenResponse,
	ProjectSessionsResponse
} from './types';

// ADR-025 Phase 4 Slice 1 — transcript-derived cortexplexus MCP-usage tally for
// the chat-header badge. Tool NAMES + counts only (never argument/result content).
export type CortexUsage = {
	sessionUuid: string | null;
	total: number;
	byTool: Record<string, number>;
	lastUsedAt: string | null;
	scanned: number;
	truncated: boolean;
};

export type AutoAllowState = {
	enabled: boolean; // SAFE read-only tier (.on)
	autonomy: boolean; // TRUST tier: build/test + local git (.autonomy)
	push: boolean; // sub-flag: allow git push (.push)
	install: boolean; // sub-flag: allow package installs (.install)
	roOff: boolean; // ADR-028 A: opt-out of default-on read-only in workspace (.ro-off)
	burstUntil: number; // ADR-028 B: burst expiry epoch (seconds); 0 if none/expired
	burstOpaque: boolean; // ADR-028 B: burst also allows opaque cmds (ssh/python) past the backstop
};

// ADR-016 Slice 2: append `?session=<uuid>` when the caller is explicitly
// addressing a non-default session. Omitted ⇒ live-slot UID (backward-compat).
function withSession(path: string, sessionUuid?: string): string {
	return sessionUuid ? `${path}?session=${encodeURIComponent(sessionUuid)}` : path;
}

export const sessionsApi = {
	list: () => api.get<SessionListItem[]>('/api/sessions'),
	transcript: (projectId: string, sessionUuid?: string, limit?: number) => {
		const base = `/api/sessions/${encodeURIComponent(projectId)}`;
		const params = new URLSearchParams();
		if (sessionUuid) params.set('session', sessionUuid);
		if (limit && limit > 0) params.set('limit', String(limit));
		const qs = params.toString();
		return api.get<TranscriptResponse>(qs ? `${base}?${qs}` : base);
	},
	listProjectSessions: (projectId: string) =>
		api.get<ProjectSessionsResponse>(
			`/api/projects/${encodeURIComponent(projectId)}/sessions`
		),
	resume: (projectId: string, sessionUuid: string) =>
		api.post<{ acceptedAt: string; activeSessionUuid: string }>(
			`/api/sessions/${encodeURIComponent(projectId)}/resume`,
			{ sessionUuid }
		),
	setLabel: (projectId: string, sessionUuid: string, label: 'shell' | 'task' | null, note?: string) =>
		api.put<{ label: string | null; note: string | null; updatedAt: string }>(
			`/api/projects/${encodeURIComponent(projectId)}/sessions/${encodeURIComponent(sessionUuid)}/label`,
			{ label, note: note ?? null }
		),
	deleteSession: (projectId: string, sessionUuid: string) =>
		api.delete<{ deleted: boolean }>(
			`/api/projects/${encodeURIComponent(projectId)}/sessions/${encodeURIComponent(sessionUuid)}`
		),
	reply: (projectId: string, text: string, sessionUuid?: string) =>
		api.post<{ acceptedAt: string }>(
			withSession(`/api/sessions/${encodeURIComponent(projectId)}/reply`, sessionUuid),
			{ text }
		),
	quickReply: (
		projectId: string,
		action: 'yes' | 'no' | 'approve' | 'deny',
		sessionUuid?: string
	) =>
		api.post<{ acceptedAt: string }>(
			withSession(
				`/api/sessions/${encodeURIComponent(projectId)}/quick-reply/${action}`,
				sessionUuid
			)
		),
	// Select a numbered option in a CC interactive menu (permission / AskUserQuestion).
	// Backend sends the digit as a RAW keystroke — NOT a pasted reply, which a menu
	// reads as a cancel ("[Request interrupted]" → stuck thinking).
	choice: (projectId: string, digit: number, sessionUuid?: string) =>
		api.post<{ acceptedAt: string }>(
			withSession(
				`/api/sessions/${encodeURIComponent(projectId)}/choice/${digit}`,
				sessionUuid
			)
		),
	// Parse CC's CURRENTLY-VISIBLE tmux menu so the banner shows the REAL options
	// (true number + label) instead of a static 1/2/3 guess that mis-mapped.
	prompt: (projectId: string) =>
		api.get<{
			found: boolean;
			question: string | null;
			options: { num: number; label: string }[];
			canEsc: boolean;
			isAsk?: boolean;
			askSections?: string[];
			askAnswered?: number;
			askQuestions?: { index: number; question: string | null; multi?: boolean; options: { num: number; label: string }[] }[];
			askContext?: string | null;
			// VISIBLE menu is multiSelect (checkbox options): a digit only
			// TOGGLES; the raw-remote keypad drives it natively (no client-side
			// special-casing needed).
			multi?: boolean;
		}>(`/api/sessions/${encodeURIComponent(projectId)}/prompt`),
	restart: (projectId: string) =>
		api.post<{ acceptedAt: string; window: string; resumed: boolean; sessionUuid: string | null }>(
			`/api/sessions/${encodeURIComponent(projectId)}/restart`
		),
	kill: (projectId: string) =>
		api.post<{ acceptedAt: string; windowExisted: boolean }>(
			`/api/sessions/${encodeURIComponent(projectId)}/kill`
		),
	// Graceful exit: /exit -> poll 3s -> kill-window backstop; clears
	// session_ownership row -> Owner.None. Distinct from kill (hard) and
	// restart (kill+spawn). Refused 409 `exit.owned_by_pc` when Mode B.
	exit: (projectId: string) =>
		api.post<{ acceptedAt: string }>(
			`/api/sessions/${encodeURIComponent(projectId)}/exit`
		),
	// Start a FRESH claude session (no --resume). Kills any existing tmux
	// window for the project first (graceful + backstop), clears
	// session_ownership row, then spawns `claude` in workspace dir. Refused
	// 409 `new.owned_by_pc` when Mode B.
	newSession: (projectId: string) =>
		api.post<{ acceptedAt: string }>(
			`/api/sessions/${encodeURIComponent(projectId)}/new`
		),
	streamToken: () => api.post<StreamTokenResponse>('/api/auth/stream-token'),
	commands: (projectId: string) =>
		api.get<{ commands: { name: string; kind: string; description: string | null }[] }>(
			`/api/projects/${encodeURIComponent(projectId)}/commands`
		),
	// Backs the composer "@" file-mention picker. Returns up to ~40 relative
	// paths (forward-slash) ranked by filename match, build/dep dirs pruned.
	files: (projectId: string, q: string) =>
		api.get<string[]>(
			`/api/projects/${encodeURIComponent(projectId)}/files?q=${encodeURIComponent(q)}`
		),
	createProject: (name: string, startNow = true) =>
		api.post<{
			acceptedAt: string;
			projectId: string;
			path: string;
			started: boolean;
			window: string | null;
		}>('/api/projects', { name, startNow }),
	cancelPicker: (projectId: string, sessionUuid?: string) =>
		api.post<{ acceptedAt: string }>(
			withSession(
				`/api/sessions/${encodeURIComponent(projectId)}/cancel-picker`,
				sessionUuid
			)
		),
	// Raw TUI remote: send ONE key to the session's tmux pane. The single
	// primitive behind the picker keypad (digits 1-9, arrows, space, enter,
	// esc, tab). No answer composition — the user drives CC's real picker
	// and watches the live pane preview react (ADR-026 raw-remote redesign).
	key: (projectId: string, key: string, sessionUuid?: string) =>
		api.post<{ acceptedAt: string }>(
			withSession(
				`/api/sessions/${encodeURIComponent(projectId)}/key/${encodeURIComponent(key)}`,
				sessionUuid
			)
		),
	// 2× ESC parity with CC CLI — fast-cancel a running turn. Backend refuses
	// (409) if state.Processing is false (idle CC), so the UI button should only
	// be shown while status.processing === true.
	interrupt: (projectId: string, sessionUuid?: string) =>
		api.post<{ acceptedAt: string }>(
			withSession(
				`/api/sessions/${encodeURIComponent(projectId)}/interrupt`,
				sessionUuid
			)
		),
	// /btw parity — submit a reply that the backend pastes immediately if CC is
	// idle, OR buffers (1-slot per project) until the current turn ends.
	// Returns mode: 'sent-immediate' or 'queued'; replaced: true if a prior
	// buffered entry was overwritten (caller may want to warn the user).
	queue: (projectId: string, text: string, sessionUuid?: string) =>
		api.post<{ acceptedAt: string; mode: 'sent-immediate' | 'queued'; replaced?: boolean }>(
			withSession(
				`/api/sessions/${encodeURIComponent(projectId)}/queue`,
				sessionUuid
			),
			{ text }
		),
	// ADR-016 Slice 2: explicit lifecycle. Kills the project's current live
	// tmux session and resumes `sessionUuid` in its place. Refused with 409
	// `activate.owned_by_pc` while Mode B (Anthropic ext on PC) owns the
	// project — the bridge cannot terminate the PC ext.
	activate: (projectId: string, sessionUuid: string) =>
		api.post<{ acceptedAt: string; activeSessionUuid: string }>(
			`/api/sessions/${encodeURIComponent(projectId)}/activate/${encodeURIComponent(sessionUuid)}`
		),
	// Per-project auto-allow tiers. The actual allow decision is made by a host
	// PreToolUse hook; this just reads/writes the flag files the hook gates on.
	// `enabled` = SAFE read-only tier; `autonomy` = build/test + local git (TRUST);
	// `push`/`install` = sub-flags of autonomy. All default OFF. POST is a partial
	// patch — only the fields you send change.
	// ADR-025: how much CC leaned on the cortexplexus MCP this session (badge).
	getCortexUsage: (projectId: string, sessionUuid?: string) =>
		api.get<CortexUsage>(
			withSession(`/api/sessions/${encodeURIComponent(projectId)}/cortex-usage`, sessionUuid)
		),
	getAutoAllow: (projectId: string) =>
		api.get<AutoAllowState>(`/api/sessions/${encodeURIComponent(projectId)}/autoallow`),
	setAutoAllow: (projectId: string, patch: Partial<AutoAllowState>) =>
		api.post<AutoAllowState>(
			`/api/sessions/${encodeURIComponent(projectId)}/autoallow`,
			patch
		),
	// ADR-028 B: start (minutes>0) or cancel (minutes<=0) a time-boxed autonomy burst.
	setBurst: (projectId: string, minutes: number, opaque: boolean) =>
		api.post<AutoAllowState>(
			`/api/sessions/${encodeURIComponent(projectId)}/autoallow/burst`,
			{ minutes, opaque }
		),
	// Record a user-approved tool call so the host hook can auto-allow it next
	// time without a prompt. Fire-and-forget from the caller's perspective —
	// any failure is non-fatal (the command will just prompt again).
	learnAutoAllow: (projectId: string, tool: string, command: string) =>
		api.post<{ ok: boolean }>(
			`/api/sessions/${encodeURIComponent(projectId)}/autoallow/learn`,
			{ tool, command }
		),
	getOwner: (projectId: string) =>
		api.get<{
			owner: 'tmux' | 'pc' | 'none';
			sessionUuid: string | null;
			sinceUtc: string;
			takeoverSafe: boolean;
		}>(`/api/sessions/${encodeURIComponent(projectId)}/owner`),
	handoff: (
		projectId: string,
		to: 'pc' | 'tmux',
		opts?: { confirmed?: boolean; client?: string; force?: boolean }
	) =>
		api.post<{ owner: 'tmux' | 'pc' | 'none'; sessionUuid: string | null; sinceUtc: string }>(
			`/api/sessions/${encodeURIComponent(projectId)}/handoff`,
			{ to, confirmed: opts?.confirmed, client: opts?.client ?? 'pwa', force: opts?.force }
		)
};
