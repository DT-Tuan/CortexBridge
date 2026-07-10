<script lang="ts">
	import { goto } from '$app/navigation';
	import { auth } from '$lib/stores/auth.svelte';
	import PushOptIn from '$lib/components/PushOptIn.svelte';
	import TokenManager from '$lib/components/TokenManager.svelte';
	import UsagePanel from '$lib/components/UsagePanel.svelte';

	function logout() {
		auth.clear();
		goto('/login');
	}
</script>

<header
	class="sticky top-0 z-10 bg-[var(--color-bg)]/95 backdrop-blur border-b border-[var(--color-border)] px-4 py-3 flex items-center gap-3"
>
	<a href="/" class="text-[var(--color-accent)]">←</a>
	<h1 class="text-lg font-semibold">Cài đặt</h1>
</header>

<!-- body is flex-col + overflow:hidden (chat view's internal scroll), so this
     main must claim flex-1 + own overflow-y-auto or its content clips with no
     scroll — same fix as the dashboard main. pb covers iOS safe-area inset. -->
<main class="flex-1 overflow-y-auto overscroll-contain px-4 py-4 space-y-4 pb-[max(env(safe-area-inset-bottom),1rem)]">
	<UsagePanel />

	<TokenManager />

	<PushOptIn />

	<button
		onclick={logout}
		class="w-full py-3 rounded bg-[var(--color-danger)]/20 border border-[var(--color-danger)]/40 text-[var(--color-danger)]"
	>
		Đăng xuất
	</button>
</main>
