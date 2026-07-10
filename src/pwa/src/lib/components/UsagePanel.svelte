<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import { usageApi } from '$lib/api/usage';
	import { ApiException } from '$lib/api/client';
	import type { UsageResponse, UsageHistoryResponse } from '$lib/api/types';

	let usage = $state<UsageResponse | null>(null);
	let history = $state<UsageHistoryResponse | null>(null);
	let range = $state<'24h' | '7d'>('24h');
	let error = $state<string | null>(null);
	let loading = $state(true);
	let now = $state(Date.now());

	const POLL_MS = 30_000;
	const TICK_MS = 1_000;
	// Official sample older than this = endpoint unreachable (sampler ticks
	// every ~60s and carries the last-known block forward on failure, ADR-024).
	const STALE_MS = 5 * 60_000;
	let pollTimer: ReturnType<typeof setInterval> | null = null;
	let tickTimer: ReturnType<typeof setInterval> | null = null;

	async function refresh() {
		try {
			const [u, h] = await Promise.all([usageApi.get(), usageApi.history(range)]);
			usage = u;
			history = h;
			error = null;
		} catch (e) {
			error = e instanceof ApiException ? e.error.message : String(e);
		} finally {
			loading = false;
		}
	}

	onMount(() => {
		void refresh();
		pollTimer = setInterval(() => {
			if (document.visibilityState === 'visible') void refresh();
		}, POLL_MS);
		tickTimer = setInterval(() => { now = Date.now(); }, TICK_MS);
	});
	onDestroy(() => {
		if (pollTimer) clearInterval(pollTimer);
		if (tickTimer) clearInterval(tickTimer);
	});

	function changeRange(r: '24h' | '7d') {
		range = r;
		void refresh();
	}

	function fmtDollar(v: number | undefined | null): string {
		if (v == null) return '$0';
		return v >= 100 ? `$${v.toFixed(0)}` : v >= 10 ? `$${v.toFixed(1)}` : `$${v.toFixed(2)}`;
	}

	function fmtRemaining(endUtcIso: string | undefined | null): string {
		if (!endUtcIso) return '—';
		const ms = new Date(endUtcIso).getTime() - now;
		if (ms <= 0) return 'resetting…';
		const totalSec = Math.floor(ms / 1000);
		const d = Math.floor(totalSec / 86_400);
		const h = Math.floor((totalSec % 86_400) / 3600);
		const m = Math.floor((totalSec % 3600) / 60);
		const s = totalSec % 60;
		if (d > 0) return `${d}d ${h}h`;
		return h > 0 ? `${h}h ${String(m).padStart(2, '0')}m` : `${m}m ${String(s).padStart(2, '0')}s`;
	}

	function fmtAge(iso: string): string {
		const ms = now - new Date(iso).getTime();
		const min = Math.floor(ms / 60_000);
		if (min < 60) return `${min}m`;
		const h = Math.floor(min / 60);
		return h < 24 ? `${h}h` : `${Math.floor(h / 24)}d`;
	}

	function colorFor(pct: number): string {
		if (pct >= 95) return 'bg-red-500';
		if (pct >= 80) return 'bg-yellow-500';
		return 'bg-emerald-500';
	}

	function spark(points: number[], width = 320, height = 60): string {
		if (points.length < 2) return '';
		const max = Math.max(...points, 1);
		const stepX = width / (points.length - 1);
		return points.map((v, i) => {
			const x = i * stepX;
			const y = height - (v / max) * height;
			return `${i === 0 ? 'M' : 'L'} ${x.toFixed(1)} ${y.toFixed(1)}`;
		}).join(' ');
	}

	const block = $derived(usage?.block5h ?? null);
	const week = $derived(usage?.week7d ?? null);
	const official = $derived(usage?.official ?? null);
	const officialStale = $derived(
		official != null && now - new Date(official.takenAtUtc).getTime() > STALE_MS
	);
	const points = $derived(history?.points ?? []);
	const block5hSeries = $derived(points.map(p => Number(p.block5hPctCurrent) || 0));
	const week7dSeries = $derived(points.map(p => Number(p.week7dPctCurrent) || 0));
	const projects = $derived(usage?.projects ?? []);
	const projectsTotal = $derived(projects.reduce((s, p) => s + (p.totalCostUsd || 0), 0));
	const projectsMaxCost = $derived(projects.reduce((m, p) => Math.max(m, p.totalCostUsd || 0), 0));
</script>

