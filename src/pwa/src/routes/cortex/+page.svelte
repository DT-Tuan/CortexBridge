<script lang="ts">
	import { page } from '$app/state';
	import { cortexApi, type CortexMemoryList } from '$lib/api/cortex';
	import { ApiException } from '$lib/api/client';

	// ADR-025 Phase 4 Slice 2 — memory cockpit (read-only). Browse + search the
	// cross-project store CortexPlexus holds. ?repo=<projectId> (from the chat 🔗
	// badge) opens scoped to that project; otherwise defaults to "all".
	const repo = $derived(page.url.searchParams.get('repo'));
	let scope = $state<'project' | 'all'>(page.url.searchParams.get('repo') ? 'project' : 'all');
	let q = $state('');
	let list = $state<CortexMemoryList | null>(null);
	let loading = $state(false);
	let unavailable = $state(false);
	let expanded = $state<Set<string>>(new Set());

	// Fast list_memories (~ms); search is a client-side filter over the fetched
	// set — semantic recall is 30–50 s on this LXC, so it's not on this path.
	const filtered = $derived.by(() => {
		const all = list?.memories ?? [];
		const needle = q.trim().toLowerCase();
		if (!needle) return all;
		return all.filter((m) => (m.content ?? '').toLowerCase().includes(needle));
	});

	async function load() {
		loading = true;
		unavailable = false;
		saveQueued = false;
		try {
			list = await cortexApi.memories({
				scope,
				repo: scope === 'project' ? (repo ?? undefined) : undefined,
				limit: 200
			});
		} catch (e) {
			list = null;
			if (e instanceof ApiException && e.status === 503) unavailable = true;
			else if (e instanceof ApiException && e.error?.code === 'cortex.repo_required') {
				// scope=project with no repo — fall back to "all".
				scope = 'all';
				return load();
			} else unavailable = true;
		} finally {
			loading = false;
		}
	}

	function setScope(s: 'project' | 'all') {
		if (scope === s) return;
		scope = s;
		load();
	}

	function toggle(id: string | null) {
		if (!id) return;
		const next = new Set(expanded);
		next.has(id) ? next.delete(id) : next.add(id);
		expanded = next;
	}

	function ago(iso: string | null): string {
		if (!iso) return '';
		const d = Date.parse(iso);
		if (Number.isNaN(d)) return '';
		const s = Math.max(0, (Date.now() - d) / 1000);
		if (s < 60) return 'vừa xong';
		if (s < 3600) return `${Math.floor(s / 60)} phút trước`;
		if (s < 86400) return `${Math.floor(s / 3600)} giờ trước`;
		return `${Math.floor(s / 86400)} ngày trước`;
	}

	const topicColor: Record<string, string> = {
		decision: 'text-[var(--color-accent)] border-[var(--color-accent)]/40',
		pattern: 'text-emerald-400 border-emerald-400/40',
		bug: 'text-[var(--color-danger)] border-[var(--color-danger)]/40',
		preference: 'text-[var(--color-warning)] border-[var(--color-warning)]/40'
	};

	// --- Save (write) — ~10 s, embeds content. scope=project uses the route repo. ---
	let showForm = $state(false);
	let saving = $state(false);
	let saveErr = $state<string | null>(null);
	let saveQueued = $state(false);
	let fContent = $state('');
	let fScope = $state<'project' | 'global'>(repo ? 'project' : 'global');
	let fTopic = $state<'preference' | 'pattern' | 'decision' | 'bug' | 'todo' | 'note'>('note');
	const topics = ['decision', 'pattern', 'preference', 'bug', 'todo', 'note'] as const;

	async function saveMemory() {
		if (saving || !fContent.trim()) return;
		saving = true;
		saveErr = null;
		try {
			await cortexApi.save({
				content: fContent.trim(),
				scope: fScope,
				topic: fTopic,
				repo: fScope === 'project' ? (repo ?? undefined) : undefined
			});
			// 202 — embedding runs off-request (~1 min). Row appears on a later
			// refetch; don't reload now (it isn't persisted yet).
			fContent = '';
			showForm = false;
			saveQueued = true;
			// embedding finishes off-request (~50–70 s); auto-refresh once past it.
			setTimeout(() => load(), 75000);
		} catch (e) {
			saveErr =
				e instanceof ApiException && e.status === 503
					? 'CortexPlexus không khả dụng.'
					: e instanceof ApiException
						? e.error?.message || 'Lưu thất bại.'
						: 'Lưu thất bại.';
		} finally {
			saving = false;
		}
	}

	let forgetting = $state<string | null>(null);
	async function forgetMemory(id: string | null) {
		if (!id || forgetting) return;
		if (!confirm('Quên bản ghi này?')) return;
		forgetting = id;
		try {
			await cortexApi.forget(id);
			if (list) list = { count: Math.max(0, list.count - 1), memories: list.memories.filter((m) => m.id !== id) };
		} catch {
			/* non-fatal — leave the row, user can retry */
		} finally {
			forgetting = null;
		}
	}

	$effect(() => {
		// initial load (and re-load if the route's repo param changes)
		void repo;
		load();
	});
