<script lang="ts">
	// ADR-017: mode is AUTOMATIC (ModeWatcher, driven by the Anthropic ide
	// lockfile). No free Bridge/PC toggle, no "Bàn giao cho PC" (A→B is auto).
	// This is a read-only mode indicator plus ONE guarded escape hatch —
	// "Tiếp quản" — enabled only when the bridge has PROVED the PC side is gone
	// (takeoverSafe). It can never force an unsafe switch (two-process risk).
	import { sessionsApi } from '$lib/api/sessions';
	import { ApiException } from '$lib/api/client';

	type Owner = 'tmux' | 'pc' | 'none';

	let {
		projectId,
		owner,
		takeoverSafe = false,
		client = 'pwa',
		disabled = false,
		onChange
	}: {
		projectId: string;
		owner: Owner | null;
		/** Bridge-proved "PC is gone, B→A is safe" — gates the only button. */
		takeoverSafe?: boolean;
		client?: string;
		// Locked with the rest of the composer toolbar while Claude is mid-turn.
		disabled?: boolean;
		onChange?: (next: { owner: Owner; sessionUuid: string | null; sinceUtc: string }) => void;
	} = $props();

	let busy = $state(false);

	async function takeover() {
		if (busy || !takeoverSafe) return;
		if (
			!window.confirm(
				'Bridge xác nhận VS Code trên PC đã đóng. Tiếp quản session về Bridge?'
			)
		)
			return;
		busy = true;
		try {
			const r = await sessionsApi.handoff(projectId, 'tmux', { client, confirmed: true });
			onChange?.(r);
		} catch (e) {
			window.alert(
				e instanceof ApiException && e.status === 409
					? e.error.message
					: e instanceof Error
						? e.message
						: String(e)
			);
		} finally {
			busy = false;
		}
	}

	// ADR-017 §3 escape hatch. Bridge has NOT proved PC is gone (takeoverSafe
	// false) — typically the VS Code Remote-SSH window is still open but no CC
	// runs there (auto B→A can't resolve: lock present ⇏ CC present). User
	// asserts it's safe; backend pins ForcedTmux so ModeWatcher won't revert
	// on the lock alone (it still yields the instant PC CC actually writes).
	async function takeoverForce() {
		if (busy) return;
		if (
			!window.confirm(
				'⚠ ÉP tiếp quản — Bridge CHƯA xác nhận PC đã đóng.\n\n' +
					'Chỉ ép khi bạn CHẮC CHẮN không còn Claude Code nào chạy trên PC ' +
					'(đã đóng tab chat CC trong VS Code; mở VS Code không sao). ' +
					'Nếu PC vẫn còn CC, hai tiến trình cùng ghi 1 session sẽ loạn ' +
					'("[Request interrupted]").\n\nÉp tiếp quản session về Bridge?'
			)
		)
			return;
		busy = true;
		try {
			const r = await sessionsApi.handoff(projectId, 'tmux', {
				client,
				confirmed: true,
				force: true
			});
			onChange?.(r);
		} catch (e) {
			window.alert(
				e instanceof ApiException && e.status === 409
					? e.error.message
					: e instanceof Error
						? e.message
						: String(e)
			);
		} finally {
			busy = false;
		}
	}
</script>

{#if owner === 'tmux'}
	<!-- Mode A — Bridge is driving (read-only indicator, no action). -->
	<span
		class="inline-flex shrink-0 items-center h-6 leading-6 px-3 rounded-full text-[11px] font-semibold bg-[var(--color-success)]/15 text-[var(--color-success)] {disabled
			? 'opacity-40'
			: ''}"
		style="box-shadow: inset 0 0 0 1px var(--color-success)"
		title="CortexBridge đang điều khiển (tmux trên VM) — reply được từ đây"
	>
		⚡ Bridge
	</span>
{:else if owner === 'pc'}
	<!-- Mode B — PC (Anthropic CC) owns it. Auto-returns to Bridge when you
	     close VS Code; the guarded button only lights up once that is proven. -->
	<div
		class="inline-flex shrink-0 rounded-full overflow-hidden text-[11px] font-medium {disabled
			? 'opacity-40 pointer-events-none'
			: ''}"
		style="box-shadow: inset 0 0 0 1px var(--color-border)"
		role="group"
		aria-label="Trạng thái session"
	>
		<span
			class="inline-flex items-center h-[30px] leading-[30px] px-3 bg-[var(--color-warning)]/15 text-[var(--color-warning)] font-semibold"
			title="Đang ở PC (VS Code + Anthropic CC). Tự chuyển về Bridge khi bạn đóng VS Code."
		>
			🖥 PC
		</span>
		{#if takeoverSafe}
			<!-- Provably safe — the normal one-tap recovery. -->
			<button
				type="button"
				disabled={busy || disabled}
				onclick={takeover}
				title="Bridge xác nhận PC đã đóng — bấm để tiếp quản về Bridge"
				class="btn-icon inline-flex items-center h-[30px] leading-[30px] px-3 border-l border-[var(--color-border)] {busy ||
				disabled
					? 'text-[var(--color-text-dim)]/50 cursor-not-allowed'
					: 'text-[var(--color-success)] font-semibold active:scale-95'}"
			>
				{busy ? '…' : 'Tiếp quản'}
			</button>
		{:else}
			<!-- Not provably safe (lock still present). Guarded ESCAPE HATCH:
			     strong confirm; backend pins ForcedTmux but still yields the
			     instant PC CC actually writes (no two-process possible). -->
			<button
				type="button"
				disabled={busy || disabled}
				onclick={takeoverForce}
				title="Bridge chưa xác nhận PC đóng (VS Code còn mở). Ép tiếp quản — chỉ khi PC không còn CC chạy."
				class="btn-icon inline-flex items-center h-[30px] leading-[30px] px-3 border-l border-[var(--color-border)] {busy ||
				disabled
					? 'text-[var(--color-text-dim)]/50 cursor-not-allowed'
					: 'text-[var(--color-warning)] font-semibold active:scale-95'}"
			>
				{busy ? '…' : 'Tiếp quản (ép)'}
			</button>
		{/if}
	</div>
{/if}
