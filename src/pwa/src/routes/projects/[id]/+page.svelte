<script lang="ts">
	import { onMount } from 'svelte';
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { auth } from '$lib/stores/auth.svelte';
	import { sessionsApi } from '$lib/api/sessions';
	import { ApiException } from '$lib/api/client';
	import type { SessionRow } from '$lib/api/types';

	const projectId = $derived(decodeURIComponent(page.params.id ?? ''));

	let sessions = $state<SessionRow[]>([]);
	let activeUuid = $state<string | null>(null);
	let owner = $state<'tmux' | 'pc' | 'none'>('none');
	let loading = $state(true);
	let error = $state<string | null>(null);
	let activating = $state<string | null>(null);
	let creatingNew = $state(false);
	let restarting = $state(false);

	// PC-mode lock: the bridge cannot terminate Anthropic ext, so destructive
	// actions (activate/new/delete/restart) must be hidden entirely (not just
	// refused) to prevent accidental two-process states.
	const pcLocked = $derived(owner === 'pc');

	async function load() {
		loading = true;
		error = null;
		try {
			const r = await sessionsApi.listProjectSessions(projectId);
			sessions = r.sessions;
			activeUuid = r.activeSessionUuid;
			owner = r.owner;
		} catch (e) {
			error = e instanceof ApiException ? e.error.message : String(e);
		} finally {
			loading = false;
		}
	}

	onMount(() => {
		if (!auth.isAuthed) {
			goto('/login');
			return;
		}
		load();
	});

	function shortUuid(u: string | null): string {
		return u ? u.slice(0, 8) : '';
	}

	// Activate a parked session - goes through /activate (ADR-016 Slice 2):
	// owner=pc hard-refuse + graceful /exit of any live window + claude --resume.
	// User-explicit confirm because activate is destructive of the current live
	// session.
	async function activateSession(uuid: string) {
		if (activating || creatingNew) return;
		const msg = activeUuid
			? `Kích hoạt phiên ${shortUuid(uuid)}?\n\nSẽ kết thúc phiên hiện tại (${shortUuid(activeUuid)}) trước.`
			: `Kích hoạt phiên ${shortUuid(uuid)}?`;
		if (!window.confirm(msg)) return;
		activating = uuid;
		try {
			await sessionsApi.activate(projectId, uuid);
			goto(`/sessions/${encodeURIComponent(projectId)}`);
		} catch (e) {
			error = e instanceof ApiException ? e.error.message : String(e);
			activating = null;
		}
	}

	// Start a fresh new session. Backend /new kills any live tmux first then
	// spawns plain `claude` (no --resume). Owner=Pc -> 409 hard-refuse.
	async function newSessionFn() {
		if (activating || creatingNew) return;
		const msg = activeUuid
			? `Bắt đầu phiên MỚI?\n\nSẽ kết thúc phiên hiện tại (${shortUuid(activeUuid)}) trước.`
			: `Bắt đầu phiên MỚI cho project "${projectId}"?`;
		if (!window.confirm(msg)) return;
		creatingNew = true;
		try {
			await sessionsApi.newSession(projectId);
			goto(`/sessions/${encodeURIComponent(projectId)}`);
		} catch (e) {
			error = e instanceof ApiException ? e.error.message : String(e);
			creatingNew = false;
		}
	}

	// Restart the currently-active session (kill + claude --resume <activeUuid>).
	// /restart resumes the last UID found by SessionScanner, which IS the active one
	// when invoked here. Hidden when PC-locked (pcLocked).
	async function restartActiveSession() {
		if (restarting || activating || creatingNew) return;
		if (!window.confirm(`Khởi động lại phiên hiện tại của "${projectId}"?`)) return;
		restarting = true;
		try {
			// Backend /restart refuses if tmux window already exists; first /exit
			// to free the window, then /restart resumes the same UID.
			await sessionsApi.exit(projectId);
			await sessionsApi.restart(projectId);
			await load();
		} catch (e) {
			error = e instanceof ApiException ? e.error.message : String(e);
		} finally {
			restarting = false;
		}
	}

	let deleting = $state<string | null>(null);
	async function deleteSession(s: SessionRow) {
		if (deleting) return;
		const labelStr = s.label ? `[${s.label}] ` : '';
		const snippet = s.firstUserText ? ` "${s.firstUserText.slice(0, 40)}..."` : '';
		if (!confirm(`Xóa ${labelStr}session ${s.sessionUuid.slice(0, 8)}${snippet}?\n\nKhông thể hoàn tác.`))
			return;
		deleting = s.sessionUuid;
		try {
			await sessionsApi.deleteSession(projectId, s.sessionUuid);
			sessions = sessions.filter((x) => x.sessionUuid !== s.sessionUuid);
		} catch (e) {
			error = e instanceof ApiException ? e.error.message : String(e);
		} finally {
			deleting = null;
		}
	}

	function formatDate(iso: string | null): string {
		if (!iso) return '—';
		const d = new Date(iso);
		const now = Date.now();
		const diffMs = now - d.getTime();
		if (diffMs < 60_000) return 'just now';
		if (diffMs < 3_600_000) return `${Math.floor(diffMs / 60_000)}m ago`;
		if (diffMs < 86_400_000) return `${Math.floor(diffMs / 3_600_000)}h ago`;
		return d.toISOString().slice(0, 16).replace('T', ' ');
	}

	function statusLabel(s: SessionRow): { label: string; color: string; symbol: string } {
		if (s.isActive) return { label: 'active', color: 'success', symbol: '●' };
		if (s.isImported) return { label: 'imported', color: 'text-dim', symbol: '📥' };
		return { label: 'stopped', color: 'text-dim', symbol: '○' };
	}
