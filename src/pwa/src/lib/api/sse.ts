import { ApiException } from './client';
import { sessionsApi } from './sessions';
import type { PanePreviewEvent, SessionMessage, SessionStatus } from './types';

export type OwnerChangeEvent = {
	owner: 'tmux' | 'pc' | 'none';
	sessionUuid: string | null;
	sinceUtc: string;
	/** ADR-017: ModeWatcher proved the PC side is gone ⇒ the guarded
	 * "Tiếp quản" escape hatch may be enabled. Absent on older frames. */
	takeoverSafe?: boolean;
};

type Handlers = {
	onMessage: (m: SessionMessage) => void;
	onStatus: (s: SessionStatus) => void;
	onSessionSwitch?: (m: SessionMessage) => void;
	onSessionReset?: (m: SessionMessage) => void;
	onOwnerChange?: (o: OwnerChangeEvent) => void;
	/** Ephemeral live-view tail of the tmux pane while CC is processing. */
	onPanePreview?: (p: PanePreviewEvent) => void;
	onError?: (e: Event) => void;
	/** Called when the bearer token is rejected (401 from /api/auth/stream-token).
	 * Page should redirect to login + clear stored token. Stops further reconnect attempts. */
	onAuthFailed?: () => void;
};

export class StreamConnection {
	private es: EventSource | null = null;
	private retries = 0;
	private timer: ReturnType<typeof setTimeout> | null = null;
	private closed = false;
	// The ?since handshake is for the VERY FIRST connect only — gate on this, not
	// `retries`, because wake() resets retries and a stale `since` would then
	// replay everything-since-mount as a flood on every foreground reconnect.
	private firstConnect = true;
	// Last time we received ANY SSE frame; lets wake() tell a live connection from
	// an iOS-frozen one (backgrounding freezes JS timers + silently kills the
	// EventSource without firing onerror).
	private lastActivityAt = Date.now();

	constructor(
		private projectId: string,
		private handlers: Handlers,
		/** Zero-gap handshake: the byte offset + session the PWA's REST transcript
		 *  read consumed up to. Sent as ?since=/?sinceSession= so the backend
		 *  replays only [since, EOF) on connect instead of the full history
		 *  (docs/specs/01). Only applied to the FIRST connect, not reconnects —
		 *  after a drop the watchdog/refresh re-syncs and `since` would be stale. */
		private handshake?: { since: number; sinceSession: string | null }
	) {}

	async start(): Promise<void> {
		if (this.closed) return;
		try {
			const { streamToken } = await sessionsApi.streamToken();
			let url = `/api/sessions/${encodeURIComponent(this.projectId)}/stream?t=${encodeURIComponent(streamToken)}`;
			// Apply the handshake only on the very first connect: on any reconnect
			// the offset is stale (messages arrived while disconnected), so we fall
			// back to live-only + the page's reloadView (foreground/watchdog).
			if (this.handshake && this.handshake.since > 0 && this.firstConnect) {
				url += `&since=${this.handshake.since}`;
				if (this.handshake.sinceSession)
					url += `&sinceSession=${encodeURIComponent(this.handshake.sinceSession)}`;
			}
			this.firstConnect = false;
			const es = new EventSource(url);
			this.es = es;

			es.addEventListener('message', (e) => {
				this.lastActivityAt = Date.now();
				try {
					const msg = JSON.parse((e as MessageEvent).data) as SessionMessage;
					this.handlers.onMessage(msg);
				} catch {
					/* ignore parse errors */
				}
			});
			es.addEventListener('status', (e) => {
				this.lastActivityAt = Date.now();
				try {
					const status = JSON.parse((e as MessageEvent).data) as SessionStatus;
					this.handlers.onStatus(status);
				} catch {
					/* ignore */
				}
			});
			es.addEventListener('session_switch', (e) => {
				try {
					const m = JSON.parse((e as MessageEvent).data) as SessionMessage;
					this.handlers.onSessionSwitch?.(m);
				} catch {
					/* ignore */
				}
			});
			es.addEventListener('session_reset', (e) => {
				try {
					const m = JSON.parse((e as MessageEvent).data) as SessionMessage;
					this.handlers.onSessionReset?.(m);
				} catch {
					/* ignore */
				}
			});
			es.addEventListener('owner_change', (e) => {
				try {
					const o = JSON.parse((e as MessageEvent).data) as OwnerChangeEvent;
					this.handlers.onOwnerChange?.(o);
				} catch {
					/* ignore */
				}
			});
			es.addEventListener('pane_preview', (e) => {
				try {
					const p = JSON.parse((e as MessageEvent).data) as PanePreviewEvent;
					this.handlers.onPanePreview?.(p);
				} catch {
					/* ignore */
				}
			});
			es.addEventListener('open', () => {
				this.retries = 0;
				this.lastActivityAt = Date.now();
			});
			es.onerror = (e) => {
				this.handlers.onError?.(e);
				this.scheduleReconnect();
			};
		} catch (e) {
			// Fix #4: distinguish 401 (bad bearer) from network/transient errors.
			// 401 → stop reconnecting, surface to page so user is redirected to login.
			if (e instanceof ApiException && e.status === 401) {
				this.closed = true;
				this.handlers.onAuthFailed?.();
				return;
			}
			this.scheduleReconnect();
		}
	}

	private scheduleReconnect() {
		if (this.closed) return;
		this.es?.close();
		this.es = null;
		const delay = Math.min(1000 * 2 ** this.retries, 30000);
		this.retries++;
		this.timer = setTimeout(() => this.start(), delay);
	}

	/** Foreground recovery. iOS suspends a backgrounded PWA: it kills the
	 *  EventSource WITHOUT firing onerror and freezes the reconnect timer, so the
	 *  page can sit on a dead stream for up to the 30s backoff after the user
	 *  returns — exactly the ">5min away, reply, then a lag before CC shows
	 *  running" report. Call this on visibilitychange→visible / pageshow / online:
	 *  if the stream isn't verifiably OPEN-and-recently-active, drop it and
	 *  reconnect IMMEDIATELY (retries reset = no backoff). The page should also
	 *  reloadView() to resync history over REST (watcher-independent). No-op if
	 *  the connection is healthy (open + a frame within STALE_MS). */
	wake(): void {
		if (this.closed) return;
		const STALE_MS = 30_000;
		const healthy =
			this.es?.readyState === EventSource.OPEN &&
			Date.now() - this.lastActivityAt < STALE_MS;
		if (healthy) return;
		if (this.timer) {
			clearTimeout(this.timer);
			this.timer = null;
		}
		this.es?.close();
		this.es = null;
		this.retries = 0; // reconnect now, skip the backoff
		void this.start();
	}

	close() {
		this.closed = true;
		if (this.timer) clearTimeout(this.timer);
		this.es?.close();
		this.es = null;
	}
}
