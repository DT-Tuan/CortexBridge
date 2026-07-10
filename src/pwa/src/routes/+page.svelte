<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import { goto } from '$app/navigation';
	import { auth } from '$lib/stores/auth.svelte';
	import { sessionsApi } from '$lib/api/sessions';
	import { ApiException } from '$lib/api/client';
	import type { SessionListItem } from '$lib/api/types';
	import UsageWidget from '$lib/components/UsageWidget.svelte';

	let sessions = $state<SessionListItem[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);
	let busy = $state<Set<string>>(new Set());
	let live = $state(false);

	// Attention-first: sessions needing a reply float to the top so among 4-5
	// concurrent sessions you instantly see which one is waiting on you. Backend
	// already orders running→stopped→none; we only lift needsInput within that.
	const ordered = $derived(
		[...sessions].sort((a, b) => Number(b.needsInput) - Number(a.needsInput))
	);
	const runningCount = $derived(sessions.filter((s) => s.status === 'running').length);
	const needsCount = $derived(sessions.filter((s) => s.needsInput).length);

	const POLL_MS = 5000;
	let pollTimer: ReturnType<typeof setInterval> | null = null;

	function startPolling() {
		stopPolling();
		pollTimer = setInterval(() => {
			if (document.visibilityState === 'visible') void refresh(true);
		}, POLL_MS);
	}
	function stopPolling() {
		if (pollTimer) clearInterval(pollTimer);
		pollTimer = null;
	}
	function onVisibility() {
		if (document.visibilityState === 'visible') {
			void refresh(true);
			startPolling();
		} else {
			stopPolling();
		}
	}

	// New-project modal
	let showNewModal = $state(false);
	let newName = $state('');
	let creating = $state(false);
	let createError = $state<string | null>(null);

	onMount(async () => {
		if (!auth.isAuthed) {
			goto('/login');
			return;
		}
		await refresh();
		startPolling();
		document.addEventListener('visibilitychange', onVisibility);
	});

	onDestroy(() => {
		stopPolling();
		if (typeof document !== 'undefined')
			document.removeEventListener('visibilitychange', onVisibility);
	});

	// background=true → silent poll: no spinner flicker, transient errors ignored
	// (keep showing the last good list); only the first/manual load surfaces them.
	async function refresh(background = false) {
		if (!background) {
			loading = true;
			error = null;
		}
		try {
			sessions = await sessionsApi.list();
			error = null;
			live = true;
		} catch (e) {
			if (e instanceof ApiException && e.status === 401) {
				auth.clear();
				goto('/login');
				return;
			}
			live = false;
			if (!background) error = e instanceof Error ? e.message : String(e);
		} finally {
			if (!background) loading = false;
		}
	}

	function setBusy(id: string, on: boolean) {
		const next = new Set(busy);
		if (on) next.add(id); else next.delete(id);
		busy = next;
	}

	// Resume a project's last session. If the bridge refuses because the session
	// is PC-owned (last used in VS Code → owner=pc → 409 "*.owned_by_pc"), offer
	// a force takeover (handoff to Bridge) then retry. This is the dashboard
	// counterpart of the chat-view OwnerBanner "Tiếp quản (ép)" — without it the
	// Active/Khởi động button 409s silently (and the displayed owner can lag the
	// bridge's derived owner, so a card showing "Active" can still be pc-owned).
	async function restartWithTakeover(projectId: string) {
		try {
			await sessionsApi.restart(projectId);
		} catch (e) {
			if (
				e instanceof ApiException &&
				e.status === 409 &&
				e.error.code.endsWith('owned_by_pc')
			) {
				if (
					!window.confirm(
						`"${projectId}" đang ở PC mode (VS Code). Tiếp quản (ép) về Bridge để khởi động?\n` +
							`Chỉ làm khi đã đóng VS Code cho project này (tránh chạy 2 tiến trình CC).`
					)
				)
					return;
				await sessionsApi.handoff(projectId, 'tmux', { force: true, client: 'pwa' });
				await sessionsApi.restart(projectId);
			} else {
				throw e;
			}
		}
	}

	async function startCc(projectId: string) {
		if (busy.has(projectId)) return;
		setBusy(projectId, true);
		try {
			await restartWithTakeover(projectId);
			await refresh();
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		} finally {
			setBusy(projectId, false);
		}
	}

	// Inline Stop / Active button per card (replaces the older kebab menu).
	async function activateProject(projectId: string) {
		if (busy.has(projectId)) return;
		if (!window.confirm(`Kích hoạt phiên CC cho "${projectId}"?`)) return;
		setBusy(projectId, true);
		try {
			await restartWithTakeover(projectId);
			await refresh();
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		} finally {
			setBusy(projectId, false);
		}
	}

	// Explicit "Tiếp quản" for a card the dashboard shows as PC-owned: force the
	// session to Bridge (handoff, ADR-017 §3) then resume. One clear confirm.
	async function takeoverProject(projectId: string) {
		if (busy.has(projectId)) return;
		if (
			!window.confirm(
				`"${projectId}" đang ở PC mode (VS Code). Tiếp quản (ép) về Bridge?\n` +
					`Chỉ làm khi đã đóng VS Code cho project này (tránh chạy 2 tiến trình CC).`
			)
		)
			return;
		setBusy(projectId, true);
		try {
			await sessionsApi.handoff(projectId, 'tmux', { force: true, client: 'pwa' });
			await sessionsApi.restart(projectId);
			await refresh();
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		} finally {
			setBusy(projectId, false);
		}
	}

	async function stopProject(projectId: string) {
		if (busy.has(projectId)) return;
		if (
			!window.confirm(
				`Dừng phiên CC hiện tại của "${projectId}"? Tmux window sẽ đóng và project về trạng thái "không có session đang chạy".`
			)
		)
			return;
		setBusy(projectId, true);
		try {
			await sessionsApi.exit(projectId);
			await refresh();
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		} finally {
			setBusy(projectId, false);
		}
	}

	function openNewModal() {
		showNewModal = true;
		newName = '';
		createError = null;
	}

	async function createProject(e: Event) {
		e.preventDefault();
		if (creating) return;
		const name = newName.trim();
		if (!/^[A-Za-z0-9._-]{1,64}$/.test(name)) {
			createError = 'Tên chỉ gồm chữ/số/dấu .-_ , tối đa 64 ký tự';
			return;
		}
		creating = true;
		createError = null;
		try {
			const r = await sessionsApi.createProject(name, true);
			showNewModal = false;
			await refresh();
			// Auto-navigate to the new session if CC started
			if (r.started) goto(`/sessions/${encodeURIComponent(r.projectId)}`);
		} catch (e) {
			createError = e instanceof Error ? e.message : String(e);
		} finally {
			creating = false;
		}
	}

	function formatTs(ts: string | null): string {
		if (!ts) return '';
		try {
			return new Date(ts).toLocaleString('vi-VN');
		} catch {
			return ts;
		}
	}