</script>

<header
	class="sticky top-0 z-10 bg-[var(--color-bg)]/95 backdrop-blur border-b border-[var(--color-border)] px-4 py-3 flex items-center gap-3"
>
	<a href="/" class="text-[var(--color-accent)]">← Projects</a>
	<div class="flex-1 min-w-0">
		<h1 class="text-base font-semibold truncate">{projectId}</h1>
		<p class="text-xs text-[var(--color-text-dim)]">
			Sessions ({sessions.length}){activeUuid ? ' · 1 active' : ''}{pcLocked
				? ' · 🖥 PC Mode (locked)'
				: ''}
		</p>
	</div>
	{#if !pcLocked}
		<button
			type="button"
			onclick={newSessionFn}
			disabled={creatingNew || activating !== null}
			class="min-h-[38px] px-3 rounded-xl border border-[var(--color-accent)]/60 text-[var(--color-accent)] text-sm disabled:opacity-50"
			title="Bắt đầu phiên mới (sẽ kết thúc phiên hiện tại nếu có)"
		>
			{creatingNew ? '…' : '+ New'}
		</button>
	{/if}
	<button
		type="button"
		onclick={() => goto(`/sessions/${encodeURIComponent(projectId)}`)}
		class="min-h-[38px] px-3 rounded-xl bg-[var(--color-accent)] text-white text-sm"
	>
		Open active
	</button>
</header>

<!-- Scrollable main so a long sessions list works on iOS PWA without the sticky
     header collapsing. overflow-y-auto + pb large enough to clear safe-area. -->
<main class="px-3 py-3 space-y-2 overflow-y-auto pb-[max(env(safe-area-inset-bottom),1rem)]">
	{#if loading}
		<p class="text-[var(--color-text-dim)] text-sm px-2 py-4">Đang tải...</p>
	{:else if error}
		<div
			class="rounded-lg bg-[var(--color-danger)]/15 border border-[var(--color-danger)]/40 px-3 py-2 text-sm text-[var(--color-danger)]"
		>
			{error}
		</div>
	{:else if sessions.length === 0}
		<div class="text-center py-12">
			<p class="text-[var(--color-text-dim)]">Chưa có session nào trong project này.</p>
			{#if !pcLocked}
				<button
					type="button"
					onclick={newSessionFn}
					disabled={creatingNew}
					class="mt-3 min-h-[38px] px-4 rounded-xl bg-[var(--color-accent)] text-white text-sm disabled:opacity-50"
				>
					{creatingNew ? 'Đang tạo…' : '+ Tạo session mới'}
				</button>
			{/if}
		</div>
	{:else}
		{#each sessions as s (s.sessionUuid)}
			{@const status = statusLabel(s)}
			<!-- Outer wrapper is a div (not button) to allow nested button children
			     (label pill + Resume). Whole div is click-navigable via role+tabindex. -->
			<div
				role="button"
				tabindex="0"
				onclick={() =>
					goto(
						s.isActive
							? `/sessions/${encodeURIComponent(projectId)}`
							: `/sessions/${encodeURIComponent(projectId)}?session=${encodeURIComponent(s.sessionUuid)}`
					)}
				onkeydown={(e) => {
					if (e.key === 'Enter' || e.key === ' ') {
						e.preventDefault();
						goto(
							s.isActive
								? `/sessions/${encodeURIComponent(projectId)}`
								: `/sessions/${encodeURIComponent(projectId)}?session=${encodeURIComponent(s.sessionUuid)}`
						);
					}
				}}
				class="cursor-pointer w-full text-left bg-[var(--color-surface)] rounded-xl p-3 active:scale-[0.99] transition border border-transparent hover:border-[var(--color-border)]/40"
			>
				<div class="flex items-start justify-between gap-2 mb-1">
					<span class="text-xs text-[var(--color-{status.color})] font-medium">
						{status.symbol} {status.label} · {formatDate(s.lastMessageAt)}
					</span>
					<div class="flex items-center gap-2 shrink-0">
						<span class="text-[11px] text-[var(--color-text-dim)] font-mono">
							{s.sessionUuid.slice(0, 8)}
						</span>
					</div>
				</div>
				<p class="text-xs text-[var(--color-text-dim)] mb-0.5">
					{s.messageCount} msgs · {(s.sizeBytes / 1024).toFixed(0)} KB
				</p>
				{#if s.firstUserText}
					<p class="text-sm text-[var(--color-text)] truncate">
						"{s.firstUserText}"
					</p>
				{/if}
				<!-- Action row: Resume / Delete buttons. Only renders for non-active sessions
				     (active session can't be deleted — would orphan tmux window). -->
				{#if s.isActive && !pcLocked}
					<!-- Active session: Restart button only (kill+resume same UID). -->
					<div class="flex items-center gap-2 mt-2">
						<button
							type="button"
							onclick={(e) => {
								e.stopPropagation();
								restartActiveSession();
							}}
							disabled={restarting || activating !== null || creatingNew}
							class="min-h-9 px-3 rounded-md border border-[var(--color-warning)]/50 text-[var(--color-warning)] text-xs disabled:opacity-50"
							title="Khởi động lại phiên này"
						>
							{restarting ? 'Đang khởi động lại…' : '↻ Restart'}
						</button>
					</div>
				{:else if !s.isActive && !pcLocked}
					<!-- Parked / stopped session: Activate + Delete. Hidden when PC-locked. -->
					<div class="flex items-center gap-2 mt-2">
						{#if s.canResume}
							<button
								type="button"
								onclick={(e) => {
									e.stopPropagation();
									activateSession(s.sessionUuid);
								}}
								disabled={activating !== null || creatingNew}
								class="min-h-9 px-3 rounded-md border border-[var(--color-accent)]/50 text-[var(--color-accent)] text-xs disabled:opacity-50"
								title={activeUuid
									? 'Sẽ kết thúc phiên hiện tại trước khi kích hoạt phiên này'
									: 'Kích hoạt phiên này (chạy claude --resume)'}
							>
								{activating === s.sessionUuid ? 'Đang kích hoạt…' : '⚡ Kích hoạt'}
							</button>
						{/if}
						<button
							type="button"
							onclick={(e) => {
								e.stopPropagation();
								deleteSession(s);
							}}
							disabled={deleting !== null}
							class="min-h-9 px-3 rounded-md border border-[var(--color-danger)]/40 text-[var(--color-danger)] text-xs disabled:opacity-50"
							title="Xóa session vĩnh viễn"
						>
							{deleting === s.sessionUuid ? 'Đang xóa...' : '🗑 Xóa'}
						</button>
					</div>
				{/if}
				{#if s.isImported}
					<p class="text-[11px] text-[var(--color-text-dim)] mt-1">
						read-only · cwd: <span class="font-mono">{s.cwd}</span>
					</p>
				{/if}
			</div>
		{/each}
	{/if}
</main>
