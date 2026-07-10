// Mirrors the bridge spec — do NOT import from src/bridge-api.

export interface SessionListItem {
	projectId: string;
	sessionUuid: string | null;
	lastMessageAt: string | null;
	/** "running" if tmux window exists, "stopped" otherwise. Used by dashboard for online indicator. */
	status: string;
	needsInput: boolean;
	/** ADR-015 ownership: "tmux" (Mode A), "pc" (Mode B), or "none". */
	owner?: 'tmux' | 'pc' | 'none';
}

export interface ContentBlockText {
	type: 'text';
	text: string;
}

export interface ContentBlockToolUse {
	type: 'tool_use';
	id: string;
	name: string;
	input: unknown;
}

export interface ContentBlockToolResult {
	type: 'tool_result';
	tool_use_id: string;
	content: string | unknown[];
	is_error?: boolean;
}

export type ContentBlock = ContentBlockText | ContentBlockToolUse | ContentBlockToolResult;

export interface TokenUsage {
	inputTokens: number;
	outputTokens: number;
	cacheCreationInputTokens: number;
	cacheReadInputTokens: number;
	model: string | null;
}

export interface SessionMessage {
	kind: 'message' | 'unknown' | 'session_switch' | 'session_reset' | 'compact';
	uuid: string | null;
	parentUuid?: string | null;
	sessionUuid?: string | null;
	projectId: string;
	ts?: string | null;
	role?: string | null;
	userType?: string | null;
	isSidechain: boolean;
	content?: string | ContentBlock[] | null;
	text?: string | null;
	raw?: unknown;
	usage?: TokenUsage | null;
}

export interface SessionStatus {
	kind: 'status';
	projectId: string;
	sessionUuid: string | null;
	needsInput: boolean;
	running: boolean;
	lastEventAt: string | null;
	/** Notification hook message text from CC ("Claude needs your permission to use Bash" etc). */
	notificationMessage?: string | null;
	/** Authoritative "claude is working this turn" (CC hook driven). */
	processing?: boolean;
}

/**
 * Ephemeral "what CC is doing now" tail from the tmux pane, pushed ~1s while
 * the turn is processing (the JSONL transcript only lands when a block
 * completes). NOT canonical — the real bubble replaces it. Empty `lines`
 * means hide the live-view panel.
 */
export interface PanePreviewEvent {
	projectId: string;
	lines: string[];
}

export interface TranscriptResponse {
	projectId: string;
	sessionUuid: string | null;
	messages: SessionMessage[];
	/** True when viewing a non-active session — composer hidden in PWA. */
	readOnly?: boolean;
	/** Total records in the JSONL (only set when ?limit= tail-load was used). */
	total?: number;
	/** True when older records exist beyond the returned tail window. */
	truncated?: boolean;
	/** Byte offset (EOF) this read consumed up to — the zero-gap SSE handshake
	 *  anchor. Passed back as ?since= when opening the stream so only [tailOffset,
	 *  EOF) is replayed, never the full history. See docs/specs/01. */
	tailOffset?: number;
}

/** Spec 04 — row in project-detail session list view. */
export type SessionLabelValue = 'shell' | 'task' | null;

export interface SessionRow {
	sessionUuid: string;
	firstMessageAt: string | null;
	lastMessageAt: string | null;
	messageCount: number;
	firstUserText: string | null;
	cwd: string | null;
	isActive: boolean;
	isImported: boolean;
	canResume: boolean;
	sizeBytes: number;
	label: SessionLabelValue;
	note: string | null;
}

export interface ProjectSessionsResponse {
	projectId: string;
	activeSessionUuid: string | null;
	owner: 'tmux' | 'pc' | 'none';
	sessions: SessionRow[];
}

export interface ProjectListItem {
	name: string;
	path: string;
	branch: string | null;
	dirty: boolean;
	ahead: number;
	behind: number;
	hasGit: boolean;
	sessionCount: number;
	activeSessionUuid: string | null;
	lastActivityAt: string | null;
}

export interface StreamTokenResponse {
	streamToken: string;
	expiresAt: string;
}

export interface UsageBlock5h {
	startUtc: string;
	endUtc: string;
	remainingMinutes: number;
	currentCostUsd: number;
	projectedCostUsd: number;
	currentTokens: number;
	projectedTokens: number;
	costPerHour: number;
	tokensPerMinute: number;
	models: string[];
	entries: number;
}

/**
 * Official Anthropic plan quota (ADR-024) — the exact % Claude Code's /usage
 * panel shows, sampled host-side from the OAuth usage endpoint. Sole source
 * for the 5h/7d gauges. takenAtUtc is the official sample's own timestamp:
 * when the endpoint is unreachable the last-known block is carried forward
 * unchanged → an old takenAtUtc means STALE (render a badge).
 */
export interface UsageOfficialWindow {
	utilization: number;
	resetsAt: string;
}

export interface UsageOfficial {
	fiveHour: UsageOfficialWindow | null;
	sevenDay: UsageOfficialWindow | null;
	takenAtUtc: string;
}

export interface UsageModelBreakdown {
	model: string;
	costUsd: number;
	inputTokens: number;
	outputTokens: number;
	cacheCreationTokens: number;
	cacheReadTokens: number;
}

export interface UsageWeek7d {
	periodStart: string;
	currentCostUsd: number;
	currentTokens: number;
	modelBreakdown: UsageModelBreakdown[];
}

export interface UsageProject {
	name: string;
	encodedPath: string;
	totalCostUsd: number;
	totalTokens: number;
	inputTokens: number;
	outputTokens: number;
	cacheCreationTokens: number;
	cacheReadTokens: number;
	sessionCount: number;
	lastActivity: string | null;
	models: string[];
}

export interface UsageResponse {
	takenAtUtc: string;
	block5h: UsageBlock5h | null;
	week7d: UsageWeek7d | null;
	projects: UsageProject[];
	official: UsageOfficial | null;
}

export interface UsageHistoryPoint {
	takenUtc: string;
	block5hCurrentUsd: number;
	block5hProjectedUsd: number;
	block5hPctCurrent: number;
	block5hPctProjected: number;
	week7dCurrentUsd: number;
	week7dPctCurrent: number;
}

export interface UsageHistoryResponse {
	points: UsageHistoryPoint[];
}
