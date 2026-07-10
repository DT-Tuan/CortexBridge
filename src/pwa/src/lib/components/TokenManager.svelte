<script lang="ts">
	import { api, ApiException } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { goto } from '$app/navigation';

	type TokenInfo = {
		id: number;
		deviceName: string | null;
		createdAt: string;
		lastUsedAt: string | null;
		revokedAt: string | null;
		current: boolean;
	};

	let tokens = $state<TokenInfo[]>([]);
	let loading = $state(true);
	let busy = $state(false);
	let err = $state<string | null>(null);
	let confirmRevoke = $state<number | null>(null);
	let confirmRotate = $state(false);
	let newToken = $state<string | null>(null);
	let copied = $state(false);
	let showRevoked = $state(false);

	// Sort: current device first, then most-recently-used active, then revoked.
	// Filter: hide revoked unless showRevoked is true (they pile up — see audit log).
	const visible = $derived(() => {
		const arr = tokens.filter((t) => showRevoked || !t.revokedAt);
		return arr.sort((a, b) => {
			if (a.current !== b.current) return a.current ? -1 : 1;
			if (!!a.revokedAt !== !!b.revokedAt) return a.revokedAt ? 1 : -1;
			const at = a.lastUsedAt ?? a.createdAt;
			const bt = b.lastUsedAt ?? b.createdAt;
			return new Date(bt).getTime() - new Date(at).getTime();
		});
	});
	const activeCount = $derived(tokens.filter((t) => !t.revokedAt).length);
	const revokedCount = $derived(tokens.filter((t) => !!t.revokedAt).length);

	function rel(iso: string | null): string {
		if (!iso) return 'chưa dùng';
		const s = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
		if (s < 60) return 'vừa xong';
		const m = Math.floor(s / 60);
		if (m < 60) return `${m} phút trước`;
		const h = Math.floor(m / 60);
		if (h < 24) return `${h} giờ trước`;
		const d = Math.floor(h / 24);
		if (d < 30) return `${d} ngày trước`;
		return new Date(iso).toLocaleDateString('vi-VN');
	}

	function fail(e: unknown) {
		err = e instanceof ApiException ? e.error.message : String(e);
	}

	async function load() {
		loading = true;
		err = null;
		try {
			tokens = await api.get<TokenInfo[]>('/api/auth/tokens');
		} catch (e) {
			fail(e);
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	async function revoke(id: number, isCurrent: boolean) {
		busy = true;
		err = null;
		try {
			await api.delete(`/api/auth/tokens/${id}`);
			if (isCurrent) {
				auth.clear();
				goto('/login');
				return;
			}
			confirmRevoke = null;
			await load();
		} catch (e) {
			fail(e);
		} finally {
			busy = false;
		}
	}

	async function rotate() {
		busy = true;
		err = null;
		try {
			const r = await api.post<{ token: string }>('/api/auth/tokens/rotate', {});
			auth.set(r.token); // swap in place — current device stays logged in
			newToken = r.token;
			confirmRotate = false;
			await load();
		} catch (e) {
			fail(e);
		} finally {
			busy = false;
		}
	}

	async function copyNew() {
		if (!newToken) return;
		try {
			await navigator.clipboard.writeText(newToken);
			copied = true;
		} catch {
			copied = false;
		}
	}
</script>

<section class="bg-[var(--color-surface)] rounded-lg p-4">
	<details class="group">
		<summary class="cursor-pointer list-none flex items-baseline justify-between gap-2 py-1 -m-1 px-1 rounded hover:bg-[var(--color-surface-2)]">
			<span class="text-sm uppercase text-[var(--color-text-dim)]">
				Thiết bị &amp; token
				{#if !loading}
					<span class="normal-case text-[11px] text-[var(--color-text)]">({activeCount} active{revokedCount > 0 ? `, ${revokedCount} đã thu hồi` : ''})</span>
				{/if}
			</span>
			<span class="text-[11px] text-[var(--color-text-dim)] group-open:rotate-180 transition-transform">▾</span>
		</summary>

	<div class="space-y-3 pt-3">
	{#if newToken}
		<div
			class="rounded border border-[var(--color-accent)]/50 bg-[var(--color-accent)]/10 p-3 space-y-2"
		>
			<p class="text-xs text-[var(--color-accent)]">
				Token mới — lưu ngay, sẽ <strong>không hiển thị lại</strong>.
			</p>
			<p class="font-mono text-xs break-all">{newToken}</p>
			<button
				type="button"
				onclick={copyNew}
				class="min-h-[38px] px-4 rounded border border-[var(--color-border)] text-sm"
			>
				{copied ? 'Đã chép ✓' : 'Chép'}
			</button>
		</div>
	{/if}

	{#if loading}
		<p class="text-xs text-[var(--color-text-dim)]">Đang tải…</p>
	{:else}
		{#if revokedCount > 0}
			<label class="flex items-center gap-2 text-[11px] text-[var(--color-text-dim)]">
				<input type="checkbox" bind:checked={showRevoked} class="accent-current" />
				Hiện token đã thu hồi ({revokedCount})
			</label>
		{/if}
		<ul class="space-y-2">
			{#each visible() as t (t.id)}
				<li class="border border-[var(--color-border)] rounded p-3 space-y-1">
					<div class="flex items-center justify-between gap-2">
						<span class="text-sm font-medium break-all">
							{t.deviceName || '(không tên)'}
						</span>
						{#if t.current}
							<span class="text-[10px] px-2 py-0.5 rounded bg-[var(--color-accent)]/20 text-[var(--color-accent)]">
								thiết bị này
							</span>
						{:else if t.revokedAt}
							<span class="text-[10px] px-2 py-0.5 rounded bg-[var(--color-danger)]/20 text-[var(--color-danger)]">
								đã thu hồi
							</span>
						{/if}
					</div>
					<p class="text-[11px] text-[var(--color-text-dim)]">
						Tạo {rel(t.createdAt)} · Dùng {rel(t.lastUsedAt)}
					</p>

					{#if !t.revokedAt}
						{#if confirmRevoke === t.id}
							<div class="flex items-center gap-2 pt-1">
								<span class="text-xs text-[var(--color-danger)]">
									{t.current ? 'Thu hồi sẽ đăng xuất thiết bị này.' : 'Xác nhận thu hồi?'}
								</span>
								<button
									type="button"
									disabled={busy}
									onclick={() => revoke(t.id, t.current)}
									class="min-h-[38px] px-3 rounded bg-[var(--color-danger)]/20 border border-[var(--color-danger)]/40 text-[var(--color-danger)] text-xs disabled:opacity-50"
								>
									Thu hồi
								</button>
								<button
									type="button"
									onclick={() => (confirmRevoke = null)}
									class="min-h-[38px] px-3 rounded border border-[var(--color-border)] text-xs"
								>
									Huỷ
								</button>
							</div>
						{:else}
							<button
								type="button"
								onclick={() => (confirmRevoke = t.id)}
								class="min-h-[38px] px-3 rounded border border-[var(--color-border)] text-xs"
							>
								Thu hồi
							</button>
						{/if}
					{/if}
				</li>
			{/each}
		</ul>

		{#if confirmRotate}
			<div class="flex items-center gap-2">
				<span class="text-xs">Đổi token thiết bị này?</span>
				<button
					type="button"
					disabled={busy}
					onclick={rotate}
					class="min-h-[38px] px-4 rounded bg-[var(--color-accent)] text-white text-sm disabled:opacity-50"
				>
					Đổi
				</button>
				<button
					type="button"
					onclick={() => (confirmRotate = false)}
					class="min-h-[38px] px-4 rounded border border-[var(--color-border)] text-sm"
				>
					Huỷ
				</button>
			</div>
		{:else}
			<button
				type="button"
				onclick={() => (confirmRotate = true)}
				class="min-h-[38px] px-4 rounded border border-[var(--color-border)] text-sm"
			>
				Đổi token thiết bị này
			</button>
		{/if}
	{/if}

	{#if err}
		<p class="text-xs text-[var(--color-danger)] break-words">{err}</p>
	{/if}
	</div>
	</details>
</section>