<section id="usage" class="space-y-3">
	<div class="flex items-baseline justify-between gap-2">
		<h2 class="text-sm uppercase text-[var(--color-text-dim)]">Usage</h2>
		<span class="flex items-baseline gap-2">
			{#if officialStale && official}
				<span class="text-[10px] px-1.5 py-0.5 rounded bg-yellow-900/40 border border-yellow-500/50 text-yellow-300">
					quota % stale {fmtAge(official.takenAtUtc)}
				</span>
			{/if}
			{#if usage?.takenAtUtc}
				<span class="text-[10px] text-[var(--color-text-dim)] tabular-nums">
					{usage.takenAtUtc.slice(11, 19)} UTC
				</span>
			{/if}
		</span>
	</div>

	{#if loading && !usage}
		<div class="text-[12px] text-[var(--color-text-dim)]">Loading…</div>
	{:else if error && !usage}
		<div class="rounded bg-red-900/30 border border-red-500/50 p-3 text-sm">{error}</div>
	{:else}
		{#if official?.fiveHour || block}
			{@const o5 = official?.fiveHour ?? null}
			{@const pct = Math.min(100, o5?.utilization ?? 0)}
			<section class="rounded-xl bg-[var(--color-surface)] border border-[var(--color-border)]/60 p-3 space-y-2">
				<div class="flex items-baseline justify-between gap-2">
					<h3 class="font-semibold">5h block</h3>
					<span class="text-[11px] text-[var(--color-text-dim)] tabular-nums">
						{#if o5}reset in {fmtRemaining(o5.resetsAt)}{:else if block}reset in {fmtRemaining(block.endUtc)}{/if}
					</span>
				</div>
				{#if o5}
					<div class="relative h-2 rounded-full bg-[var(--color-border)]/40 overflow-hidden">
						<div class="absolute inset-y-0 left-0 {colorFor(o5.utilization)}" style="width: {pct}%"></div>
					</div>
					<div class="text-[10px] text-[var(--color-text-dim)] tabular-nums">
						{o5.utilization.toFixed(0)}% of plan limit (official)
					</div>
				{:else}
					<div class="text-[11px] text-[var(--color-text-dim)]">
						Chưa có quota % chính thức (endpoint chưa trả dữ liệu).
					</div>
				{/if}
				{#if block}
					<div class="text-[11px] text-[var(--color-text-dim)] tabular-nums pt-1 border-t border-[var(--color-border)]/40">
						{fmtDollar(block.currentCostUsd)} ccusage
						· projected {fmtDollar(block.projectedCostUsd)}
						· burn ${block.costPerHour.toFixed(2)}/h
						· {block.entries} entries
						{#if block.models.length}· {block.models.join(', ')}{/if}
					</div>
				{/if}
			</section>
		{/if}

		{#if official?.sevenDay || week}
			{@const o7 = official?.sevenDay ?? null}
			{@const wpct = Math.min(100, o7?.utilization ?? 0)}
			<section class="rounded-xl bg-[var(--color-surface)] border border-[var(--color-border)]/60 p-3 space-y-2">
				<div class="flex items-baseline justify-between gap-2">
					<h3 class="font-semibold">7d week</h3>
					<span class="text-[11px] text-[var(--color-text-dim)] tabular-nums">
						{#if o7}reset in {fmtRemaining(o7.resetsAt)}{:else if week}since {week.periodStart}{/if}
					</span>
				</div>
				{#if o7}
					<div class="relative h-2 rounded-full bg-[var(--color-border)]/40 overflow-hidden">
						<div class="absolute inset-y-0 left-0 {colorFor(o7.utilization)}" style="width: {wpct}%"></div>
					</div>
					<div class="text-[10px] text-[var(--color-text-dim)] tabular-nums">
						{o7.utilization.toFixed(0)}% of plan limit (official)
					</div>
				{:else}
					<div class="text-[11px] text-[var(--color-text-dim)]">
						Chưa có quota % chính thức (endpoint chưa trả dữ liệu).
					</div>
				{/if}
				{#if week}
					<div class="text-[11px] text-[var(--color-text-dim)] tabular-nums pt-1 border-t border-[var(--color-border)]/40">
						{fmtDollar(week.currentCostUsd)} ccusage (rolling 7d)
					</div>
					{#if week.modelBreakdown.length}
						<div class="pt-1 border-t border-[var(--color-border)]/40 space-y-1">
							<div class="text-[11px] font-medium text-[var(--color-text)]">Per model</div>
							{#each week.modelBreakdown as m (m.model)}
								{@const total = (m.inputTokens || 0) + (m.outputTokens || 0) + (m.cacheCreationTokens || 0) + (m.cacheReadTokens || 0)}
								<div class="flex items-baseline justify-between gap-2 text-[11px] tabular-nums">
									<span class="text-[var(--color-text)] truncate">{m.model}</span>
									<span class="text-[var(--color-text-dim)]">{fmtDollar(m.costUsd)} · {(total / 1_000_000).toFixed(2)}M tok</span>
								</div>
							{/each}
						</div>
					{/if}
				{/if}
			</section>
		{/if}

		<details class="rounded-xl bg-[var(--color-surface)] border border-[var(--color-border)]/60" open>
			<summary class="cursor-pointer list-none flex items-baseline justify-between gap-2 p-3 hover:bg-[var(--color-surface-2)] rounded-xl">
				<h3 class="font-semibold">Theo project</h3>
				<span class="text-[11px] text-[var(--color-text-dim)] tabular-nums">
					{projects.length} dự án · {fmtDollar(projectsTotal)}
				</span>
			</summary>
			<div class="p-3 pt-0 space-y-2">
				{#if !projects.length}
					<div class="text-[11px] text-[var(--color-text-dim)] text-center py-3">Chưa có dữ liệu.</div>
				{:else}
					{#each projects as p (p.encodedPath)}
						{@const rel = projectsMaxCost > 0 ? (p.totalCostUsd / projectsMaxCost) * 100 : 0}
						{@const tokM = p.totalTokens / 1_000_000}
						<div class="space-y-0.5">
							<div class="flex items-baseline justify-between gap-2 text-[12px]">
								<span class="font-medium text-[var(--color-text)] truncate">{p.name || '(unknown)'}</span>
								<span class="text-[var(--color-text-dim)] tabular-nums shrink-0">
									{fmtDollar(p.totalCostUsd)} · {tokM >= 100 ? tokM.toFixed(0) : tokM.toFixed(1)}M tok
								</span>
							</div>
							<div class="relative h-1 rounded-full bg-[var(--color-border)]/40 overflow-hidden">
								<div class="absolute inset-y-0 left-0 bg-emerald-500/60" style="width: {rel}%"></div>
							</div>
							<div class="text-[10px] text-[var(--color-text-dim)] tabular-nums">
								{p.sessionCount} session{p.sessionCount === 1 ? '' : 's'}{p.lastActivity ? ` · last ${p.lastActivity}` : ''}{p.models.length ? ` · ${p.models.length} model${p.models.length === 1 ? '' : 's'}` : ''}
							</div>
						</div>
					{/each}
				{/if}
			</div>
		</details>

		<details class="rounded-xl bg-[var(--color-surface)] border border-[var(--color-border)]/60">
			<summary class="cursor-pointer list-none flex items-baseline justify-between gap-2 p-3 hover:bg-[var(--color-surface-2)] rounded-xl">
				<h3 class="font-semibold">History</h3>
				<span class="text-[11px] text-[var(--color-text-dim)]">{points.length} snapshots</span>
			</summary>
			<div class="p-3 pt-0 space-y-2">
				<div class="flex gap-1">
					{#each ['24h', '7d'] as r}
						<button
							type="button"
							class="px-2 py-0.5 text-[11px] rounded-md {range === r ? 'bg-[var(--color-surface-2)] text-[var(--color-text)]' : 'text-[var(--color-text-dim)] hover:bg-[var(--color-surface-2)]'}"
							onclick={() => changeRange(r as '24h' | '7d')}
						>{r}</button>
					{/each}
				</div>
				{#if points.length < 2}
					<div class="text-[11px] text-[var(--color-text-dim)] py-4 text-center">
						Not enough snapshots yet (poller fires every 5 min). Come back later.
					</div>
				{:else}
					<div class="space-y-3">
						<div>
							<div class="text-[10px] text-[var(--color-text-dim)] mb-0.5">5h block — official %</div>
							<svg viewBox="0 0 320 60" class="w-full h-12">
								<path d={spark(block5hSeries)} fill="none" stroke="currentColor" stroke-width="1.5" class="text-emerald-400" />
							</svg>
							<div class="text-[10px] text-[var(--color-text-dim)] tabular-nums flex justify-between">
								<span>{points[0].takenUtc.slice(5, 16)}</span>
								<span>{points[points.length - 1].takenUtc.slice(5, 16)}</span>
							</div>
						</div>
						<div>
							<div class="text-[10px] text-[var(--color-text-dim)] mb-0.5">7d week — official %</div>
							<svg viewBox="0 0 320 60" class="w-full h-12">
								<path d={spark(week7dSeries)} fill="none" stroke="currentColor" stroke-width="1.5" class="text-emerald-400" />
							</svg>
						</div>
					</div>
				{/if}
			</div>
		</details>
	{/if}
</section>
