<script lang="ts">
	import { onMount, onDestroy, tick } from 'svelte';
	import { page } from '$app/state';
	import { goto, replaceState } from '$app/navigation';
	import { auth } from '$lib/stores/auth.svelte';
	import { sessionsApi } from '$lib/api/sessions';
	import type { CortexUsage } from '$lib/api/sessions';
	import { ApiException } from '$lib/api/client';
	import { StreamConnection } from '$lib/api/sse';
	import type { SessionMessage } from '$lib/api/types';
	import ChatBubble from '$lib/components/ChatBubble.svelte';
	import OwnerBanner from '$lib/components/OwnerBanner.svelte';
	import { parseLocalCommand } from '$lib/utils/commands';
	import { hapticTick } from '$lib/utils/haptic';
	import { pullToRefresh } from '$lib/utils/pull-refresh';

	const projectId = $derived(decodeURIComponent(page.params.id ?? ''));
	// Spec 04: viewing a specific past session via ?session=<uuid> → read-only mode.
	const requestedSessionUuid = $derived(page.url.searchParams.get('session'));
	let readOnly = $state(false);

	// ADR-016 Slice 2: when ?session= is deep-linked to a historical (non-live)
	// session and the user writes, the backend returns 409 `session.not_live`.
	// Confirm → POST activate (kill+resume) → retry. activate.owned_by_pc surfaces
	// a guidance message (the bridge cannot kill the Anthropic ext per ADR-017).
	async function tryActivateOn409<T>(fn: () => Promise<T>): Promise<T> {
		try {
			return await fn();
		} catch (e) {
			if (
				!(e instanceof ApiException) ||
				e.status !== 409 ||
				e.error.code !== 'session.not_live' ||
				!requestedSessionUuid
			)
				throw e;
			const ok = window.confirm(
				'Phiên này không phải phiên đang sống. Kích hoạt sẽ ngắt phiên đang chạy. Đồng ý?'
			);
			if (!ok) throw e;
			try {
				await sessionsApi.activate(projectId, requestedSessionUuid);
			} catch (e2) {
				if (e2 instanceof ApiException && e2.error.code === 'activate.owned_by_pc') {
					alert(
						'Mode PC đang chiếm phiên này — đóng VS Code workspace hoặc dùng Tiếp quản (ép) trước.'
					);
				}
				throw e2;
			}
			return await fn();
		}
	}

	// Use $state (NOT $state.raw) so push/splice/index-set trigger reactivity.
	// Previously $state.raw caused SSE-delivered messages to land in the array but
	// not re-render the chat — perceived as "no realtime update".
	const messages = $state<SessionMessage[]>([]);
	const seenUuids = new Set<string>();
	// Hide CC's auto-injected "[Request interrupted by user…]" cancellation
	// markers from the rendered transcript — they bloat chat with no info the
	// user can act on (one per Esc / menu-cancel; can stack into noisy runs
	// during a tmux-paste-eaten-by-menu loop). Underlying JSONL row stays
	// intact so backend audit + thinking-detection (isInterruptText below) are
	// unaffected; only the visual layer is filtered.
	const visibleMessages = $derived.by(() =>
		messages.filter((m) => !isInterruptText(m.content))
	);
	// Windowing: bound DOM for huge transcripts. Render the last RENDER_CHUNK
	// messages; reveal older ones in chunks. Mirrors the companion ext's
	// Transcript windowing.
	const RENDER_CHUNK = 400;
	let renderLimit = $state(RENDER_CHUNK);
	// Backend tail-load: open the session on the last INITIAL_LIMIT records
	// (a 37 MB / 13.8k-msg transcript was a 57 MB payload). `hasEarlier` = the
	// server has older records we haven't fetched; "load full history" pulls them.
	const INITIAL_LIMIT = 400;
	let hasEarlier = $state(false);
	let totalMessages = $state(0);
	let loadingEarlier = $state(false);
	// True once the user pulled the full history; keeps re-fetches (watchdog /
	// pull-to-refresh) at the same extent instead of collapsing back to the tail.
	let fullLoaded = $state(false);
	const windowHidden = $derived(Math.max(0, visibleMessages.length - renderLimit));
	const windowedMessages = $derived(
		windowHidden > 0 ? visibleMessages.slice(visibleMessages.length - renderLimit) : visibleMessages
	);
	function showEarlier() {
		const before = scrollEl?.scrollHeight ?? 0;
		renderLimit += RENDER_CHUNK;
		tick().then(() => {
			if (scrollEl) scrollEl.scrollTop = scrollEl.scrollHeight - before;
		});
	}

	// Re-fetch the WHOLE transcript (no limit) when the user wants history beyond
	// the initial tail. One-time cost paid only on explicit request; afterwards
	// the local DOM windowing (showEarlier) reveals the rest on scroll.
	async function loadFullHistory() {
		if (loadingEarlier) return;
		loadingEarlier = true;
		try {
			const t = await sessionsApi.transcript(projectId, requestedSessionUuid ?? undefined);
			stickToBottom = false; // user is reading history — don't yank to bottom
			loadFresh(t.messages);
			hasEarlier = false;
			fullLoaded = true;
		} catch {
			/* transient — tail stays in place, user can retry */
		} finally {
			loadingEarlier = false;
		}
	}

	// Consistent re-fetch for the watchdog + pull-to-refresh. Fetch the CURRENT
	// extent (tail, or full if the user expanded it) and REPLACE via loadFresh —
	// NEVER appendMessage-merge a fetch into the existing array: merging a full
	// fetch into a tail-loaded list appends the ~13k older messages AFTER the
	// newest (wrong order) and duplicates null-uuid records (appendMessage can't
	// dedup a null uuid). See docs/specs/02 "re-fetch phải REPLACE".
	async function reloadView() {
		const limit = fullLoaded ? undefined : INITIAL_LIMIT;
		const t = await sessionsApi.transcript(projectId, requestedSessionUuid ?? undefined, limit);
		loadFresh(t.messages);
		hasEarlier = !!t.truncated;
		totalMessages = t.total ?? 0;
		refreshCortex();
	}
	let needsInput = $state(false);
	let notificationMessage = $state<string | null>(null);
	// Live menu parsed from CC's tmux pane (real options, not a static 1/2/3
	// guess). Fetched on needsInput; null until/unless resolved.
	type LivePrompt = {
		found: boolean;
		question: string | null;
		options: { num: number; label: string }[];
		canEsc: boolean;
		// Pending multi-question AskUserQuestion (parsed from the picker tab
		// bar — the only remote signal while it's unanswered, ADR-017 note).
		isAsk?: boolean;
		askSections?: string[];
		askAnswered?: number;
		// All questions collected by the bridge's read-only tab traversal
		// (PromptEndpoint). Absent ⇒ traversal skipped/aborted ⇒ show current only.
		askQuestions?: { index: number; question: string | null; multi?: boolean; options: { num: number; label: string }[] }[];
		// CC's analysis prose printed above the picker, scraped from the pane
		// (it isn't in JSONL until the question is answered). Best-effort.
		askContext?: string | null;
	};
	let livePrompt = $state<LivePrompt | null>(null);
	// True from the moment needsInput flips until the live /prompt fetch
	// concludes. While pending the banner shows a neutral "đang đọc menu…"
	// instead of the generic fallback — otherwise the fallback ("old" banner)
	// flashes for ~1s then gets swapped by the real menu ("new" banner).
	let livePromptPending = $state(false);
	let running = $state(true);
	// Authoritative processing signal from the bridge (CC hook driven). Once we've
	// seen a status frame we trust this over the JSONL-shape heuristic.
	let serverProcessing = $state(false);
	let serverStatusSeen = $state(false);
	// Ephemeral live-view: tail of the tmux pane while CC is processing (the
	// JSONL transcript only lands when a block completes). NOT canonical —
	// cleared (panel hidden) the moment Processing ends; the real bubble is
	// already in `messages`.
	let livePane = $state<string[]>([]);
	// ADR-015/017 owner state — null until first /owner GET resolves.
	let owner = $state<'tmux' | 'pc' | 'none' | null>(null);
	// ADR-017: bridge-proved "PC is gone, B→A safe" — gates the one Tiếp quản button.
	let takeoverSafe = $state(false);
	const isModeB = $derived(owner === 'pc');
	// Auto-allow SAFE read-only permission prompts (per-project flag, default OFF).
	let autoAllow = $state({ enabled: false, autonomy: false, push: false, install: false });
	let autoAllowBusy = $state(false);
	let autoAllowOpen = $state(false);
	// Shield is "active" (filled) when either tier is on; autonomy tints it amber
	// as a louder "you've widened beyond read-only" signal.
	const aaActive = $derived(autoAllow.enabled || autoAllow.autonomy);
	// ADR-025 Slice 1: how much CC leaned on cortexplexus this session (badge only).
	let cortex = $state<CortexUsage | null>(null);
	const cortexBreakdown = $derived(
		cortex && cortex.total > 0
			? Object.entries(cortex.byTool)
					.sort((a, b) => b[1] - a[1])
					.map(([t, n]) => `${t} ×${n}`)
					.join(' · ')
			: ''
	);
	async function refreshCortex() {
		try {
			cortex = await sessionsApi.getCortexUsage(projectId, requestedSessionUuid ?? undefined);
		} catch {
			/* non-fatal — badge just stays hidden/last value */
		}
	}
	let loading = $state(true);
	let error = $state<string | null>(null);
	let replyText = $state('');
	let sending = $state(false);
	// Stop-button state: true while POST /interrupt is in-flight. Distinct from
	// `sending` so the spinner doesn't race against a parallel reply submit.
	let interrupting = $state(false);
	// /btw parity: true while POST /queue is in-flight. The textarea clearing
	// on success is the primary feedback; SSE catches up when CC actually
	// processes the queued reply.
	let queueing = $state(false);
	let scrollEl: HTMLDivElement | null = $state(null);
	let contentEl: HTMLDivElement | null = $state(null);
	// JS-computed bottom-align — set marginTop on content so it sits at bottom of
	// scrollEl when content < available space. iOS Safari has flaky behavior with
	// column-reverse/mt-auto/min-h-full + overflow-y:auto, so we compute directly.
	let chatTopSpacer = $state(0);
	// DEBUG: visible overlay numbers so we can diagnose layout issue on phone
	let dbgContainerH = $state(0);
	let dbgContentH = $state(0);
	let dbgComposerH = $state(0);
	let dbgRecomputes = $state(0);
	let dbgScrollTop = $state(0);
	let dbgScrollMax = $state(0);
	let dbgVV = $state(0);
	let dbgWinH = $state(0);
	let dbgBodyH = $state(0);
	let dbgAppH = $state('');
	let dbgWinW = $state(0);
	let dbgVVOffset = $state(0);
	let taEl: HTMLTextAreaElement | null = $state(null);
	let composerHeight = $state(0);
	let stream: StreamConnection | null = null;
	// Zero-gap SSE handshake (docs/specs/01): the byte offset + session the
	// onMount REST tail-load consumed up to. Passed to StreamConnection so the
	// backend replays only [since, EOF) on connect instead of the full history.
	let streamSince = 0;
	let streamSinceSession: string | null = null;

	$effect(() => {
		void replyText;
		if (!taEl) return;
		taEl.style.height = 'auto';
		taEl.style.height = `${taEl.scrollHeight}px`;
	});

	// Keyboard handling: simplest reliable approach. Composer uses position:fixed
	// with `bottom: env(keyboard-inset-height, 0px)`. When iOS 17.4+/Chrome supports
	// VirtualKeyboard API (overlaysContent=true set in +layout.svelte), the env var
	// gives the EXACT keyboard height — composer slides above keyboard precisely.
	// Otherwise falls back to bottom: 0 (composer at viewport bottom; iOS native
	// scroll-into-view handles things).
	let composerFocused = $state(false);
	const kbOffset = 0;

	// Dedup key. Real CC records have a uuid. Internal records (local-command,
	// system, summary, compact) often have uuid=null — appendMessage used to skip
	// dedup for those, so the SSE ?since= catch-up overlapping the live fanout (or
	// any double-fetch) duplicated them. Key null-uuid records by ts+kind+role+
	// text-prefix: a byte-identical re-send collapses to one, while two genuinely
	// distinct records (different ts/text) stay separate. Namespaced `t:` so it
	// never collides with a real uuid in the same Set.
	function dedupKey(m: SessionMessage): string {
		if (m.uuid) return m.uuid;
		const text = typeof m.text === 'string' ? m.text.slice(0, 60) : '';
		return `t:${m.ts ?? ''}|${m.kind}|${m.role ?? ''}|${text}`;
	}

	function appendMessage(m: SessionMessage) {
		// CC 2.1.x ghi nhiều record nội bộ không có giá trị hiển thị (permission-mode,
		// queue-operation, attachment, file-history-snapshot, ai-title, last-prompt) —
		// chúng tới SSE với kind="unknown". Drop ngay đầu pipeline để chat view sạch.
		if (m.kind === 'unknown') return;

		// Reconcile: if this is the canonical of a pending optimistic user message, replace it
		if (
			m.role === 'user' &&
			m.userType !== 'internal' &&
			typeof m.content === 'string' &&
			m.uuid
		) {
			const idx = messages.findIndex(
				(x) => x.uuid?.startsWith('optimistic-') && x.content === m.content
			);
			if (idx >= 0) {
				const oldUuid = messages[idx].uuid;
				if (oldUuid) seenUuids.delete(oldUuid);
				messages[idx] = m;
				seenUuids.add(m.uuid);
				return;
			}
		}
		const key = dedupKey(m);
		if (seenUuids.has(key)) return;
		seenUuids.add(key);
		messages.push(m);
		// $effect on messages.length will trigger doScrollToBottom after Svelte commit.
	}

	// Batch full-transcript load (mount + session switch). A fresh transcript has
	// no pending optimistic placeholders, so we skip appendMessage's per-message
	// O(n) findIndex reconcile entirely: dedup via the seenUuids Set, build a
	// plain array once, then mutate `messages` in a few chunked pushes instead of
	// thousands of individual reactive appends. The auto-scroll $effect still
	// fires once (batched), not per message. SSE + pull-refresh keep using
	// appendMessage (incremental, must reconcile optimistic sends).
	function loadFresh(incoming: SessionMessage[]) {
		seenUuids.clear();
		const built: SessionMessage[] = [];
		for (const m of incoming) {
			if (m.kind === 'unknown') continue;
			const key = dedupKey(m);
			if (seenUuids.has(key)) continue;
			seenUuids.add(key);
			built.push(m);
		}
		messages.length = 0;
		const CHUNK = 1000;
		for (let i = 0; i < built.length; i += CHUNK) {
			messages.push(...built.slice(i, i + CHUNK));
		}
	}

	// stickToBottom: true while content's bottom edge is within ~200px of viewport bottom.
	// Normal scroll semantics: scrollTop=scrollHeight-clientHeight means at bottom.
	let stickToBottom = $state(true);

	function isNearBottom(): boolean {
		if (!scrollEl) return true;
		const distance = scrollEl.scrollHeight - scrollEl.scrollTop - scrollEl.clientHeight;
		return distance < 200;
	}

	function onScroll() {
		stickToBottom = isNearBottom();
		if (scrollEl) {
			dbgScrollTop = Math.round(scrollEl.scrollTop);
			dbgScrollMax = Math.round(scrollEl.scrollHeight - scrollEl.clientHeight);
		}
	}

	function doScrollToBottom() {
		if (!scrollEl) return;
		scrollEl.scrollTop = scrollEl.scrollHeight;
	}

	// Recompute chatTopSpacer whenever messages change, composer height changes,
	// or scrollEl resizes (window resize, keyboard show/hide). Pushes content
	// down to bottom of available area when content is shorter.
	$effect(() => {
		void messages.length;
		void composerHeight;
		void kbOffset; // re-run when keyboard toggles
		if (!scrollEl || !contentEl) return;
		const recompute = () => {
			if (!scrollEl || !contentEl) return;
			const containerH = scrollEl.clientHeight;
			const padTop = 16; // pt-4
			const padBottom = 12; // pb-3 on scrollEl + small buffer
			const available = containerH - padTop - padBottom;
			// svelte-check narrows `contentEl` to `never` inside this closure
			// despite the runtime truthy guard above — a Svelte 5 `$state(null)`
			// quirk where the literal-null initializer + closure capture defeats
			// flow analysis. Cast (not `!` — that doesn't undo a `never` narrowing).
			const contentH = (contentEl as HTMLDivElement).scrollHeight;
			const diff = available - contentH;
			const next = diff > 0 ? Math.round(diff) : 0;
			// expose for debug overlay
			dbgContainerH = containerH;
			dbgContentH = contentH;
			dbgComposerH = composerHeight;
			dbgRecomputes += 1;
			if (next !== chatTopSpacer) chatTopSpacer = next;
			// When content OVERFLOWS available area AND user is supposed to be at
			// the bottom (stickToBottom), force scroll there so newest is visible.
			// Without this, layout can leave scrollTop=0 (showing oldest at top).
			if (diff < 0 && stickToBottom) {
				scrollEl.scrollTop = scrollEl.scrollHeight;
			}
			dbgScrollTop = Math.round(scrollEl.scrollTop);
			dbgScrollMax = Math.round(scrollEl.scrollHeight - scrollEl.clientHeight);
			if (typeof window !== 'undefined') {
				dbgVV = Math.round(window.visualViewport?.height ?? 0);
				dbgVVOffset = Math.round(window.visualViewport?.offsetTop ?? 0);
				dbgWinH = window.innerHeight;
				dbgWinW = window.innerWidth;
				dbgBodyH = document.body.clientHeight;
				dbgAppH = document.documentElement.style.getPropertyValue('--app-height') || '(unset)';
			}
		};
		const ro = new ResizeObserver(recompute);
		ro.observe(scrollEl);
		ro.observe(contentEl);
		recompute();
		return () => ro.disconnect();
	});

	/** Force scroll to bottom — used on mount + session switch + restart */
	function scrollToBottomNow() {
		stickToBottom = true;
		tick().then(() => {
			doScrollToBottom();
			requestAnimationFrame(() => doScrollToBottom());
			setTimeout(() => doScrollToBottom(), 150);
		});
	}

	// Auto-scroll on EVERY message append (reactive on messages.length).
	// ResizeObserver alone wasn't reliable because Svelte's keyed-each may unmount/remount the
	// observed child. $effect tracking length covers append/remove explicitly.
	$effect(() => {
		void messages.length; // dependency — re-run on each push/splice
		if (!stickToBottom) return;
		// Run twice: after Svelte commit + after browser reflow (markdown {@html} can grow async)
		tick().then(() => {
			doScrollToBottom();
			requestAnimationFrame(() => doScrollToBottom());
		});
	});

	// Cách 1: the needsInput prompt card now renders at the BOTTOM of the chat
	// flow (right after CC's analysis), not as a screen-eating top banner. When a
	// prompt appears, scroll it into view so the user sees question + analysis
	// together. Card height also grows async → the ResizeObserver below catches it.
	$effect(() => {
		if (!needsInput) return;
		if (!stickToBottom) return;
		tick().then(() => {
			doScrollToBottom();
			requestAnimationFrame(() => doScrollToBottom());
		});
	});

	// Backup: ResizeObserver catches LATE content growth (image load, font swap, code highlight)
	$effect(() => {
		if (!scrollEl) return;
		const ro = new ResizeObserver(() => {
			if (stickToBottom) doScrollToBottom();
		});
		ro.observe(scrollEl);
		const child = scrollEl.firstElementChild;
		if (child) ro.observe(child);
		return () => ro.disconnect();
	});

	onMount(async () => {
		if (!auth.isAuthed) {
			goto('/login');
			return;
		}

		// Transcript fetch is BEST-EFFORT and decoupled from SSE: a transient
		// failure (e.g. bridge mid-redeploy) must NOT prevent the stream from
		// starting — otherwise the banner / live updates never come back and
		// onMount has no retry (the "PWA lost banner after redeploys" bug). The
		// periodic watchdog re-fetches the transcript anyway.
		try {
			const t = await sessionsApi.transcript(projectId, requestedSessionUuid ?? undefined, INITIAL_LIMIT);
			loadFresh(t.messages);
			hasEarlier = !!t.truncated;
			totalMessages = t.total ?? 0;
			readOnly = !!t.readOnly;
			// Anchor the SSE handshake to exactly what this read consumed.
			streamSince = t.tailOffset ?? 0;
			streamSinceSession = t.sessionUuid;
			scrollToBottomNow();
		} catch (e) {
			if (e instanceof ApiException && e.status === 401) {
				loading = false;
				auth.clear();
				goto('/login');
				return;
			}
			error = e instanceof Error ? e.message : String(e);
			// fall through → still open SSE for the (assumed active) session.
		}
		loading = false;

		// Spec 04: don't open SSE for read-only past-session views — backend would
		// close the stream after one status frame anyway, no point.
		if (readOnly) return;

		try {
			// Bootstrap owner state in parallel — failure non-fatal (banner just stays null).
			sessionsApi
				.getOwner(projectId)
				.then((o) => {
					owner = o.owner;
					takeoverSafe = o.takeoverSafe;
				})
				.catch(() => {});

			// Auto-allow state — non-fatal, toggle just stays off if it fails.
			sessionsApi
				.getAutoAllow(projectId)
				.then((a) => {
					autoAllow = a;
				})
				.catch(() => {});

			// Cortexplexus MCP-usage badge — non-fatal, hidden if it fails.
			refreshCortex();

			stream = new StreamConnection(projectId, {
				onMessage: (m) => appendMessage(m),
				onPanePreview: (p) => {
					livePane = p.lines;
				},
				onStatus: (s) => {
					// If a Notification hook fired BEFORE our reply landed in tmux, the
					// SSE status frame for it may arrive AFTER our optimistic clear. Drop
					// any needsInput=true within the grace window — a genuinely new prompt
					// will arrive after the window expires.
					if (s.needsInput && Date.now() - lastReplyAt < STALE_NOTIFY_GUARD_MS) {
						// also dismiss the lockscreen push that triggered this stale event
						dismissDeviceNotification();
					} else {
						needsInput = s.needsInput;
						notificationMessage = s.needsInput ? (s.notificationMessage ?? null) : null;
					}
					running = s.running;
					serverProcessing = s.processing ?? false;
					serverStatusSeen = true;
					// Defensive: if the turn ended, drop the live preview even
					// if the backend's empty pane_preview frame is delayed/lost.
					if (!serverProcessing) livePane = [];
				},
				onOwnerChange: (o) => {
					owner = o.owner;
					takeoverSafe = o.takeoverSafe ?? false;
				},
				onSessionSwitch: () => {
					// issue #1: CC switched sessions (e.g. /clear) in the same tmux
					// window. Drop any ?session=<oldUuid> pin so reply / interrupt /
					// needsInput follow the new live slot instead of the dead session.
					if (requestedSessionUuid) {
						const u = new URL(page.url);
						u.searchParams.delete('session');
						replaceState(u, {});
					}
					fullLoaded = false;   // new session opens on the tail
					sessionsApi.transcript(projectId, undefined, INITIAL_LIMIT).then((t2) => {
						loadFresh(t2.messages);   // clears + repopulates in one batch
						hasEarlier = !!t2.truncated;
						totalMessages = t2.total ?? 0;
						scrollToBottomNow();
					});
				},
				onSessionReset: () => {
					seenUuids.clear();
					messages.length = 0;
					livePane = [];
				},
				onAuthFailed: () => {
					auth.clear();
					goto('/login');
				}
			}, { since: streamSince, sinceSession: streamSinceSession });
			await stream.start();
		} catch (e) {
			loading = false;
			if (e instanceof ApiException && e.status === 401) {
				auth.clear();
				goto('/login');
				return;
			}
			error = e instanceof Error ? e.message : String(e);
		}
	});

	onDestroy(() => stream?.close());

	// Pull-to-refresh: re-fetch the transcript; appendMessage dedups by uuid so
	// already-shown messages aren't duplicated.
	async function refreshTranscript() {
		try {
			await reloadView();   // REPLACE, not merge — see reloadView()
		} catch {
			/* transient — user can pull again */
		}
	}

	async function send() {
		const text = replyText.trim();
		// Gate by isThinking, not sending — sending flips off as soon as POST /reply
		// returns 202, but CC keeps chewing on the previous turn for many more
		// seconds. Sending another reply during that window interleaves bytes into
		// tmux while CC's prompt isn't ready, corrupting input.
		if (!text || isThinking) return;
		// ADR-015: in Mode B (owner=pc), the tmux claude is dead — bridge has
		// nothing to send-keys into. Banner offers Take Over; refuse silently here.
		if (isModeB) return;
		hapticTick();

		// Optimistic append — appears instantly. Reconciled in appendMessage()
		// when SSE delivers the canonical message with a real CC uuid.
		const tempUuid = `optimistic-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
		const optimistic: SessionMessage = {
			kind: 'message',
			uuid: tempUuid,
			parentUuid: null,
			sessionUuid: null,
			projectId,
			ts: new Date().toISOString(),
			role: 'user',
			userType: 'external',
			isSidechain: false,
			content: text
		};
		messages.push(optimistic);
		seenUuids.add(tempUuid);
		// $effect handles scroll automatically when messages.length changes.

		const sentText = text;
		replyText = '';
		sending = true;
		try {
			await tryActivateOn409(() =>
				sessionsApi.reply(projectId, sentText, requestedSessionUuid ?? undefined)
			);
			lastReplyAt = Date.now();
			dismissDeviceNotification();
		} catch (e) {
			// Roll back the optimistic message on failure, restore composer text for retry
			const idx = messages.findIndex((x) => x.uuid === tempUuid);
			if (idx >= 0) messages.splice(idx, 1);
			seenUuids.delete(tempUuid);
			replyText = sentText;
			error = e instanceof Error ? e.message : String(e);
		} finally {
			sending = false;
		}
	}

	function onKey(e: KeyboardEvent) {
		// "@" file-picker navigation takes priority while it's open with results.
		// Guard isComposing so a Vietnamese IME commit (Enter) isn't stolen.
		if (atOpen && atFiles.length > 0 && !e.isComposing) {
			if (e.key === 'ArrowDown') {
				e.preventDefault();
				atActiveIdx = (atActiveIdx + 1) % atFiles.length;
				return;
			}
			if (e.key === 'ArrowUp') {
				e.preventDefault();
				atActiveIdx = (atActiveIdx - 1 + atFiles.length) % atFiles.length;
				return;
			}
			if (e.key === 'Enter' || e.key === 'Tab') {
				e.preventDefault();
				pickFile(atFiles[atActiveIdx]);
				return;
			}
			if (e.key === 'Escape') {
				e.preventDefault();
				closeMention();
				return;
			}
		}
		// Enter without shift submits — like iMessage. Mobile keyboards' Enter is "newline" by default,
		// so users can long-press composer or use a hardware keyboard.
		if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
			e.preventDefault();
			send();
		}
	}

	// Thinking indicator: walk back from end. CC is "thinking" if the most recent
	// MEANINGFUL turn is from the user. Skip records that aren't real user turns:
	//   - kind=unknown (CC internal noise — already filtered backend-side)
	//   - local-command caveats (/clear, /help — no reply expected)
	//   - tool_result blocks (model↔tool plumbing, not user-typed)
	//   - "[Request interrupted by user ...]" auto-injection (declines, escapes)
	function isInterruptText(content: unknown): boolean {
		if (!Array.isArray(content)) return false;
		if (content.length !== 1) return false;
		const b = content[0];
		return b?.type === 'text' && typeof b.text === 'string'
			&& b.text.startsWith('[Request interrupted');
	}
	function hasToolResult(content: unknown): boolean {
		return Array.isArray(content) && content.some((b) => b?.type === 'tool_result');
	}
	const isThinking = $derived.by(() => {
		if (sending) return true;
		// Server processing is authoritative once we've seen a status frame: the
		// bridge knows from CC hooks (UserPromptSubmit→Stop) whether claude is
		// mid-turn — far more reliable than guessing from JSONL shape, which
		// wrongly went false the moment claude emitted its first assistant line
		// then spent minutes on tool work ("crashed?" → nhồi lệnh).
		if (serverStatusSeen) return serverProcessing;
		// Fallback only for the brief window before the first status frame.
		for (let i = messages.length - 1; i >= 0; i--) {
			const m = messages[i];
			if (m.kind === 'unknown') continue;
			if (parseLocalCommand(m.content)) continue;
			if (hasToolResult(m.content)) continue;
			if (isInterruptText(m.content)) continue;
			if (m.role === 'assistant') return false;
			if (m.role === 'user' && m.userType !== 'internal') return true;
			// system, summary — skip and keep looking back
		}
		return false;
	});

	// When needsInput=true, surface the actual question + numbered options inside the
	// banner so user knows WHAT they're approving. Walks back to last assistant text and
	// extracts:
	//   - question: trimmed text of last meaningful paragraph
	//   - options:  lines matching `^N. text` (or `N) text` / `N - text`)
	// Falls back to generic 1/2 if no numbered options found.
	type ParsedPrompt = {
		question: string;
		options: { num: number; label: string }[];
		// Picker mode = AskUserQuestion-style interactive picker (checkbox markers in label).
		// In this mode a digit just toggles a checkbox; user must navigate to Submit + Enter.
		// PWA can't safely shortcut this from a single button tap, so we surface a hint instead.
		isPicker: boolean;
	};

	function parseAssistantPrompt(text: string): ParsedPrompt {
		const lines = text.split(/\r?\n/);
		const optRe = /^\s*(\d+)[.)\-]\s+(.+?)\s*$/;
		const checkboxRe = /^\s*\[[ xX✔✓·•]\]\s*/;
		const options: { num: number; label: string }[] = [];
		const nonOptionLines: string[] = [];
		let isPicker = false;
		for (const line of lines) {
			const m = optRe.exec(line);
			if (m) {
				let label = m[2].trim();
				if (checkboxRe.test(label)) {
					isPicker = true;
					label = label.replace(checkboxRe, '');
				}
				options.push({ num: Number(m[1]), label });
			} else {
				nonOptionLines.push(line);
			}
		}
		const questionText = nonOptionLines.join('\n').trim();
		const paragraphs = questionText.split(/\n{2,}/).map((p) => p.trim()).filter(Boolean);
		const question = paragraphs[paragraphs.length - 1] ?? questionText;
		return { question: question.slice(0, 500), options, isPicker };
	}

	const promptInfo = $derived.by<ParsedPrompt | null>(() => {
		if (!needsInput) return null;
		for (let i = messages.length - 1; i >= 0; i--) {
			const m = messages[i];
			if (m.role !== 'assistant') continue;
			let text = '';
			if (typeof m.content === 'string') text = m.content;
			else if (Array.isArray(m.content)) {
				for (const b of m.content) if (b.type === 'text') text += (text ? '\n' : '') + b.text;
			}
			if (text.trim()) return parseAssistantPrompt(text);
			break;
		}
		return null;
	});

	// CC permission prompts always offer 3 fixed options (1=Yes, 2=Yes don't ask, 3=No)
	// but the option text isn't written to JSONL — only the assistant tool_use is.
	// Detect via notification message ("...permission to use Bash" etc) and synthesize
	// the 3-button shortcut so user doesn't have to remember the convention.
	const isPermissionPrompt = $derived(
		!!notificationMessage && /permission to use/i.test(notificationMessage)
	);

	// For permission prompts, surface the actual tool_use the user is being asked to
	// approve — most importantly the Bash command being run, since "Allow Bash?" alone
	// gives no signal whether it's `ls -la` or `rm -rf /`.
	type PendingTool = { name: string; label: string; details: string };

	function summarizeToolUse(name: string, input: unknown): PendingTool {
		const i = (input ?? {}) as Record<string, unknown>;
		const str = (k: string) => (typeof i[k] === 'string' ? (i[k] as string) : '');
		switch (name) {
			case 'Bash':
				return { name, label: str('description') || 'Bash', details: str('command') };
			case 'Write':
				return { name, label: 'Ghi file', details: str('file_path') };
			case 'Edit':
				return { name, label: 'Sửa file', details: str('file_path') };
			case 'Read':
				return { name, label: 'Đọc file', details: str('file_path') };
			case 'Glob':
				return { name, label: 'Glob', details: str('pattern') };
			case 'Grep':
				return { name, label: 'Grep', details: str('pattern') };
			case 'WebFetch':
				return { name, label: 'WebFetch', details: str('url') };
			default:
				return { name, label: name, details: JSON.stringify(i).slice(0, 200) };
		}
	}

	const pendingTool = $derived.by<PendingTool | null>(() => {
		if (!needsInput) return null;
		for (let i = messages.length - 1; i >= 0; i--) {
			const m = messages[i];
			if (m.role !== 'assistant') continue;
			if (!Array.isArray(m.content)) break;
			for (let j = m.content.length - 1; j >= 0; j--) {
				const block = m.content[j];
				if (block.type === 'tool_use') {
					return summarizeToolUse(block.name, block.input);
				}
			}
			break;
		}
		return null;
	});

	// AskUserQuestion has a structured `questions` array with labelled options.
	// CC's TUI renders this as an interactive picker; from PWA we render the same
	// data as a tap-to-fill form so user doesn't have to type out option labels.
	type AskOption = { label: string; description?: string };
	type AskQuestion = {
		question: string;
		header?: string;
		multiSelect?: boolean;
		options: AskOption[];
	};

	// Watchdog: if isThinking has been true for >30s with no new messages arriving,
	// SSE may have silently dropped (iOS background tab, network blip). Re-fetch the
	// transcript so user doesn't see a stale "thinking" spinner forever.
	let lastMessageCount = $state(0);
	let lastChangeAt = $state(Date.now());
	$effect(() => {
		void messages.length;
		if (messages.length !== lastMessageCount) {
			lastMessageCount = messages.length;
			lastChangeAt = Date.now();
		}
	});
	$effect(() => {
		const id = setInterval(async () => {
			if (!auth.isAuthed) return;
			// Only act when we BELIEVE we're thinking but nothing has changed in 30s
			const stale = Date.now() - lastChangeAt > 30_000;
			if (!stale) return;
			// Re-derive isThinking inline (can't read $derived from setInterval safely)
			let thinking = sending || (serverStatusSeen && serverProcessing);
			if (!thinking && !serverStatusSeen) {
				for (let i = messages.length - 1; i >= 0; i--) {
					const m = messages[i];
					if (m.kind === 'unknown') continue;
					if (parseLocalCommand(m.content)) continue;
					if (hasToolResult(m.content)) continue;
					if (isInterruptText(m.content)) continue;
					if (m.role === 'assistant') { thinking = false; break; }
					if (m.role === 'user' && m.userType !== 'internal') { thinking = true; break; }
				}
			}
			if (!thinking) return;
			try {
				await reloadView();   // REPLACE, not merge — see reloadView()
			} catch { /* try again next tick */ }
		}, 15_000);
		return () => clearInterval(id);
	});

	// Foreground recovery. iOS suspends a backgrounded PWA — it kills the SSE
	// EventSource without firing onerror and freezes the reconnect timer. So after
	// being away (the user's ">5min, reply from the panel, then a lag before CC
	// shows running") the page can sit on a dead stream until the 30s backoff
	// fires. On return to visible (also pageshow/online) force the stream awake
	// (immediate reconnect if not verifiably live) and reloadView() to resync
	// history over REST — watcher-independent, so it works even if the 5-min idle
	// shutdown already disposed the server-side watcher.
	$effect(() => {
		if (typeof document === 'undefined') return;
		const onForeground = () => {
			if (document.visibilityState !== 'visible') return;
			if (!auth.isAuthed || readOnly) return;
			stream?.wake();
			reloadView().catch(() => {});
		};
		document.addEventListener('visibilitychange', onForeground);
		window.addEventListener('pageshow', onForeground);
		window.addEventListener('online', onForeground);
		return () => {
			document.removeEventListener('visibilitychange', onForeground);
			window.removeEventListener('pageshow', onForeground);
			window.removeEventListener('online', onForeground);
		};
	});

	const askUserQuestion = $derived.by<{ questions: AskQuestion[] } | null>(() => {
		if (!needsInput) return null;
		// Scan back to the most recent UNANSWERED AskUserQuestion tool_use. Do NOT
		// stop at the first assistant message: CC can append a trailing text/
		// thinking assistant record after the tool_use that shadows it — the
		// banner then fell back to the pane view (only CC's currently-shown
		// question), so multi-question prompts looked single and the user never
		// saw Q2…Qn → CC stuck waiting. Stop at the user's own prompt (anything
		// above it is already answered). Normalise option shapes defensively.
		for (let i = messages.length - 1; i >= 0; i--) {
			const m = messages[i];
			if (m.role === 'user' && m.userType !== 'internal' && typeof m.content === 'string') break;
			if (m.role !== 'assistant' || !Array.isArray(m.content)) continue;
			for (let j = m.content.length - 1; j >= 0; j--) {
				const block = m.content[j] as { type?: string; name?: string; input?: unknown };
				if (block?.type !== 'tool_use' || block.name !== 'AskUserQuestion') continue;
				let input: unknown = block.input;
				if (typeof input === 'string') {
					try { input = JSON.parse(input); } catch { input = null; }
				}
				const qs = (input as { questions?: unknown } | null)?.questions;
				if (!Array.isArray(qs) || qs.length === 0) continue;
				const norm: AskQuestion[] = qs.map((raw) => {
					const q = raw as { question?: unknown; header?: unknown; multiSelect?: unknown; options?: unknown };
					return {
						question: String(q?.question ?? ''),
						header: q?.header ? String(q.header) : undefined,
						multiSelect: !!q?.multiSelect,
						options: Array.isArray(q?.options)
							? q.options.map((o) =>
									typeof o === 'string'
										? { label: o }
										: {
												label: String((o as { label?: unknown })?.label ?? ''),
												description: (o as { description?: unknown })?.description
													? String((o as { description?: unknown }).description)
													: undefined
											}
								)
							: []
					};
				});
				return { questions: norm };
			}
		}
		return null;
	});

	// Single-question single-select AskUserQuestion → answerable in ONE tap via a
	// raw digit (live-verified claude 2.1.178: digit selects + submits, no Enter,
	// no re-ask). multiSelect or multi-question stay on the free-text compose path
	// (user reads the analysis + question carefully, then picks — project decision
	// 2026-06-16). Covers BOTH the JSONL tool_use path (askUserQuestion) and the
	// pending pane path (livePrompt.isAsk).
	const askSingleSelect = $derived.by<boolean>(() => {
		if (askUserQuestion) {
			const qs = askUserQuestion.questions;
			return qs.length === 1 && !qs[0]?.multiSelect;
		}
		if (livePrompt?.isAsk) {
			const secs = livePrompt.askSections ?? [];
			const qs = livePrompt.askQuestions ?? [];
			// One section = one question; treat as single-select unless the parsed
			// question is explicitly multiSelect.
			return secs.length === 1 && (qs.length === 0 || !qs[0]?.multi);
		}
		return false;
	});

	// Strip markdown table syntax from option labels: | cell separators and
	// runs of 3+ dashes (table separator rows like ---). Single - is preserved
	// (list items, en-dashes in prose). Applied to display AND composer insert
	// so the label is clean everywhere; CC matches answers by partial text.
	function stripTableChars(label: string): string {
		return label
			.replace(/\|/g, ' ')
			.replace(/-{3,}/g, ' ')
			.replace(/\s+/g, ' ')
			.trim();
	}

	function escapeRe(x: string) {
		return x.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
	}
	// Strict gate (user choice 2026-05-18): while answering a multi-question
	// AskUserQuestion, Send stays disabled until EVERY section/question has a
	// non-empty value — else an incomplete "Header:" line is sent and CC
	// replies "missing content".
	const askAnswer = $derived.by<{ active: boolean; missing: string[] }>(() => {
		if (askUserQuestion) {
			const qs = askUserQuestion.questions;
			if (qs.length <= 1) {
				return {
					active: true,
					missing: replyText.trim()
						? []
						: [qs[0]?.header || (qs[0]?.question ?? 'câu trả lời').slice(0, 24)]
				};
			}
			const missing: string[] = [];
			for (let n = 1; n <= qs.length; n++) {
				if (!new RegExp(`(^|\\n)[ \\t]*Q${n}:[ \\t]*\\S`).test(replyText))
					missing.push(qs[n - 1]?.header || `Q${n}`);
			}
			return { active: true, missing };
		}
		if (livePrompt?.isAsk) {
			const secs = livePrompt.askSections ?? [];
			if (secs.length === 0) return { active: false, missing: [] };
			const missing = secs.filter(
				(sec) => !new RegExp(`(^|\\n)[ \\t]*${escapeRe(sec)}:[ \\t]*\\S`).test(replyText)
			);
			return { active: true, missing };
		}
		return { active: false, missing: [] };
	});

	// Pending multi-question AskUserQuestion answered via free text (the
	// picker can't be driven remotely). Keep replyText as one "Header: value"
	// line per known section, in order, so CC's model maps it cleanly after
	// the bridge Esc-dismisses the picker on Send.
	// Single-select: tap replaces that section's value. multiSelect: tap
	// TOGGLES the value in/out of a comma list ("Header: A, B").
	function askCompose(sections: string[], header?: string, value?: string, multi = false) {
		if (sections.length === 0) {
			if (header && value) replyText = (replyText ? replyText + '\n' : '') + value;
		} else {
			const cur = new Map<string, string>();
			for (const line of replyText.split('\n')) {
				const m = line.match(/^\s*([^:]+):\s?(.*)$/);
				if (m && sections.includes(m[1].trim())) cur.set(m[1].trim(), m[2].trim());
			}
			if (header) {
				if (multi && value) {
					const parts = (cur.get(header) ?? '')
						.split(',').map((x) => x.trim()).filter(Boolean);
					const i = parts.indexOf(value);
					if (i >= 0) parts.splice(i, 1);
					else parts.push(value);
					cur.set(header, parts.join(', '));
				} else {
					cur.set(header, value ?? '');
				}
			}
			replyText = sections.map((sec) => `${sec}: ${cur.get(sec) ?? ''}`).join('\n');
		}
		tick().then(() => {
			taEl?.focus();
			if (taEl) {
				const p = replyText.length;
				try { taEl.setSelectionRange(p, p); } catch { /* ignore */ }
			}
		});
	}

	// Is `value` currently chosen for `header` (used to highlight tapped
	// options, esp. multiSelect)? Reads replyText so it stays reactive.
	function askPicked(header: string, value: string): boolean {
		for (const line of replyText.split('\n')) {
			const m = line.match(/^\s*([^:]+):\s?(.*)$/);
			if (m && m[1].trim() === header)
				return m[2].split(',').map((x) => x.trim()).includes(value);
		}
		return false;
	}

	function addOptionToComposer(qIdx: number, totalQuestions: number, label: string, multiSelect: boolean) {
		// Build "Q1: <label>" lines so the model can map answers back to questions.
		// Single-select: replace prior "Q<N>:" line for the same question.
		// Multi-select: append a new "Q<N>: <label>" line (allows multiple picks per question).
		// Single-question prompts: no prefix.
		const tag = totalQuestions > 1 ? `Q${qIdx + 1}: ` : '';
		const newLine = `${tag}${label}`;
		const lines = replyText.split('\n').filter((l) => l.length > 0);
		const filtered =
			totalQuestions > 1 && !multiSelect
				? lines.filter((l) => !l.startsWith(`Q${qIdx + 1}:`))
				: lines;
		filtered.push(newLine);
		replyText = filtered.join('\n');
		tick().then(() => {
			taEl?.focus();
			if (taEl) {
				const pos = replyText.length;
				try { taEl.setSelectionRange(pos, pos); } catch { /* ignore */ }
			}
		});
	}

	// Tracks the last time user successfully sent a reply. Used to:
	//   1) Dismiss any stale push notification on iPhone lockscreen
	//   2) Guard against late SSE status events that re-flip needsInput=true within
	//      a short window (Notification hook may have been in-flight before reply)
	let lastReplyAt = 0;
	const STALE_NOTIFY_GUARD_MS = 3000;

	async function dismissDeviceNotification() {
		// Direct close via registration.getNotifications — this works from a page context
		// without requiring serviceWorker.controller (which is often null on iOS Safari
		// for fresh PWA loads or after navigation). Fall back to postMessage in case
		// some quirky browser doesn't surface notifications via the page-side API.
		if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return;
		try {
			const reg = await navigator.serviceWorker.ready;
			const list = await reg.getNotifications({ tag: projectId });
			for (const n of list) n.close();
			navigator.serviceWorker.controller?.postMessage({
				type: 'closeNotifications',
				tag: projectId
			});
		} catch {
			/* SW not ready — ignore */
		}
	}

	// Select a numbered option in a CC interactive menu. Goes through /choice,
	// which sends the digit as a RAW keystroke — a CC menu reads number keys as
	// accelerators. The old path pasted the digit as a free-text reply, which the
	// menu interpreted as a cancel → "[Request interrupted]" → stuck "thinking".
	async function sendChoice(digit: number) {
		if (sending || isModeB) return;
		hapticTick();
		// Capture pendingTool NOW — it's derived from messages and will be null
		// after needsInput clears, so we snapshot it before flipping state.
		const learnTool = pendingTool;
		sending = true;
		needsInput = false;
		notificationMessage = null;
		try {
			await tryActivateOn409(() =>
				sessionsApi.choice(projectId, digit, requestedSessionUuid ?? undefined)
			);
			lastReplyAt = Date.now();
			dismissDeviceNotification();
			// Fire-and-forget: learn this approval so the hook can auto-allow
			// it next time. Non-fatal — failure just means it prompts again.
			if (learnTool) {
				sessionsApi
					.learnAutoAllow(projectId, learnTool.name, learnTool.details)
					.catch(() => { /* non-fatal */ });
			}
		} catch (e) {
			needsInput = true;
			error = e instanceof Error ? e.message : String(e);
		} finally {
			sending = false;
		}
	}

	function focusComposer() {
		taEl?.focus();
		taEl?.scrollIntoView({ block: 'end', behavior: 'smooth' });
	}

	// /btw parity — buffer the composer text for after the current CC turn ends
	// (or send immediately if CC is already idle). The button shows next to the
	// stop button while CC is processing AND the composer has trimmed text.
	async function queueText() {
		if (queueing) return;
		const text = replyText.trim();
		if (!text) return;
		queueing = true;
		try {
			await sessionsApi.queue(projectId, text, requestedSessionUuid ?? undefined);
			// Whatever mode (sent-immediate / queued), the textarea clears — the
			// user's "send this" action succeeded. SSE will pick up the assistant
			// response when CC actually consumes the queued message.
			replyText = '';
			// Drop focus on iOS so the keyboard slides away — match send flow.
			taEl?.blur();
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		} finally {
			queueing = false;
		}
	}

	// Fast-cancel a running CC turn (PWA parity with CC CLI's ESC keystroke).
	// Backend sends 2× Escape into tmux and clears processing state. Backend
	// 409s if CC is already idle — that's a no-op success from the UI's view.
	async function interruptTurn() {
		if (interrupting) return;
		interrupting = true;
		try {
			await sessionsApi.interrupt(projectId, requestedSessionUuid ?? undefined);
		} catch (e: unknown) {
			// 409 interrupt.not_processing = already idle; treat as silent success
			// (the button shouldn't have been showing anyway — SSE will catch up).
			const code = (e as { error?: { code?: string } })?.error?.code;
			if (code !== 'interrupt.not_processing') {
				error = e instanceof Error ? e.message : String(e);
			}
		} finally {
			interrupting = false;
		}
	}

	// Send Esc to CC to dismiss its AskUserQuestion picker, then focus composer
	// for a free-text reply. PWA can't drive the picker; this is the clean exit.
	async function dismissPickerAndFocus() {
		try {
			await tryActivateOn409(() =>
				sessionsApi.cancelPicker(projectId, requestedSessionUuid ?? undefined)
			);
			needsInput = false;
			notificationMessage = null;
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		}
		focusComposer();
	}

	// When needsInput flips true, read CC's CURRENTLY-VISIBLE menu from the tmux
	// pane so the banner renders the REAL options (Bash vs Edit-allow-all vs
	// N-option) instead of a static 1/2/3 that mis-mapped → wrong digit → cancel.
	// Small retry: the Notification hook can land a beat before CC paints the menu.
	$effect(() => {
		if (!needsInput) {
			livePrompt = null;
			livePromptPending = false;
			return;
		}
		let cancelled = false;
		// Fresh prompt: drop any prior menu so it can't linger, and mark
		// pending so the banner withholds the generic fallback until we know.
		livePrompt = null;
		livePromptPending = true;
		(async () => {
			try {
				for (let attempt = 0; attempt < 4 && !cancelled; attempt++) {
					try {
						const p = await sessionsApi.prompt(projectId);
						if (cancelled) return;
						livePrompt = p;
						if (p.found) return;
					} catch {
						/* transient — retry */
					}
					await new Promise((r) => setTimeout(r, 350));
				}
			} finally {
				// Loop ended (found, exhausted, or errored) — stop withholding
				// the fallback. Skip if cancelled: a new run will re-arm it.
				if (!cancelled) livePromptPending = false;
			}
		})();
		return () => {
			cancelled = true;
		};
	});

	// Slash-command / skill picker — fetched on first open, cached for the page lifetime.
	type CommandItem = { name: string; kind: string; description: string | null };
	let showCommandPicker = $state(false);
	let commands = $state<CommandItem[]>([]);
	let commandsLoaded = $state(false);
	let commandFilter = $state('');

	const filteredCommands = $derived.by(() => {
		const q = commandFilter.trim().toLowerCase();
		if (!q) return commands;
		return commands.filter(
			(c) =>
				c.name.toLowerCase().includes(q) ||
				(c.description ?? '').toLowerCase().includes(q)
		);
	});

	async function openCommandPicker() {
		showCommandPicker = true;
		commandFilter = '';
		if (commandsLoaded) return;
		try {
			const r = await sessionsApi.commands(projectId);
			commands = r.commands;
			commandsLoaded = true;
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		}
	}

	function pickCommand(name: string) {
		// Insert at cursor if composer has focus + selection; otherwise prefix the whole text.
		const insert = `/${name} `;
		if (!replyText.trim()) {
			replyText = insert;
		} else {
			replyText = `${insert}${replyText}`;
		}
		showCommandPicker = false;
		// Defer focus until DOM update so cursor lands at end of the inserted command.
		tick().then(() => {
			taEl?.focus();
			if (taEl) {
				const pos = insert.length;
				try { taEl.setSelectionRange(pos, pos); } catch { /* ignore */ }
			}
		});
	}

	// ── "@" file-mention picker ──────────────────────────────────────────
	// Type "@" in the composer to fuzzy-pick a project file; selecting inserts
	// "@<relative/path> " which CC resolves natively (it Reads the file on
	// submit — verified live). Inline autocomplete above the composer.
	let atQuery = $state<string | null>(null); // query after the active "@", or null
	let atTokenStart = $state(-1); // index of the "@" within replyText
	let atFiles = $state<string[]>([]);
	let atLoading = $state(false);
	let atActiveIdx = $state(0);
	let atDebounce: ReturnType<typeof setTimeout> | null = null;
	const atOpen = $derived(atQuery !== null);

	// Detect an active "@token" ending at the caret: an "@" at string start or
	// after whitespace, then a run of non-space/non-"@" chars up to the cursor.
	function detectMention() {
		const el = taEl;
		if (!el) return closeMention();
		const pos = el.selectionStart ?? replyText.length;
		const m = replyText.slice(0, pos).match(/(?:^|\s)@([^\s@]*)$/);
		if (!m) return closeMention();
		atTokenStart = pos - m[1].length - 1; // the "@"
		if (atQuery !== m[1]) {
			atQuery = m[1];
			atActiveIdx = 0;
			scheduleFetch(m[1]);
		}
	}

	function scheduleFetch(q: string) {
		if (atDebounce) clearTimeout(atDebounce);
		atDebounce = setTimeout(async () => {
			atLoading = true;
			try {
				atFiles = await sessionsApi.files(projectId, q);
			} catch {
				atFiles = [];
			} finally {
				atLoading = false;
				atActiveIdx = 0;
			}
		}, 150);
	}

	function closeMention() {
		atQuery = null;
		atTokenStart = -1;
		atFiles = [];
		if (atDebounce) {
			clearTimeout(atDebounce);
			atDebounce = null;
		}
	}

	function pickFile(path: string) {
		const caret = taEl?.selectionStart ?? replyText.length;
		const start = atTokenStart >= 0 ? atTokenStart : caret;
		const before = replyText.slice(0, start);
		const after = replyText.slice(caret);
		const insert = `@${path} `;
		replyText = before + insert + after;
		const newPos = before.length + insert.length;
		closeMention();
		// Defer so the caret lands after the inserted "@path ".
		tick().then(() => {
			taEl?.focus();
			try {
				taEl?.setSelectionRange(newPos, newPos);
			} catch {
				/* ignore */
			}
		});
	}

	// Token usage from the latest assistant message — drives the header badge so user
	// knows when to invoke /compact. CC writes per-turn usage in JSONL; the LATEST
	// turn's input + cache_creation + cache_read ≈ current conversation context size.
	const lastUsage = $derived.by(() => {
		for (let i = messages.length - 1; i >= 0; i--) {
			const u = messages[i].usage;
			if (u) return u;
		}
		return null;
	});

	function modelContextLimit(model: string | null | undefined): number {
		const m = (model ?? '').toLowerCase();
		if (m.includes('opus') || m.includes('sonnet') || m.includes('1m')) return 1_000_000;
		if (m.includes('haiku')) return 200_000;
		return 200_000;
	}

	const contextTokens = $derived(
		lastUsage
			? lastUsage.inputTokens + lastUsage.cacheCreationInputTokens + lastUsage.cacheReadInputTokens
			: 0
	);
	const contextLimit = $derived(modelContextLimit(lastUsage?.model));
	const usagePercent = $derived(
		contextLimit > 0 ? Math.round((contextTokens / contextLimit) * 1000) / 10 : 0
	);

	function formatTokens(n: number): string {
		if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
		if (n >= 10_000) return `${Math.round(n / 1000)}k`;
		if (n >= 1000) return `${(n / 1000).toFixed(1)}k`;
		return String(n);
	}

	function compactNow() {
		// Fire-and-forget /compact via the same paste-buffer path as a normal reply.
		sessionsApi
			.reply(projectId, '/compact', requestedSessionUuid ?? undefined)
			.catch((e) => {
				error = e instanceof Error ? e.message : String(e);
			});
	}

	// Partial patch: only the sent tier flips; backend returns the full new state.
	async function setAutoAllow(patch: Partial<typeof autoAllow>) {
		if (autoAllowBusy) return;
		autoAllowBusy = true;
		try {
			autoAllow = await sessionsApi.setAutoAllow(projectId, patch);
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		} finally {
			autoAllowBusy = false;
		}
	}

	// Restart CC tmux for this project (fires when user taps "Khởi động lại")
	let restarting = $state(false);
	async function restartCc() {
		// Belt-and-braces: never restart while PC owns the session (two-process
		// guard). UI already hides the button in Mode B; backend also 409s.
		if (restarting || isModeB) return;
		restarting = true;
		error = null;
		try {
			const r = await sessionsApi.restart(projectId);
			// Optimistic UI: mark running, allow some time for CC to write first record
			running = true;
			// Re-fetch transcript so the new session's records show up; SSE will follow up
			setTimeout(async () => {
				try {
					const t = await sessionsApi.transcript(projectId);
					seenUuids.clear();
					messages.length = 0;
					for (const m of t.messages) appendMessage(m);
				} catch {
					/* SSE will catch up */
				}
			}, 1500);
			console.log('[restart]', r);
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		} finally {
			restarting = false;
		}
	}
</script>

<!-- Auto-allow toggle rows (shared markup for the shield popover). -->
{#snippet sw(on: boolean, warn: boolean)}
	<span
		class="relative inline-block w-9 h-5 rounded-full shrink-0 transition {on
			? warn
				? 'bg-[var(--color-warning)]'
				: 'bg-[var(--color-accent)]'
			: 'bg-[var(--color-border)]'}"
	>
		<span
			class="absolute top-0.5 h-4 w-4 rounded-full bg-white transition-all {on
				? 'left-[18px]'
				: 'left-0.5'}"
		></span>
	</span>
{/snippet}

{#snippet aaRow(title: string, desc: string, on: boolean, warn: boolean, toggle: () => void)}
	<button
		type="button"
		disabled={autoAllowBusy}
		onclick={toggle}
		aria-pressed={on}
		class="w-full flex items-start gap-2.5 p-2 rounded-lg hover:bg-[var(--color-bg)]/60 text-left disabled:opacity-50 transition"
	>
		{@render sw(on, warn)}
		<span class="flex-1 min-w-0">
			<span class="font-medium text-[var(--color-text)]">{title}</span>
			<span class="block text-[11px] leading-snug text-[var(--color-text-dim)]">{desc}</span>
		</span>
	</button>
{/snippet}

{#snippet aaSub(title: string, desc: string, on: boolean, toggle: () => void)}
	<button
		type="button"
		disabled={autoAllowBusy || !autoAllow.autonomy}
		onclick={toggle}
		aria-pressed={on}
		class="w-full flex items-start gap-2.5 p-2 pl-3 rounded-lg hover:bg-[var(--color-bg)]/60 text-left transition disabled:cursor-not-allowed {autoAllow.autonomy
			? ''
			: 'opacity-40'}"
	>
		{@render sw(on, true)}
		<span class="flex-1 min-w-0">
			<span class="font-medium text-[var(--color-text)]">{title}</span>
			<span class="block text-[11px] leading-snug text-[var(--color-text-dim)]">{desc}</span>
		</span>
	</button>
{/snippet}

<header
	class="sticky top-0 z-10 bg-[var(--color-bg)]/95 backdrop-blur border-b border-[var(--color-border)] px-3 py-2 flex items-center gap-2"
>
	<a
		href="/"
		class="shrink-0 w-8 h-8 inline-flex items-center justify-center rounded-full text-[var(--color-accent)] hover:bg-[var(--color-surface)] active:scale-95 transition"
		aria-label="Về dashboard"
	>
		<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
			 stroke-linecap="round" stroke-linejoin="round" class="w-5 h-5">
			<path d="M19 12H5" /><path d="M12 19l-7-7 7-7" />
		</svg>
	</a>
	<span
		class="inline-block w-1.5 h-1.5 rounded-full shrink-0 {running ? 'bg-[var(--color-success)]' : 'bg-[var(--color-text-dim)]'}"
		title={running ? 'CC đang chạy' : 'CC đã dừng'}
	></span>
	<h1 class="min-w-0 truncate text-sm font-semibold">{projectId}</h1>
	<span class="flex-1"></span>
	<!-- Auto-allow SAFE read-only prompts: shield fills + turns accent when ON.
	     Safe cmds (read-only tools + read-only Bash) then run with no permission
	     prompt; everything write/network/build still prompts. Per-project.
	     Hidden on read-only history views (state isn't loaded there, and there's
	     no live session to act on). -->
	{#if !readOnly}
	<div class="relative shrink-0">
		<button
			type="button"
			onclick={() => (autoAllowOpen = !autoAllowOpen)}
			title="Tự duyệt lệnh (chạm để mở)"
			aria-label="Tự duyệt lệnh"
			aria-expanded={autoAllowOpen}
			class="w-8 h-8 inline-flex items-center justify-center rounded-full hover:bg-[var(--color-surface)] active:scale-95 transition {autoAllow.autonomy
				? 'text-[var(--color-warning)]'
				: aaActive
					? 'text-[var(--color-accent)]'
					: 'text-[var(--color-text-dim)]'}"
		>
			<svg viewBox="0 0 24 24" fill={aaActive ? 'currentColor' : 'none'} stroke="currentColor"
				 stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="w-5 h-5">
				<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" />
				{#if autoAllow.autonomy}
					<!-- exclamation = "widened beyond read-only" -->
					<path d="M12 8v4" stroke="var(--color-bg)" />
					<path d="M12 16h.01" stroke="var(--color-bg)" />
				{:else if autoAllow.enabled}
					<path d="M9 12l2 2 4-4" stroke="var(--color-bg)" />
				{/if}
			</svg>
		</button>
		{#if autoAllowOpen}
			<!-- click-away backdrop -->
			<button
				type="button"
				aria-label="Đóng"
				class="fixed inset-0 z-20 cursor-default"
				onclick={() => (autoAllowOpen = false)}
			></button>
			<div
				class="absolute right-0 top-9 z-30 w-[19rem] max-w-[88vw] rounded-xl bg-[var(--color-surface)] border border-[var(--color-border)] shadow-2xl p-1.5 text-[13px]"
			>
				{@render aaRow(
					'Tự duyệt lệnh an toàn',
					'Tool chỉ-đọc + Bash chỉ-đọc (kể cả lệnh nối && | ;). Không ghi / mạng / build.',
					autoAllow.enabled,
					false,
					() => setAutoAllow({ enabled: !autoAllow.enabled })
				)}
				{@render aaRow(
					'Tự chủ (build/test + git add/commit)',
					'⚠ build/test THỰC THI mã của project. Là tuyên bố tin cậy, không phải “an toàn”.',
					autoAllow.autonomy,
					true,
					() => setAutoAllow({ autonomy: !autoAllow.autonomy })
				)}
				<div class="border-t border-[var(--color-border)]/60 my-1"></div>
				{@render aaSub(
					'Cho git push',
					'Đẩy lên remote (không cho --force).',
					autoAllow.push,
					() => setAutoAllow({ push: !autoAllow.push })
				)}
				{@render aaSub(
					'Cho cài gói',
					'npm/pnpm install, dotnet restore (chạy script + tải mạng).',
					autoAllow.install,
					() => setAutoAllow({ install: !autoAllow.install })
				)}
			</div>
		{/if}
	</div>
	{/if}
	{#if lastUsage}
		<!-- Context size badge — tap to /compact when high. Color escalates with
		     usage. Shares .hdr-badge sizing with the model label; .btn-icon escapes
		     the global 44px button min-height (app.css) so it stays 32px tall. -->
		<button
			type="button"
			onclick={compactNow}
			title="Tap to /compact (giảm context)"
			class="hdr-badge btn-icon transition active:scale-95
			{usagePercent >= 80
				? 'bg-[var(--color-danger)]/20 border-[var(--color-danger)]/50 text-[var(--color-danger)]'
				: usagePercent >= 50
					? 'bg-[var(--color-warning)]/20 border-[var(--color-warning)]/50 text-[var(--color-warning)]'
					: 'bg-[var(--color-surface)] border-[var(--color-border)]/50 text-[var(--color-text-dim)]'}"
		>
			{formatTokens(contextTokens)} · {usagePercent}%
		</button>
	{/if}
	{#if needsInput}
		<span class="text-[10px] leading-none text-[var(--color-warning)] shrink-0">• cần trả lời</span>
	{/if}
</header>

{#if !running && !loading && !isModeB}
	<!-- Restart affordance — Mode A / none only. In Mode B (owner=pc) the tmux
	     window is killed BY DESIGN (XOR, ADR-017) so !running is NORMAL, not an
	     error: a restart here spawns a 2nd `claude` on the UID claude-vscode is
	     using (the "[Request interrupted]" interleave). OwnerBanner 🖥 PC +
	     "Tiếp quản" is the safe path back; backend also hard-refuses
	     (restart.owned_by_pc). -->
	<div
		class="sticky top-[40px] z-10 bg-[var(--color-text-dim)]/15 backdrop-blur border-b border-[var(--color-text-dim)]/40 px-4 py-3 text-sm flex items-center justify-between gap-3"
	>
		<span class="text-[var(--color-text-dim)]">CC đã dừng. Khởi động lại để tiếp tục session.</span>
		<button
			type="button"
			onclick={restartCc}
			disabled={restarting}
			class="min-h-[38px] px-4 py-2 rounded-lg bg-[var(--color-success)]/30 border border-[var(--color-success)]/60 text-[var(--color-success)] font-semibold disabled:opacity-50 active:scale-95 transition"
		>
			{restarting ? 'Đang khởi động…' : 'Khởi động lại'}
		</button>
	</div>
{/if}

<!-- Prompt card body — rendered inline at the bottom of the transcript via
     {@render needsInputCard()} inside the scroll area. Declared as a snippet so
     the markup lives once but paints in the chat flow, not above it. -->
{#snippet needsInputCard()}
	<div class="space-y-2 text-sm rounded-xl bg-[var(--color-warning)]/10 border border-[var(--color-warning)]/40 px-4 py-3">
		<div class="flex items-center gap-2 text-[var(--color-warning)] font-semibold">
			<span>⚠️</span>
			<span>Claude đang chờ phản hồi</span>
		</div>
		{#if notificationMessage}
			<!-- Hook message from CC — usually the only signal for permission prompts
			     (e.g. "Claude needs your permission to use Bash"). Stronger than the parsed
			     assistant text, so render first/prominent. -->
			<div class="text-[var(--color-text)] whitespace-pre-wrap break-words text-[13.5px] leading-relaxed">
				{notificationMessage}
			</div>
		{/if}
		{#if askUserQuestion}
			<!-- AskUserQuestion runs as an interactive picker in CC's TUI (arrow nav + Enter
			     for each question, then Submit tab). PWA can't reliably drive that via
			     tmux paste-buffer — text replies get partially consumed by the picker and
			     leave it stuck mid-question. Render READ-ONLY so user knows the questions,
			     and instruct them how to answer cleanly. -->
			<div class="space-y-2">
				<div class="text-[11.5px] text-[var(--color-text)] bg-[var(--color-warning)]/10 border border-[var(--color-warning)]/40 rounded-md p-2 leading-snug">
					{#if askSingleSelect}<b>1 câu</b> — chạm đáp án để <b>gửi luôn</b> (không cần bấm Gửi). Muốn trả lời khác thì dùng "Huỷ picker" bên dưới.{:else}<b>{askUserQuestion.questions.length} câu hỏi</b> — chạm chọn cho <b>TỪNG câu</b> (điền vào ô soạn) rồi bấm <b>Gửi</b>. Phải trả lời đủ cả {askUserQuestion.questions.length} câu thì CC mới tiếp tục.{/if}
				</div>
				<!-- Bounded scroll: a long N-question list must not push the
				     cancel/Send actions off-screen. Header+hint+Huỷ-picker stay
				     OUTSIDE this box → always visible & tappable. -->
				<div class="max-h-[44vh] overflow-y-auto overscroll-contain space-y-2 -mx-1 px-1">
				{#each askUserQuestion.questions as q, qi (qi)}
					{@const total = askUserQuestion.questions.length}
					<div class="rounded-md bg-[var(--color-bg)]/60 border border-[var(--color-border)]/40 p-2">
						<div class="flex items-baseline gap-2 mb-1.5">
							<span class="font-mono text-[10px] text-[var(--color-warning)] shrink-0">Q{qi + 1}/{total}</span>
							<div class="text-[12.5px] font-medium text-[var(--color-text)] leading-snug">
								{q.question}{#if q.multiSelect} <span class="text-[10px] font-normal text-[var(--color-text-dim)]">(chọn nhiều)</span>{/if}
							</div>
						</div>
						<div class="flex flex-col gap-1.5">
							{#each q.options as opt, oi (oi)}
								<button
									type="button"
									onclick={() => (askSingleSelect ? sendChoice(oi + 1) : addOptionToComposer(qi, total, stripTableChars(opt.label), !!q.multiSelect))}
									disabled={sending}
									class="min-h-[38px] px-3 py-2 rounded-lg bg-[var(--color-surface)] border border-[var(--color-border)] hover:border-[var(--color-accent)]/60 text-left text-[var(--color-text)] disabled:opacity-50 active:scale-[0.99] transition flex items-start gap-3"
								>
									<span class="text-[var(--color-accent)]/70 font-mono text-[12px] shrink-0 mt-0.5">{oi + 1}.</span>
									<span class="flex-1 min-w-0">
										<span class="block leading-snug">{stripTableChars(opt.label)}</span>
										{#if opt.description}
											<span class="block text-[11px] leading-snug text-[var(--color-text-dim)]">{opt.description}</span>
										{/if}
									</span>
								</button>
							{/each}
						</div>
					</div>
				{/each}
				</div>
				<div class="text-[11px] text-[var(--color-text-dim)] leading-snug">
					Chạm đáp án → điền "Q1: …", "Q2: …" vào ô soạn (sửa được). Hoặc gõ tự do. Bấm Gửi: bridge Esc picker của CC rồi gửi — nhớ trả lời đủ tất cả câu.
				</div>
				{#if askAnswer.active && askAnswer.missing.length}
					<div class="text-[12px] text-[var(--color-warning)] bg-[var(--color-warning)]/10 border border-[var(--color-warning)]/40 rounded-md px-2 py-1.5 leading-snug">
						⚠️ Còn thiếu: <b>{askAnswer.missing.join(', ')}</b> — điền/chạm hết rồi mới Gửi được.
					</div>
				{/if}
				<button
					type="button"
					onclick={dismissPickerAndFocus}
					disabled={sending}
					class="min-h-[38px] w-full px-3 py-2 rounded-lg bg-[var(--color-warning)]/20 border border-[var(--color-warning)]/50 text-[var(--color-warning)] text-[13px] active:scale-[0.99] transition"
				>
					Huỷ picker + gõ trả lời tự do ↓
				</button>
			</div>
		{:else if pendingTool && pendingTool.details}
			<!-- Show the actual tool_use input — the bash command, file path, etc.
			     Critical for permission decisions: "Allow Bash?" with no command shown
			     is a security smell. Monospace + scrollable for long commands. -->
			<div class="rounded-md bg-[var(--color-bg)]/80 border border-[var(--color-border)]/50 px-2.5 py-1.5 overflow-x-auto">
				<div class="text-[10px] uppercase tracking-wide text-[var(--color-text-dim)] mb-0.5">
					{pendingTool.label}
				</div>
				<pre class="font-mono text-[12.5px] text-[var(--color-text)] whitespace-pre-wrap break-all max-h-40 overflow-y-auto m-0">{pendingTool.details}</pre>
			</div>
		{/if}
		{#if promptInfo?.question && promptInfo.question !== notificationMessage && !askUserQuestion && !livePromptPending}
			<div class="text-[var(--color-text-dim)] whitespace-pre-wrap break-words text-[12.5px] leading-relaxed">
				{promptInfo.question}
			</div>
		{/if}
		{#if askUserQuestion}
			<!-- AskUserQuestion form already rendered above with tap-to-fill option buttons.
			     No need for additional choice buttons; user fills composer via taps + Send. -->
		{:else if livePrompt?.isAsk}
			{@const secs = livePrompt.askSections ?? []}
			{@const ans = livePrompt.askAnswered ?? 0}
			{@const curSec = secs[ans] ?? secs[secs.length - 1] ?? ''}
			{@const curTag = curSec || ('Câu ' + (ans + 1))}
			<!-- Pending multi-question AskUserQuestion: not in JSONL yet, TUI
			     shows one question at a time, picker undriveable remotely
			     (ADR-015/16). User composes a per-section answer; on Send the
			     bridge Esc-dismisses the picker then pastes it (ReplyEndpoints).
			     Tapping an option fills that section's line. No "cancel". -->
			<div class="flex flex-col gap-2 pt-1">
				<div class="text-[12px] text-[var(--color-text)] bg-[var(--color-warning)]/10 border border-[var(--color-warning)]/40 rounded-md p-2 leading-snug">
					📋 CC đang hỏi <b>{secs.length} câu</b>: <b>{secs.join(' / ')}</b>{#if ans > 0} · đã trả lời {ans}/{secs.length}{/if}.
					<br />{#if (livePrompt.askQuestions?.length ?? 0) > 0}Chạm chọn đáp án cho <b>TỪNG câu</b> bên dưới (hoặc gõ), rồi bấm <b>Gửi</b>{:else}Chạm chọn / gõ cho <b>từng phần</b> rồi bấm <b>Gửi</b>{/if} — bridge tự Esc picker, gửi đủ cả {secs.length} câu (model map theo nhãn). Không cần “huỷ”.
				</div>
				{#if livePrompt.askContext}
					<!-- CC's analysis prose above the picker. Not in JSONL until the
					     question is answered, so the bridge scrapes it from the pane
					     (PromptEndpoint.ExtractAskContext) — gives the context the
					     questions refer to so the user can answer informed. -->
					<details open class="text-[12px] rounded-md bg-[var(--color-bg)]/60 border border-[var(--color-border)]/40">
						<summary class="cursor-pointer select-none px-2 py-1.5 text-[var(--color-text-dim)] font-medium">
							🧠 Bối cảnh từ CC
						</summary>
						<div class="px-2 pb-2 whitespace-pre-wrap break-words text-[var(--color-text)] leading-relaxed max-h-[30vh] overflow-y-auto overscroll-contain">{livePrompt.askContext}</div>
					</details>
				{/if}
				{#if (livePrompt.askQuestions?.length ?? 0) > 0}
					<!-- Cuộn riêng vùng câu hỏi: N câu × nhiều option rất cao;
					     giới hạn chiều cao để banner không tràn viewport, header/
					     guard/nút vẫn cố định ngoài khung cuộn. -->
					<div class="max-h-[44vh] overflow-y-auto overscroll-contain space-y-2 -mx-1 px-1">
					{#each livePrompt.askQuestions ?? [] as q (q.index)}
						{@const qSec = secs[q.index - 1] ?? ('Câu ' + q.index)}
						<div class="rounded-md bg-[var(--color-bg)]/60 border border-[var(--color-border)]/40 p-2">
							<div class="text-[12.5px] font-medium text-[var(--color-text)] mb-1.5">
								<span class="font-mono text-[10px] text-[var(--color-warning)] mr-1">Q{q.index}/{secs.length}</span>{q.question}{#if q.multi} <span class="text-[10px] font-normal text-[var(--color-text-dim)]">(chọn nhiều)</span>{/if}
							</div>
							{#if q.options.length > 0}
								<div class="flex flex-col gap-1.5">
									{#each q.options as opt (opt.num)}
										<button
											type="button"
											onclick={() => (askSingleSelect ? sendChoice(opt.num) : askCompose(secs, qSec, stripTableChars(opt.label), q.multi))}
											disabled={sending}
											class="min-h-[38px] px-3 py-2 rounded-lg border text-left disabled:opacity-50 active:scale-[0.99] transition flex items-start gap-3 {askPicked(qSec, stripTableChars(opt.label)) ? 'bg-[var(--color-accent)]/15 border-[var(--color-accent)] text-[var(--color-text)]' : 'bg-[var(--color-surface)] border-[var(--color-border)] hover:border-[var(--color-accent)]/60 text-[var(--color-text)]'}"
										>
											<span class="font-mono text-[12px] text-[var(--color-accent)]/70 shrink-0 mt-0.5">{opt.num}.</span>
											<span class="flex-1 break-words">{stripTableChars(opt.label)}</span>
										</button>
									{/each}
								</div>
							{/if}
						</div>
					{/each}
					</div>
				{:else}
					{#if livePrompt.question}
						<div class="text-[12.5px] font-medium text-[var(--color-text)]">{livePrompt.question}</div>
					{/if}
					{#if livePrompt.options.length > 0}
						<div class="flex flex-col gap-1.5">
							{#each livePrompt.options as opt (opt.num)}
								<button
									type="button"
									onclick={() => (askSingleSelect ? sendChoice(opt.num) : askCompose(secs, curTag, stripTableChars(opt.label)))}
									disabled={sending}
									class="min-h-[38px] px-3 py-2 rounded-lg bg-[var(--color-surface)] border border-[var(--color-border)] hover:border-[var(--color-accent)]/60 text-left text-[var(--color-text)] disabled:opacity-50 active:scale-[0.99] transition flex items-start gap-3"
								>
									<span class="font-mono text-[12px] text-[var(--color-accent)]/70 shrink-0 mt-0.5">{opt.num}.</span>
									<span class="flex-1 break-words">{stripTableChars(opt.label)}</span>
								</button>
							{/each}
						</div>
					{/if}
				{/if}
				{#if askAnswer.active && askAnswer.missing.length}
					<div class="text-[12px] text-[var(--color-warning)] bg-[var(--color-warning)]/10 border border-[var(--color-warning)]/40 rounded-md px-2 py-1.5 leading-snug">
						⚠️ Còn thiếu: <b>{askAnswer.missing.join(', ')}</b> — điền/chạm hết rồi mới Gửi được.
					</div>
				{/if}
				<button
					type="button"
					onclick={() => askCompose(secs)}
					disabled={sending}
					class="min-h-[38px] w-full px-3 py-2 rounded-lg bg-[var(--color-surface)] border border-dashed border-[var(--color-border)] text-[var(--color-text-dim)] text-[13px] active:scale-[0.99] transition"
				>
					Điền khung {secs.length} câu để gõ
				</button>
			</div>
		{:else if livePrompt?.found && livePrompt.options.length > 0}
			<!-- REAL menu, read live from CC's tmux pane (PromptEndpoint): exact
			     numbers + labels for THIS prompt (Bash / Edit-allow-all / 2-opt /
			     N-opt). Tap → /choice raw-digit. No static 1/2/3 guess. -->
			<div class="flex flex-col gap-2 pt-1">
				{#each livePrompt.options as opt (opt.num)}
					<button
						type="button"
						onclick={() => sendChoice(opt.num)}
						disabled={sending}
						class="min-h-[38px] px-3 py-2 rounded-lg bg-[var(--color-surface)] border border-[var(--color-border)] hover:border-[var(--color-accent)]/60 text-left text-[var(--color-text)] disabled:opacity-50 active:scale-[0.99] transition flex items-start gap-3"
					>
						<span class="font-mono text-[var(--color-accent)] font-semibold min-w-6">{opt.num}.</span>
						<span class="flex-1 break-words">{stripTableChars(opt.label)}</span>
					</button>
				{/each}
				<button
					type="button"
					onclick={dismissPickerAndFocus}
					class="min-h-[38px] px-3 py-2 rounded-lg border border-dashed border-[var(--color-border)] text-[var(--color-text-dim)] text-[13px] active:scale-[0.99] transition"
				>
					Từ chối / trả lời khác (Esc) ↓
				</button>
				<div class="text-[11px] text-[var(--color-text-dim)]/80 pt-0.5 leading-snug">
					Nút lấy đúng từ menu CC đang hiển thị (số + nhãn thật).
				</div>
			</div>
		{:else if livePromptPending}
			<!-- Live /prompt fetch in flight — withhold the generic fallback so
			     it doesn't flash for ~1s then get swapped by the real menu. -->
			<div class="flex items-center gap-2 pt-1 text-[12.5px] text-[var(--color-text-dim)]" aria-live="polite">
				<span class="w-3.5 h-3.5 rounded-full border-2 border-[var(--color-text-dim)]/40 border-t-[var(--color-accent)] animate-spin shrink-0"></span>
				<span>Đang đọc menu CC…</span>
			</div>
		{:else if isPermissionPrompt}
			<!-- SAFE fallback: live menu not parsed yet (capture failed / not a
			     numbered menu). Only the INVARIANTS — option 1 is "Yes" on every CC
			     menu; Esc cancels on every CC menu. NEVER a static 2/3 (their
			     meaning differs per prompt → wrong-option → interrupt). -->
			<div class="flex flex-col gap-2 pt-1">
				<button
					type="button"
					onclick={() => sendChoice(1)}
					disabled={sending}
					class="min-h-[38px] px-3 py-2 rounded-lg bg-[var(--color-success)]/15 border border-[var(--color-success)]/50 text-left disabled:opacity-50 active:scale-[0.99] transition flex items-start gap-3"
				>
					<span class="font-mono text-[var(--color-success)] font-semibold min-w-6">1.</span>
					<span class="flex-1 break-words text-[var(--color-text)]">Cho phép (Yes)</span>
				</button>
				<button
					type="button"
					onclick={dismissPickerAndFocus}
					class="min-h-[38px] px-3 py-2 rounded-lg border border-dashed border-[var(--color-border)] text-[var(--color-text-dim)] text-[13px] active:scale-[0.99] transition"
				>
					Từ chối / trả lời khác (Esc) ↓
				</button>
				<div class="text-[11px] text-[var(--color-text-dim)]/80 pt-0.5 leading-snug">
					Chưa đọc được menu thật → chỉ 1=Yes (bất biến) + Esc. Đợi 1–2 giây rồi mở lại nếu cần lựa chọn khác.
				</div>
			</div>
		{:else if promptInfo && promptInfo.options.length > 0 && !promptInfo.isPicker}
			<!-- CC explicit numbered options (e.g. permission prompts). Tap sends the digit;
			     button label shows what that digit means. Plus an "Other" escape to composer. -->
			<div class="flex flex-col gap-2 pt-1">
				{#each promptInfo.options as opt (opt.num)}
					<button
						type="button"
						onclick={() => sendChoice(opt.num)}
						disabled={sending}
						class="min-h-[38px] px-3 py-2 rounded-lg bg-[var(--color-surface)] border border-[var(--color-border)] hover:border-[var(--color-accent)]/60 text-left text-[var(--color-text)] disabled:opacity-50 active:scale-[0.99] transition flex items-start gap-3"
					>
						<span class="font-mono text-[var(--color-accent)] font-semibold min-w-6">{opt.num}.</span>
						<span class="flex-1 break-words">{stripTableChars(opt.label)}</span>
					</button>
				{/each}
				<button
					type="button"
					onclick={focusComposer}
					class="min-h-[38px] px-3 py-2 rounded-lg border border-dashed border-[var(--color-border)] text-[var(--color-text-dim)] text-[13px] active:scale-[0.99] transition"
				>
					Khác — gõ trả lời tự do ↓
				</button>
				<div class="text-[11px] text-[var(--color-text-dim)]/80 pt-0.5 leading-snug">
					Tap gửi đúng digit (1/2/…), không gửi text. CC chỉ nhận digit cho prompt loại này.
				</div>
			</div>
		{:else if promptInfo && promptInfo.options.length > 0 && promptInfo.isPicker}
			<!-- Multi-select / interactive picker: digit only toggles checkbox; Enter toggles too,
			     doesn't submit. PWA can't drive this from one tap. Render read-only + Other. -->
			<div class="flex flex-col gap-1 pt-1">
				{#each promptInfo.options as opt (opt.num)}
					<div
						class="px-3 py-1.5 rounded-md bg-[var(--color-surface)]/50 border border-[var(--color-border)]/40 text-[var(--color-text-dim)] flex items-start gap-3 text-[13px]"
					>
						<span class="font-mono text-[var(--color-accent)]/70 font-semibold min-w-6">{opt.num}.</span>
						<span class="flex-1 break-words">{stripTableChars(opt.label)}</span>
					</div>
				{/each}
				<button
					type="button"
					onclick={focusComposer}
					class="mt-2 min-h-[38px] px-3 py-2 rounded-lg border border-dashed border-[var(--color-border)] text-[var(--color-text-dim)] text-[13px] active:scale-[0.99] transition"
				>
					Trả lời tự do ↓
				</button>
				<div class="text-[11px] text-[var(--color-text-dim)]/80 pt-0.5 leading-snug">
					Multi-select picker — Phase 1 chưa drive được từ PWA. Trả lời tự do bên dưới hoặc thao tác CC trên host.
				</div>
			</div>
		{:else}
			<!-- No structured options parsed (free-text question or assistant tool_use only).
			     Don't guess Yes/No — just guide user to composer. -->
			<button
				type="button"
				onclick={focusComposer}
				class="mt-1 w-full min-h-[38px] px-3 py-2 rounded-lg border border-dashed border-[var(--color-border)] text-[var(--color-text-dim)] text-[13px] active:scale-[0.99] transition"
			>
				Gõ trả lời ↓
			</button>
		{/if}
	</div>
{/snippet}


<!-- Chat scroll area. Composer is position:fixed (back), so reserve padding-bottom
     = composerHeight + 12 to keep content from being covered. Padding-bottom
     auto-updates when composer height changes (textarea grows on type). -->
<div
	bind:this={scrollEl}
	onscroll={onScroll}
	use:pullToRefresh={{ onRefresh: refreshTranscript }}
	class="flex-1 min-h-0 overflow-y-auto px-3 pt-4 space-y-4"
	style="padding-bottom: {composerHeight + 12}px"
>
	{#if loading}
		<p class="text-[var(--color-text-dim)] text-center py-16 text-sm">Đang tải transcript…</p>
	{:else if error}
		<div class="rounded-lg bg-[var(--color-danger)]/20 border border-[var(--color-danger)]/50 p-3 text-sm">
			{error}
		</div>
	{:else if messages.length === 0}
		<div class="text-center py-20 px-6">
			<div class="text-5xl mb-3 opacity-60">💬</div>
			<p class="text-[var(--color-text-dim)] text-sm">Chưa có message. Gửi tin nhắn để bắt đầu.</p>
		</div>
	{:else}
		{#if hasEarlier}
			<button
				type="button"
				onclick={loadFullHistory}
				disabled={loadingEarlier}
				class="mx-auto block text-xs text-[var(--color-text-dim)] border border-[var(--color-border)] rounded-full px-4 py-1.5 active:scale-95 transition disabled:opacity-50"
			>
				{loadingEarlier ? 'Đang tải…' : `▲ Tải toàn bộ lịch sử (${totalMessages} tin)`}
			</button>
		{:else if windowHidden > 0}
			<button
				type="button"
				onclick={showEarlier}
				class="mx-auto block text-xs text-[var(--color-text-dim)] border border-[var(--color-border)] rounded-full px-4 py-1.5 active:scale-95 transition"
			>
				▲ Xem {windowHidden} tin cũ hơn
			</button>
		{/if}
		{#each windowedMessages as m, i (m.uuid ?? (m.ts ? `t${m.ts}-${m.kind}` : `i${i}-${m.kind}`))}
			<ChatBubble msg={m} />
		{/each}
		{#if isThinking}
			<div class="flex justify-start">
				<div
					class="rounded-2xl rounded-tl-sm px-3.5 py-2.5 bg-[var(--color-surface)] border border-[var(--color-border)]"
					aria-label="Claude đang phản hồi"
				>
					<span class="thinking-dots inline-flex gap-1.5">
						<span class="dot"></span>
						<span class="dot"></span>
						<span class="dot"></span>
					</span>
				</div>
			</div>
		{/if}
	{/if}

	<!-- Inline prompt card (Cách 1): the needsInput question renders HERE, at the
	     bottom of the transcript flow — right under CC's analysis — so the user
	     reads the lead-up and the question together. No top banner/strip (the
	     header's "• cần trả lời" chip is the only top signal). -->
	{#if needsInput}
		<div class="pt-1">
			{@render needsInputCard()}
		</div>
	{/if}
</div>

<style>
	.thinking-dots .dot {
		width: 6px;
		height: 6px;
		border-radius: 50%;
		background: var(--color-text-dim);
		display: inline-block;
		animation: thinking-bounce 1.4s ease-in-out infinite;
	}
	.thinking-dots .dot:nth-child(2) {
		animation-delay: 0.2s;
	}
	.thinking-dots .dot:nth-child(3) {
		animation-delay: 0.4s;
	}
	@keyframes thinking-bounce {
		0%, 60%, 100% {
			opacity: 0.3;
			transform: translateY(0);
		}
		30% {
			opacity: 1;
			transform: translateY(-3px);
		}
	}
</style>

<!-- Slash command / skill picker — bottom sheet over the composer.
     Tap outside scrim or pick an item to close. Picked items prefix the composer text. -->
{#if showCommandPicker}
	<div
		class="fixed inset-0 z-30 bg-black/40 backdrop-blur-sm flex items-end"
		onclick={() => (showCommandPicker = false)}
		role="presentation"
	>
		<div
			class="w-full max-h-[70vh] bg-[var(--color-bg)] border-t border-[var(--color-border)] rounded-t-2xl shadow-2xl flex flex-col"
			style="padding-bottom: env(safe-area-inset-bottom)"
			onclick={(e) => e.stopPropagation()}
			role="dialog"
			aria-label="Chọn lệnh hoặc skill"
		>
			<div class="px-4 pt-3 pb-2 border-b border-[var(--color-border)]/60">
				<div class="flex items-center justify-between mb-2">
					<span class="font-semibold text-[var(--color-text)]">Lệnh / Skill</span>
					<button
						type="button"
						onclick={() => (showCommandPicker = false)}
						class="text-[var(--color-text-dim)] text-sm px-2 py-1 rounded active:scale-95"
					>
						Đóng
					</button>
				</div>
				<input
					type="search"
					bind:value={commandFilter}
					placeholder="Tìm lệnh / skill…"
					autocomplete="off"
					autocapitalize="off"
					spellcheck="false"
					class="w-full bg-[var(--color-surface)] border border-[var(--color-border)] rounded-lg px-3 py-2 text-base focus:outline-none focus:border-[var(--color-accent)]/60"
				/>
			</div>
			<div class="flex-1 overflow-y-auto px-2 py-2 space-y-1">
				{#if !commandsLoaded}
					<p class="text-center text-[var(--color-text-dim)] text-sm py-6">Đang tải…</p>
				{:else if filteredCommands.length === 0}
					<p class="text-center text-[var(--color-text-dim)] text-sm py-6">Không có kết quả</p>
				{:else}
					{#each filteredCommands as c (c.name + c.kind)}
						<button
							type="button"
							onclick={() => pickCommand(c.name)}
							class="w-full text-left px-3 py-2.5 rounded-lg hover:bg-[var(--color-surface)] active:bg-[var(--color-surface)] active:scale-[0.99] transition flex items-start gap-3"
						>
							<span class="font-mono text-[var(--color-accent)] font-semibold mt-0.5">/</span>
							<span class="flex-1 min-w-0">
								<span class="block font-medium text-[var(--color-text)] truncate">
									{c.name}
									<span class="text-[10px] uppercase tracking-wide text-[var(--color-text-dim)]/70 ml-2">
										{c.kind.replace('-', ' ')}
									</span>
								</span>
								{#if c.description}
									<span class="block text-[12.5px] text-[var(--color-text-dim)] leading-snug line-clamp-2">
										{c.description}
									</span>
								{/if}
							</span>
						</button>
					{/each}
				{/if}
			</div>
		</div>
	</div>
{/if}

<!-- Composer: position:fixed using env(keyboard-inset-height). With VirtualKeyboard
     API opted in (+layout.svelte), iOS 17.4+/Chrome will set this env var to the
     actual keyboard height when keyboard is open, sliding the composer flush
     above the keyboard with NO JS measurement. On older iOS where env is 0,
     composer stays at viewport bottom and iOS native scroll-into-view handles it. -->
<div
	bind:clientHeight={composerHeight}
	class="fixed inset-x-0 pointer-events-none"
	style="bottom: env(keyboard-inset-height, 0px); padding-bottom: max(0px, calc(env(safe-area-inset-bottom) - 34px)); transition: bottom 0.15s ease-out"
>
	<form
		onsubmit={(e) => {
			e.preventDefault();
			send();
		}}
		class="pointer-events-auto"
	>
		{#if readOnly}
			<!-- Spec 04: read-only banner replaces composer for non-active session views. -->
			<div
				class="rounded-t-2xl bg-[var(--color-surface)] border-t border-x border-[var(--color-border)] px-4 py-3 shadow-2xl"
			>
				<p class="text-sm text-[var(--color-text-dim)]">
					📜 <b>Read-only history</b> — đang xem session cũ.
					<a href="/projects/{encodeURIComponent(projectId)}" class="text-[var(--color-accent)] underline">
						Quay lại danh sách
					</a>
				</p>
			</div>
		{:else if isModeB}
			<!-- Principle: in PC mode the session is driven by the native CC ext on
			     the PC; the bridge has nothing to send-keys into. Replace the whole
			     composer with a locked notice + the switch to take over. -->
			<div
				class="rounded-t-2xl bg-[var(--color-surface)] border-t border-x border-[var(--color-border)] px-4 py-3 shadow-2xl flex items-center justify-between gap-3"
				style="padding-bottom: max(0.75rem, env(safe-area-inset-bottom))"
			>
				<p class="text-sm text-[var(--color-text-dim)] min-w-0">
					🖥 <b>Đang ở PC</b> — tự về Bridge khi đóng VS Code.
				</p>
				<OwnerBanner
					{projectId}
					{owner}
					{takeoverSafe}
					onChange={(next) => {
						owner = next.owner;
						takeoverSafe = false;
					}}
				/>
			</div>
		{:else}
		<!-- Card container: full width, only top corners rounded — flush with screen
		     edges (Gemini-style). Removes the floating "pill" look for a more seamless
		     transition between chat content and composer. -->
		<div
			class="rounded-t-2xl bg-[var(--color-bg)] border-t border-x border-[var(--color-border)] focus-within:border-[var(--color-accent)]/60 transition shadow-2xl"
		>
			<!-- Busy strip: only shows while CC is processing (and there's no needsInput
			     banner overriding the lower half of the screen). Anchored to the top of
			     the composer card so it's always visible regardless of scroll position,
			     unlike the in-transcript 3-dot bubble which can be off-screen. -->
			{#if isThinking && !needsInput}
				<div
					class="flex items-center gap-2 px-4 pt-2 pb-1.5 text-[12px] text-[var(--color-text-dim)] border-b border-[var(--color-border)]/40"
					aria-live="polite"
				>
					<span class="relative flex w-2 h-2 shrink-0">
						<span class="absolute inline-flex w-full h-full rounded-full bg-[var(--color-accent)]/60 animate-ping"></span>
						<span class="relative inline-flex w-2 h-2 rounded-full bg-[var(--color-accent)]"></span>
					</span>
					<span>Claude đang xử lý…</span>
				</div>
				{#if livePane.length}
					<!-- Ephemeral live-view: tail of the tmux pane while the turn
					     runs (JSONL only lands when a block completes). Replaced by
					     the canonical bubble + auto-hidden the moment Processing
					     ends. Read-only peek — not a transcript source. -->
					<div
						class="px-4 pt-1.5 pb-2 max-h-40 overflow-y-auto border-b border-[var(--color-border)]/40"
						aria-live="polite"
						aria-label="Claude đang làm gì"
					>
						<pre class="font-mono text-[11px] leading-snug text-[var(--color-text-dim)] whitespace-pre-wrap break-words m-0">{livePane.join('\n')}</pre>
					</div>
				{/if}
			{/if}
			{#if atOpen}
				<!-- "@" file-mention autocomplete — sits above the textarea, inside
				     the composer card. Tap or ↑/↓ + Enter to insert "@path ". -->
				<div
					class="border-b border-[var(--color-border)]/50 max-h-52 overflow-y-auto"
					role="listbox"
					aria-label="Chọn file để tag"
				>
					{#if atLoading && atFiles.length === 0}
						<div class="px-4 py-2 text-xs text-[var(--color-text-dim)]">Đang tìm file…</div>
					{:else if atFiles.length === 0}
						<div class="px-4 py-2 text-xs text-[var(--color-text-dim)]">
							Không có file khớp “{atQuery}”
						</div>
					{:else}
						{#each atFiles as f, i (f)}
							<button
								type="button"
								role="option"
								aria-selected={i === atActiveIdx}
								onclick={() => pickFile(f)}
								class="w-full text-left px-4 py-2 min-h-[38px] flex items-center gap-2 text-sm transition {i ===
								atActiveIdx
									? 'bg-[var(--color-bg)]/70'
									: 'hover:bg-[var(--color-bg)]/40'}"
							>
								<span class="text-[var(--color-text-dim)]">@</span>
								<span class="truncate">{f}</span>
							</button>
						{/each}
					{/if}
				</div>
			{/if}
			<textarea
				bind:this={taEl}
				bind:value={replyText}
				onkeydown={onKey}
				oninput={detectMention}
				onfocus={() => (composerFocused = true)}
				onblur={() => (composerFocused = false)}
				placeholder="Message Claude…"
				rows="1"
				autocapitalize="sentences"
				spellcheck="false"
				class="w-full max-h-72 px-4 pt-2 pb-0 bg-transparent text-base leading-snug focus:outline-none resize-none overflow-y-auto placeholder:text-[var(--color-text-dim)]"
			></textarea>
			<!-- Internal toolbar — flush inside the card. 3-col grid (not
			     justify-between) so the Bridge/PC switch is TRUE-centered in the
			     bar regardless of the left tools' vs send button's widths. -->
			<div class="grid grid-cols-[1fr_auto_1fr] items-center px-1.5 pb-1">
				<div class="flex items-center gap-1 justify-self-start text-[var(--color-text-dim)]">
					<button
						type="button"
						class="w-8 h-8 flex items-center justify-center rounded-full hover:bg-[var(--color-bg)]/60 active:scale-95 transition opacity-50 cursor-not-allowed"
						aria-label="Đính kèm (sắp có)"
						disabled
						title="Sắp có"
					>
						<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
							 stroke-linecap="round" stroke-linejoin="round" class="w-5 h-5">
							<path d="M12 5v14"/><path d="M5 12h14"/>
						</svg>
					</button>
					<button
						type="button"
						onclick={openCommandPicker}
						disabled={isThinking}
						class="w-8 h-8 flex items-center justify-center rounded-full transition text-[var(--color-text-dim)] {isThinking
							? 'opacity-40 cursor-not-allowed'
							: 'hover:bg-[var(--color-bg)]/60 hover:text-[var(--color-text)] active:scale-95'}"
						aria-label="Slash command / skill"
						title={isThinking ? 'Claude đang xử lý…' : 'Slash command / skill'}
					>
						<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
							 stroke-linecap="round" stroke-linejoin="round" class="w-5 h-5">
							<path d="M14 4L10 20"/>
						</svg>
					</button>
				</div>
				<!-- Center grid cell — model id of the current session (moved here from
				     the header). Always present (empty until first usage) so the 3-col
				     composer toolbar stays stable and centered. The ⚡Bridge/🖥PC owner
				     pill used to live here; the PC-mode "Tiếp quản" escape hatch still
				     renders in the locked-composer notice above (ADR-017). -->
				<div class="justify-self-center flex items-center gap-1.5">
					{#if lastUsage?.model}
						<span
							class="hdr-badge bg-[var(--color-surface)] border-[var(--color-border)]/50 text-[var(--color-text-dim)]"
							title="Model phiên hiện tại: {lastUsage.model}"
						>
							{lastUsage.model}
						</span>
					{/if}
					{#if cortex}
						<!-- ADR-025: CortexPlexus-usage badge (memory + code map CC consulted
						     this session). 🔗 = the code/knowledge graph ("plexus" = network);
						     count of mcp tool_use calls, "—" when unused. Tap → memory cockpit
						     scoped to this project (Slice 2). title= shows the per-tool breakdown. -->
						<a
							href="/cortex?repo={encodeURIComponent(projectId)}"
							title={cortex.total > 0
								? `CortexPlexus: ${cortexBreakdown} — chạm để mở bộ nhớ`
								: 'Phiên này CC chưa tra CortexPlexus — chạm để mở bộ nhớ'}
							class="hdr-badge active:scale-95 transition {cortex.total > 0
								? 'bg-[var(--color-accent)]/15 border-[var(--color-accent)]/40 text-[var(--color-accent)]'
								: 'bg-[var(--color-surface)] border-[var(--color-border)]/50 text-[var(--color-text-dim)]'}"
						>
							🔗 {cortex.total > 0 ? cortex.total : '—'}
						</a>
					{/if}
				</div>
				{#if isThinking && !isModeB}
					<!-- During processing: queue button appears when textarea has
					     content (/btw parity — buffer for after current turn);
					     stop button always present (ESC parity — cancel current turn). -->
					<div class="justify-self-end flex items-center gap-1.5">
						{#if replyText.trim().length > 0}
							<button
								type="button"
								onclick={queueText}
								disabled={queueing}
								class="w-8 h-8 flex items-center justify-center rounded-full bg-emerald-600/90 text-white hover:bg-emerald-600 disabled:opacity-50 disabled:cursor-not-allowed active:scale-95 transition"
								aria-label="Queue (gửi sau khi Claude idle)"
								title="Queue — sẽ gửi khi Claude xong turn hiện tại"
							>
								{#if queueing}
									<span class="animate-pulse">…</span>
								{:else}
									<!-- Pause-bars + tiny send arrow combined → queue glyph -->
									<svg viewBox="0 0 24 24" fill="none" stroke="currentColor"
										stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"
										class="w-4 h-4">
										<rect x="5" y="6" width="3" height="12" rx="0.5" fill="currentColor" stroke="none"/>
										<rect x="11" y="6" width="3" height="12" rx="0.5" fill="currentColor" stroke="none"/>
										<path d="M17 9l4 3-4 3"/>
									</svg>
								{/if}
							</button>
						{/if}
						<!-- Stop button — parity with CC CLI's ESC keystroke. Replaces
						     the disabled "…" pulse: when CC is working, the user's
						     most useful action is to cancel. Backend sends 2× Esc. -->
						<button
							type="button"
							onclick={interruptTurn}
							disabled={interrupting}
							class="w-8 h-8 flex items-center justify-center rounded-full bg-[var(--color-danger)]/90 text-white hover:bg-[var(--color-danger)] disabled:opacity-50 disabled:cursor-not-allowed active:scale-95 transition"
							aria-label="Dừng (gửi Esc tới Claude)"
							title="Dừng turn hiện tại của Claude (2× Esc)"
						>
							{#if interrupting}
								<span class="animate-pulse">…</span>
							{:else}
								<!-- Solid square stop glyph -->
								<svg viewBox="0 0 24 24" fill="currentColor" class="w-3.5 h-3.5">
									<rect x="6" y="6" width="12" height="12" rx="1.5"/>
								</svg>
							{/if}
						</button>
					</div>
				{:else}
					<button
						type="submit"
						disabled={isModeB || !replyText.trim() || (askAnswer.active && askAnswer.missing.length > 0)}
						class="justify-self-end w-8 h-8 flex items-center justify-center rounded-full bg-[var(--color-text)] text-[var(--color-bg)] disabled:bg-[var(--color-text-dim)]/30 disabled:text-[var(--color-text-dim)]/50 disabled:cursor-not-allowed active:scale-95 transition"
						aria-label={isModeB ? 'Session đang ở PC' : 'Gửi'}
						title={isModeB
							? 'Đang ở PC — tự chuyển khi đóng VS Code; nút Tiếp quản sáng khi an toàn'
							: 'Gửi'}
					>
						<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"
							 stroke-linecap="round" stroke-linejoin="round" class="w-5 h-5">
							<path d="M12 19V5"/><path d="M5 12l7-7 7 7"/>
						</svg>
					</button>
				{/if}
			</div>
		</div>
		{/if}
	</form>
</div>