</script>

<header class="sticky top-0 z-10 bg-[var(--color-bg)]/95 backdrop-blur border-b border-[var(--color-border)] px-4 py-3 flex items-center justify-between">
	<div class="min-w-0">
		<h1 class="text-lg font-semibold leading-tight">CortexBridge</h1>
		{#if !loading && sessions.length > 0}
			<div class="text-[11px] text-[var(--color-text-dim)] flex items-center gap-2 mt-0.5">
				<span
					class="inline-block w-1.5 h-1.5 rounded-full {live
						? 'bg-[var(--color-success)]'
						: 'bg-[var(--color-text-dim)]/50'}"
					title={live ? 'Live (cập nhật mỗi 5s)' : 'Mất kết nối — đang thử lại'}
				></span>
				<span>{runningCount} chạy</span>
				{#if needsCount > 0}
					<span class="text-[var(--color-warning)]">· {needsCount} cần trả lời</span>
				{/if}
			</div>
		{/if}
	</div>
	<div class="flex gap-2">
		<button
			onclick={openNewModal}
			class="px-3 py-1.5 text-sm rounded-lg bg-[var(--color-surface)] hover:bg-[var(--color-surface-2)] flex items-center"
			title="Tạo project mới"
			aria-label="Tạo project mới"
		>
			➕
		</button>
		<button
			onclick={() => refresh()}
			class="px-3 py-1.5 text-sm rounded-lg bg-[var(--color-surface)] hover:bg-[var(--color-surface-2)]"
		>
			↻
		</button>
		<a
			href="/cortex"
			title="Bộ nhớ CortexPlexus"
			class="px-3 py-1.5 text-sm rounded-lg bg-[var(--color-surface)] hover:bg-[var(--color-surface-2)] flex items-center"
		>
			🔗
		</a>
		<a
			href="/settings"
			class="px-3 py-1.5 text-sm rounded-lg bg-[var(--color-surface)] hover:bg-[var(--color-surface-2)] flex items-center"
		>
			⚙
		</a>
	</div>
</header>

<!-- body has overflow:hidden (for chat view's internal scroll), so the
     dashboard main must claim flex-1 + own overflow-y-auto, otherwise the
     project-card list overflows the viewport silently. pb covers iOS
     safe-area bottom inset. -->
<main class="flex-1 overflow-y-auto overscroll-contain px-4 py-3 space-y-2 pb-[max(env(safe-area-inset-bottom),1rem)]">
	<UsageWidget />
	{#if loading}
		<p class="text-[var(--color-text-dim)]">Đang tải…</p>
	{:else if error}
		<div class="rounded bg-[var(--color-danger)]/20 border border-[var(--color-danger)] p-3 text-sm">
			{error}
		</div>
	{:else if sessions.length === 0}
		<p class="text-[var(--color-text-dim)] text-center py-12">
			Chưa có project nào. Tap nút ➕ trên cùng để tạo.
		</p>
	{:else}
		{#each ordered as s (s.projectId)}
			{@const isNone = s.status === 'none'}
			{#if isNone}
				<!-- Workspace dir without CC session — show inline "Khởi động" instead of nav link. -->
				<div class="rounded-xl bg-[var(--color-surface)]/60 p-4 flex items-center justify-between gap-3 border border-dashed border-[var(--color-border)]/60">
					<span class="font-medium flex items-center gap-2 min-w-0">
						<span class="inline-block w-2 h-2 rounded-full shrink-0 bg-[var(--color-text-dim)]/50" title="Chưa khởi tạo CC"></span>
						<span class="truncate text-[var(--color-text-dim)]">{s.projectId}</span>
					</span>
					<button
						type="button"
						onclick={() => startCc(s.projectId)}
						disabled={busy.has(s.projectId)}
						class="text-xs px-3 py-1.5 rounded-lg bg-[var(--color-success)]/30 border border-[var(--color-success)]/60 text-[var(--color-success)] font-semibold disabled:opacity-50 active:scale-95 transition shrink-0"
					>
						{busy.has(s.projectId) ? 'Đang…' : 'Khởi động CC'}
					</button>
				</div>
			{:else}
				<a
					href={`/sessions/${encodeURIComponent(s.projectId)}`}
					class="block rounded-xl p-4 transition bg-[var(--color-surface)] hover:bg-[var(--color-surface-2)] {s.needsInput
						? 'ring-1 ring-[var(--color-warning)]/60'
						: ''}"
				>
					<!-- Top row: status dot + project name on the left, 'More sessions…'
					     pill on the right (same spatial slot as 'Khởi động CC' on the
					     isNone variant — they never coexist, so this keeps the right-edge
					     action consistent across both card states). -->
					<div class="flex items-center justify-between gap-2">
						<span class="font-medium flex items-center gap-2 min-w-0">
							<span
								class="inline-block w-2 h-2 rounded-full shrink-0
								{s.owner === 'pc'
									? 'bg-[var(--color-warning)]'
									: s.status === 'running'
										? 'bg-[var(--color-success)]'
										: 'bg-[var(--color-text-dim)]'}"
								title={s.owner === 'pc'
									? 'Đang trên PC (Mode B)'
									: s.status === 'running'
										? 'CC đang chạy'
										: 'CC đã dừng'}
							></span>
							<span class="truncate">{s.projectId}</span>
							{#if s.owner === 'tmux'}
								<span
									class="text-[10px] leading-none px-1.5 py-0.5 rounded shrink-0 bg-[var(--color-success)]/15 text-[var(--color-success)] border border-[var(--color-success)]/40"
									title="CortexBridge điều khiển — reply được từ PWA/extension"
								>⚡ Bridge</span>
							{:else if s.owner === 'pc'}
								<span
									class="text-[10px] leading-none px-1.5 py-0.5 rounded shrink-0 bg-[var(--color-warning)]/15 text-[var(--color-warning)] border border-[var(--color-warning)]/40"
									title="Đang dùng trên VS Code (PC) — reply tạm khoá"
								>🖥 PC</span>
							{/if}
						</span>
						<button
							type="button"
							onclick={(e) => {
								e.preventDefault();
								e.stopPropagation();
								goto(`/projects/${encodeURIComponent(s.projectId)}`);
							}}
							class="text-[11px] leading-none px-3 py-1.5 rounded-lg border border-[var(--color-border)]/50 text-[var(--color-text-dim)] hover:text-[var(--color-accent)] hover:border-[var(--color-accent)]/50 shrink-0 transition"
							title="Xem tất cả sessions của project"
						>
							More sessions…
						</button>
					</div>
					<!-- Sub-row: timestamp + status badge (needsInput / stopped). -->
					<div class="flex items-center justify-between gap-2 mt-1">
						<span class="text-xs text-[var(--color-text-dim)] truncate">
							{formatTs(s.lastMessageAt)}
						</span>
						{#if s.needsInput}
							<span
								class="text-[11px] leading-none px-2 py-1 rounded-md bg-[var(--color-warning)]/30 text-[var(--color-warning)] border border-[var(--color-warning)]/50 shrink-0"
							>
								cần trả lời
							</span>
						{:else if s.owner === 'pc'}
							<!-- PC Mode (VS Code). The orange dot conveys state, but offer a
							     force takeover so a PC-created/used session can be moved to
							     Bridge from the dashboard (not only the chat-view banner). -->
							<button
								type="button"
								onclick={(e) => {
									e.preventDefault();
									e.stopPropagation();
									takeoverProject(s.projectId);
								}}
								disabled={busy.has(s.projectId)}
								class="text-[11px] leading-none px-3 py-1 rounded-md bg-[var(--color-warning)]/15 text-[var(--color-warning)] border border-[var(--color-warning)]/50 shrink-0 disabled:opacity-50 active:scale-95 transition"
								title="Tiếp quản (ép) session về Bridge — chỉ khi đã đóng VS Code"
							>
								{busy.has(s.projectId) ? '…' : 'Tiếp quản'}
							</button>
						{:else if s.status === 'running'}
							<button
								type="button"
								onclick={(e) => {
									e.preventDefault();
									e.stopPropagation();
									stopProject(s.projectId);
								}}
								disabled={busy.has(s.projectId)}
								class="text-[11px] leading-none px-3 py-1 rounded-md bg-[var(--color-danger)]/15 text-[var(--color-danger)] border border-[var(--color-danger)]/50 shrink-0 disabled:opacity-50 active:scale-95 transition"
								title="Dừng phiên CC hiện tại"
							>
								{busy.has(s.projectId) ? '…' : 'Stop'}
							</button>
						{:else if s.status === 'stopped'}
							<button
								type="button"
								onclick={(e) => {
									e.preventDefault();
									e.stopPropagation();
									activateProject(s.projectId);
								}}
								disabled={busy.has(s.projectId)}
								class="text-[11px] leading-none px-3 py-1 rounded-md bg-[var(--color-success)]/15 text-[var(--color-success)] border border-[var(--color-success)]/50 shrink-0 disabled:opacity-50 active:scale-95 transition"
								title="Khởi động lại phiên CC (resume session cuối)"
							>
								{busy.has(s.projectId) ? '…' : 'Active'}
							</button>
						{/if}
					</div>
				</a>
			{/if}
		{/each}
	{/if}
</main>

{#if showNewModal}
	<div
		class="fixed inset-0 z-30 bg-black/40 backdrop-blur-sm flex items-end sm:items-center justify-center"
		onclick={() => (showNewModal = false)}
		role="presentation"
	>
		<form
			onsubmit={createProject}
			onclick={(e) => e.stopPropagation()}
			class="w-full sm:max-w-sm bg-[var(--color-bg)] border-t sm:border border-[var(--color-border)] sm:rounded-2xl shadow-2xl p-4 space-y-3"
			style="padding-bottom: max(1rem, env(safe-area-inset-bottom))"
		>
			<div class="flex items-center justify-between">
				<h2 class="font-semibold">Tạo project mới</h2>
				<button
					type="button"
					onclick={() => (showNewModal = false)}
					class="text-[var(--color-text-dim)] text-sm px-2 py-1 rounded active:scale-95"
				>Đóng</button>
			</div>
			<label class="block">
				<span class="block text-[12.5px] text-[var(--color-text-dim)] mb-1">Tên thư mục (sẽ tạo trong /workspace/)</span>
				<input
					type="text"
					bind:value={newName}
					placeholder="vd: my-project"
					autocomplete="off"
					autocapitalize="none"
					spellcheck="false"
					required
					class="w-full bg-[var(--color-surface)] border border-[var(--color-border)] rounded-lg px-3 py-2 text-base focus:outline-none focus:border-[var(--color-accent)]/60"
				/>
			</label>
			{#if createError}
				<p class="text-xs text-[var(--color-danger)]">{createError}</p>
			{/if}
			<div class="flex gap-2 pt-1">
				<button
					type="submit"
					disabled={creating}
					class="flex-1 min-h-[38px] px-4 py-2 rounded-lg bg-[var(--color-accent)]/30 border border-[var(--color-accent)]/60 text-[var(--color-accent)] font-semibold disabled:opacity-50 active:scale-95 transition"
				>
					{creating ? 'Đang tạo…' : 'Tạo + Khởi động CC'}
				</button>
			</div>
			<p class="text-[11px] text-[var(--color-text-dim)] leading-snug">
				Sẽ tạo thư mục, mở tmux window mới và spawn <code>claude</code> trong đó. Sau khi xong, dashboard reload và mở chat view của project mới.
			</p>
		</form>
	</div>
{/if}
