<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import { goto } from '$app/navigation';
	import { usageApi } from '$lib/api/usage';
	import { ApiException } from '$lib/api/client';
	import type { UsageResponse } from '$lib/api/types';

	let usage = $state<UsageResponse | null>(null);
	let error = $state<string | null>(null);
	let loading = $state(true);
	let now = $state(Date.now());

	const POLL_MS = 30_000;
	const TICK_MS = 1_000;
	// Official sample older than this = endpoint unreachable (ADR-024).
	const STALE_MS = 5 * 60_000;
	let pollTimer: ReturnType<typeof setInterval> | null = null;
	let tickTimer: ReturnType<typeof setInterval> | null = null;

	async function refresh() {
		try {
			usage = await usageApi.get();
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

	// Live countdown to a reset instant, formatted "Nd Hh", "Hh MMm" or "MMm SSs".
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
		if (h > 0) return `${h}h ${String(m).padStart(2, '0')}m`;
		return `${m}m ${String(s).padStart(2, '0')}s`;
	}

	function fmtDollar(v: number | undefined): string {
		if (v == null) return '$0';
		return v >= 100 ? `$${v.toFixed(0)}` : v >= 10 ? `$${v.toFixed(1)}` : `$${v.toFixed(2)}`;
	}

	function colorFor(pct: number): string {
		if (pct >= 95) return 'bg-red-500';
		if (pct >= 80) return 'bg-yellow-500';
		return 'bg-emerald-500';
	}

	const block = $derived(usage?.block5h ?? null);
	const week = $derived(usage?.week7d ?? null);
	const official = $derived(usage?.official ?? null);
	const o5 = $derived(official?.fiveHour ?? null);
	const o7 = $derived(official?.sevenDay ?? null);
	const officialStale = $derived(
		official != null && now - new Date(official.takenAtUtc).getTime() > STALE_MS
	);
</script>

<button
	type="button"
	class="w-full text-left rounded-xl bg-[var(--color-surface)] hover:bg-[var(--color-surface-2)] border border-[var(--color-border)]/60 px-3 py-2 transition-colors"
	onclick={() => goto('/settings#usage')}
	aria-label="Open usage detail"
>
	{#if loading && !usage}
		<div class="text-[11px] text-[var(--color-text-dim)]">Loading usage…</div>
	{:else if error && !usage}
		<div class="text-[11px] text-red-400">Usage: {error}</div>
	{:else if !o5 && !o7 && !block && !week}
		<div class="text-[11px] text-[var(--color-text-dim)]">No usage data yet.</div>
	{:else}
		<div class="space-y-1.5">
			{#if officialStale && official}
				<div class="text-[10px] text-yellow-300">quota % stale (endpoint unreachable)</div>
			{/if}
			{#if o5 || block}
				{@const pct = Math.min(100, o5?.utilization ?? 0)}
				<div>
					<div class="flex items-baseline justify-between gap-2 text-[11px]">
						<span class="font-medium text-[var(--color-text)]">5h block</span>
						<span class="text-[var(--color-text-dim)] tabular-nums">
							{#if block}{fmtDollar(block.currentCostUsd)} ·{/if}
							{fmtRemaining(o5?.resetsAt ?? block?.endUtc)}
						</span>
					</div>
					{#if o5}
						<div class="relative mt-0.5 h-1.5 rounded-full bg-[var(--color-border)]/40 overflow-hidden">
							<div class="absolute inset-y-0 left-0 {colorFor(o5.utilization)}" style="width: {pct}%"></div>
						</div>
						<div class="text-[10px] text-[var(--color-text-dim)] mt-0.5 tabular-nums">
							{o5.utilization.toFixed(0)}% of plan limit
						</div>
					{/if}
				</div>
			{/if}
			{#if o7 || week}
				{@const wpct = Math.min(100, o7?.utilization ?? 0)}
				<div>
					<div class="flex items-baseline justify-between gap-2 text-[11px]">
						<span class="font-medium text-[var(--color-text)]">7d week</span>
						<span class="text-[var(--color-text-dim)] tabular-nums">
							{#if week}{fmtDollar(week.currentCostUsd)} ·{/if}
							{fmtRemaining(o7?.resetsAt)}
						</span>
					</div>
					{#if o7}
						<div class="relative mt-0.5 h-1.5 rounded-full bg-[var(--color-border)]/40 overflow-hidden">
							<div class="absolute inset-y-0 left-0 {colorFor(o7.utilization)}" style="width: {wpct}%"></div>
						</div>
						<div class="text-[10px] text-[var(--color-text-dim)] mt-0.5 tabular-nums">
							{o7.utilization.toFixed(0)}% of plan limit
						</div>
					{/if}
				</div>
			{/if}
		</div>
	{/if}
</button>
