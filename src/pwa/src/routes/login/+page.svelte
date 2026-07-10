<script lang="ts">
	import { goto } from '$app/navigation';
	import { auth } from '$lib/stores/auth.svelte';

	let token = $state('');
	let testing = $state(false);
	let error = $state<string | null>(null);

	async function submit(e: SubmitEvent) {
		e.preventDefault();
		if (!token.trim()) return;

		testing = true;
		error = null;
		try {
			// Sanity-check the token by hitting a protected endpoint
			const r = await fetch('/api/sessions', {
				headers: { Authorization: `Bearer ${token.trim()}` }
			});
			if (!r.ok) {
				error = `Token không hợp lệ (${r.status})`;
				return;
			}
			auth.set(token.trim());
			goto('/');
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		} finally {
			testing = false;
		}
	}
</script>

<main class="min-h-screen flex items-center justify-center px-4">
	<form
		onsubmit={submit}
		class="w-full max-w-sm space-y-4 bg-[var(--color-surface)] rounded-lg p-6"
	>
		<div>
			<h1 class="text-xl font-semibold">CortexBridge</h1>
			<p class="text-sm text-[var(--color-text-dim)] mt-1">Dán bearer token để đăng nhập</p>
		</div>

		<input
			type="text"
			bind:value={token}
			placeholder="cb_..."
			autocomplete="off"
			autocapitalize="off"
			autocorrect="off"
			spellcheck="false"
			class="w-full px-3 py-2 rounded bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] focus:outline-none font-mono text-sm"
		/>

		{#if error}
			<p class="text-sm text-[var(--color-danger)]">{error}</p>
		{/if}

		<button
			type="submit"
			disabled={testing || !token.trim()}
			class="w-full py-2.5 rounded bg-[var(--color-accent)] text-[var(--color-bg)] font-medium disabled:opacity-50"
		>
			{testing ? 'Đang kiểm tra…' : 'Đăng nhập'}
		</button>
	</form>
</main>