</script>

<svelte:head><title>Bộ nhớ CortexPlexus</title></svelte:head>

<header
	class="sticky top-0 z-10 bg-[var(--color-bg)]/95 backdrop-blur border-b border-[var(--color-border)] px-4 py-3 flex items-center gap-3"
>
	<a href="/" class="text-[var(--color-accent)]">←</a>
	<h1 class="text-lg font-semibold flex-1">🔗 Bộ nhớ CortexPlexus</h1>
	<button
		type="button"
		onclick={() => load()}
		disabled={loading}
		title="Làm mới"
		class="px-2 text-[var(--color-text-dim)] hover:text-[var(--color-accent)] disabled:opacity-40"
	>
		↻
	</button>
</header>

<main class="flex-1 overflow-y-auto overscroll-contain px-4 py-3 space-y-3 pb-[max(env(safe-area-inset-bottom),1rem)]">
	<!-- scope toggle -->
	<div class="flex items-center gap-1 text-[13px]">
		<button
			type="button"
			onclick={() => setScope('project')}
			disabled={!repo}
			class="px-3 h-8 rounded-l-lg border {scope === 'project'
				? 'bg-[var(--color-accent)]/15 border-[var(--color-accent)]/50 text-[var(--color-accent)]'
				: 'border-[var(--color-border)] text-[var(--color-text-dim)]'} disabled:opacity-40"
		>
			Dự án này{repo ? ` (${repo})` : ''}
		</button>
		<button
			type="button"
			onclick={() => setScope('all')}
			class="px-3 h-8 rounded-r-lg border -ml-px {scope === 'all'
				? 'bg-[var(--color-accent)]/15 border-[var(--color-accent)]/50 text-[var(--color-accent)]'
				: 'border-[var(--color-border)] text-[var(--color-text-dim)]'}"
		>
			Tất cả
		</button>
	</div>

	<input
		type="search"
		bind:value={q}
		placeholder="Lọc theo nội dung…"
		class="w-full h-10 px-3 rounded-lg bg-[var(--color-surface)] border border-[var(--color-border)] text-sm"
	/>

	<!-- Save (write) — collapsible. ~10s while embedding, so a spinner + disabled. -->
	<div class="rounded-lg border border-[var(--color-border)]/60">
		<button
			type="button"
			onclick={() => (showForm = !showForm)}
			class="w-full px-3 h-9 flex items-center justify-between text-[13px] text-[var(--color-accent)]"
		>
			<span>+ Lưu bộ nhớ</span><span>{showForm ? '▾' : '▸'}</span>
		</button>
		{#if showForm}
			<div class="p-3 pt-0 space-y-2">
				<textarea
					bind:value={fContent}
					rows="3"
					placeholder="Nội dung bài học / quyết định…"
					class="w-full px-3 py-2 rounded-lg bg-[var(--color-surface)] border border-[var(--color-border)] text-sm"
				></textarea>
				<div class="flex items-center gap-2 text-[13px]">
					<select bind:value={fScope} class="h-9 px-2 rounded-lg bg-[var(--color-surface)] border border-[var(--color-border)]">
						<option value="project" disabled={!repo}>Dự án này{repo ? ` (${repo})` : ''}</option>
						<option value="global">Toàn cục</option>
					</select>
					<select bind:value={fTopic} class="h-9 px-2 rounded-lg bg-[var(--color-surface)] border border-[var(--color-border)]">
						{#each topics as t}<option value={t}>{t}</option>{/each}
					</select>
				</div>
				{#if saveErr}<p class="text-[11px] text-[var(--color-danger)]">{saveErr}</p>{/if}
				<button
					type="button"
					onclick={saveMemory}
					disabled={saving || !fContent.trim()}
					class="w-full h-9 rounded-lg bg-[var(--color-accent)]/20 border border-[var(--color-accent)]/50 text-[var(--color-accent)] text-[13px] disabled:opacity-50"
				>
					{saving ? 'Đang gửi…' : 'Lưu'}
				</button>
			</div>
		{/if}
	</div>
	{#if saveQueued}
		<p class="text-[11px] text-[var(--color-accent)] px-1">
			Đã nhận — đang lưu nền (~1 phút do nhúng embedding). Kéo làm mới để thấy bản ghi mới.
		</p>
	{/if}

	{#if loading}
		<p class="text-center text-[var(--color-text-dim)] text-sm py-6">Đang tải…</p>
	{:else if unavailable}
		<p class="text-center text-[var(--color-warning)] text-sm py-6">
			CortexPlexus không khả dụng. Thử lại sau.
		</p>
	{:else if list && filtered.length > 0}
		<p class="text-[11px] text-[var(--color-text-dim)]">
			{filtered.length} / {list.count} bản ghi{q.trim() ? ' (đã lọc)' : ''}
		</p>
		<ul class="space-y-2">
			{#each filtered as m (m.id)}
				<li class="rounded-lg bg-[var(--color-surface)] border border-[var(--color-border)]/60 p-3">
					<div class="flex items-center gap-2 mb-1.5 text-[10px]">
						{#if m.topic}
							<span class="px-1.5 py-0.5 rounded border {topicColor[m.topic] ?? 'text-[var(--color-text-dim)] border-[var(--color-border)]'}">{m.topic}</span>
						{/if}
						{#if m.repository}<span class="text-[var(--color-text-dim)]">{m.repository}</span>{/if}
						{#if m.score != null}<span class="text-[var(--color-text-dim)]">score {m.score.toFixed(2)}</span>{/if}
						<span class="flex-1"></span>
						<span class="text-[var(--color-text-dim)]">{ago(m.createdAt)}</span>
						<button
							type="button"
							onclick={() => forgetMemory(m.id)}
							disabled={forgetting === m.id}
							title="Quên bản ghi này"
							class="text-[var(--color-text-dim)] hover:text-[var(--color-danger)] disabled:opacity-40 px-1"
						>
							{forgetting === m.id ? '…' : '🗑'}
						</button>
					</div>
					<button
						type="button"
						onclick={() => toggle(m.id)}
						class="text-left text-[13px] leading-snug w-full {m.id && expanded.has(m.id) ? '' : 'line-clamp-3'}"
					>
						{m.content}
					</button>
				</li>
			{/each}
		</ul>
	{:else}
		<p class="text-center text-[var(--color-text-dim)] text-sm py-6">
			{list && list.count > 0 && q.trim() ? 'Không khớp bộ lọc.' : 'Không có bản ghi nào.'}
		</p>
	{/if}
</main>
