<script lang="ts">
	import '../app.css';
	import { onMount } from 'svelte';

	let { children } = $props();

	// Initialize --app-height to window.innerHeight on mount + orientation change.
	// Routes that handle keyboard (e.g. chat session) override this dynamically.
	onMount(() => {
		if (typeof window === 'undefined') return;
		let onVisible: (() => void) | undefined;
		let swCleanup: (() => void) | undefined;
		// Register the service worker manually. vite-plugin-pwa's injectRegister
		// can't insert a script into SvelteKit's adapter-static HTML; doing it
		// here ensures /sw.js gets registered exactly once.
		if ('serviceWorker' in navigator) {
			// Auto-reload exactly once when a NEW service worker takes control.
			// Without this, the SW updates in the background but the open PWA keeps
			// running the OLD cached build until the user manually reloads TWICE —
			// which made every UI change appear as "nothing changed". Guard with a
			// flag + only when there was already a controller (an update, not the
			// first install).
			let reloading = false;
			const hadController = !!navigator.serviceWorker.controller;
			navigator.serviceWorker.addEventListener('controllerchange', () => {
				if (reloading || !hadController) return;
				reloading = true;
				window.location.reload();
			});
			navigator.serviceWorker
				.register('/sw.js', { scope: '/' })
				.then((reg) => {
					// Poll for an updated SW so a long-lived PWA session still picks
					// up new deploys without the user thinking it's frozen.
					const poll = setInterval(() => reg.update().catch(() => {}), 60_000);
					// iOS Safari suspends timers + SW checks while the PWA is
					// backgrounded, so the 60 s poll doesn't run there. Check the
					// instant the PWA returns to foreground — the exact "deploy,
					// then pick the phone up" case — so a fresh build is picked
					// up immediately instead of up to 60 s later (or never, if
					// iOS never resumes the timer).
					onVisible = () => {
						if (document.visibilityState === 'visible') reg.update().catch(() => {});
					};
					document.addEventListener('visibilitychange', onVisible);
					swCleanup = () => {
						clearInterval(poll);
						if (onVisible) document.removeEventListener('visibilitychange', onVisible);
					};
				})
				// eslint-disable-next-line no-console
				.catch((err) => console.warn('SW register failed:', err));
		}
		// Opt in to Virtual Keyboard API where supported (Chrome Android, iOS 17.4+).
		// Lets the keyboard overlay content + exposes env(keyboard-inset-height) to CSS.
		const vk = (navigator as unknown as { virtualKeyboard?: { overlaysContent: boolean } })
			.virtualKeyboard;
		if (vk) {
			try { vk.overlaysContent = true; } catch { /* ignore */ }
		}

		const root = document.documentElement;
		const apply = () => {
			root.style.setProperty('--app-height', `${window.innerHeight}px`);
		};
		window.addEventListener('resize', apply);
		window.addEventListener('orientationchange', apply);
		apply();
		return () => {
			window.removeEventListener('resize', apply);
			window.removeEventListener('orientationchange', apply);
			root.style.removeProperty('--app-height');
			swCleanup?.();
		};
	});
</script>

{@render children()}
