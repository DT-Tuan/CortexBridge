<script lang="ts">
	import { pushApi } from '$lib/api/push';
	import { api } from '$lib/api/client';

	let supported = $state(false);
	let permission = $state<NotificationPermission>('default');
	let subscribed = $state(false);
	let serverEnabled = $state<boolean | null>(null);
	let busy = $state(false);
	let errorMsg = $state<string | null>(null);
	let status = $state<string>('');

	function setStatus(s: string) {
		status = s;
		// eslint-disable-next-line no-console
		console.log('[PushOptIn]', s);
	}

	function withTimeout<T>(p: Promise<T>, ms: number, label: string): Promise<T> {
		return Promise.race([
			p,
			new Promise<T>((_, rej) =>
				setTimeout(() => rej(new Error(`${label} timeout ${ms / 1000}s`)), ms)
			)
		]);
	}

	async function refresh() {
		supported = pushApi.supported();
		if (!supported) return;
		permission = pushApi.permission();
		try {
			const vapid = await pushApi.vapidKey();
			serverEnabled = vapid.enabled;
		} catch {
			serverEnabled = false;
		}
		try {
			const sub = await pushApi.currentSubscription();
			subscribed = sub !== null;
		} catch {
			subscribed = false;
		}
	}

	$effect(() => {
		refresh();
	});

	async function enable() {
		errorMsg = null;
		status = '';
		busy = true;
		try {
			setStatus('1: requestPermission');
			const perm = await withTimeout(Notification.requestPermission(), 30_000, 'permission');
			setStatus('1: permission=' + perm);
			if (perm !== 'granted') throw new Error('Permission ' + perm);

			setStatus('2: ensure SW registered');
			let reg = await navigator.serviceWorker.getRegistration();
			if (!reg) {
				reg = await navigator.serviceWorker.register('/sw.js', { scope: '/' });
			}
			setStatus(
				'2: state ' +
					(reg.active?.state ?? reg.installing?.state ?? reg.waiting?.state ?? 'none')
			);

			if (!reg.active) {
				setStatus('2: waiting for active...');
				const target = reg.installing ?? reg.waiting;
				if (target) {
					await withTimeout(
						new Promise<void>((resolve, reject) => {
							target.addEventListener('statechange', () => {
								if (target.state === 'activated') resolve();
								else if (target.state === 'redundant') reject(new Error('SW redundant'));
							});
						}),
						45_000,
						'SW activate'
					);
				} else {
					// No installing/waiting and no active — fall back to ready (single SW, should be quick)
					reg = await withTimeout(navigator.serviceWorker.ready, 30_000, 'sw ready');
				}
				setStatus('2: active=' + (reg.active?.state ?? 'still-none'));
			}
			if (!reg.active) throw new Error('SW never became active');

			setStatus('3: vapid-key');
			const vapid = await withTimeout(pushApi.vapidKey(), 15_000, 'vapid');
			setStatus('3: vapid keyLen=' + (vapid.publicKey?.length ?? 0));
			if (!vapid.enabled || !vapid.publicKey) throw new Error('Server thiếu VAPID');

			setStatus('4: pushManager.subscribe');
			let sub = await reg.pushManager.getSubscription();
			if (!sub) {
				const padding = '='.repeat((4 - (vapid.publicKey.length % 4)) % 4);
				const b64 = (vapid.publicKey + padding).replace(/-/g, '+').replace(/_/g, '/');
				const raw = atob(b64);
				const buf = new ArrayBuffer(raw.length);
				const u8 = new Uint8Array(buf);
				for (let i = 0; i < raw.length; i++) u8[i] = raw.charCodeAt(i);
				sub = await withTimeout(
					reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: buf }),
					30_000,
					'subscribe'
				);
			}
			setStatus('4: endpoint=' + sub.endpoint.slice(0, 40));

			setStatus('5: POST /api/push/subscribe');
			const j = sub.toJSON();
			await withTimeout(
				api.post('/api/push/subscribe', {
					subscription: {
						endpoint: sub.endpoint,
						keys: { p256dh: j.keys?.p256dh ?? '', auth: j.keys?.auth ?? '' },
						expirationTime: sub.expirationTime ?? null
					},
					deviceLabel: navigator.userAgent.includes('iPhone') ? 'iPhone' : 'Browser'
				}),
				15_000,
				'post'
			);
			setStatus('DONE');
			await refresh();
		} catch (e) {
			errorMsg = e instanceof Error ? e.message : String(e);
			setStatus('ERROR: ' + errorMsg);
		} finally {
			busy = false;
		}
	}

	async function disable() {
		errorMsg = null;
		busy = true;
		try {
			await pushApi.unsubscribe();
			await refresh();
		} catch (e) {
			errorMsg = e instanceof Error ? e.message : String(e);
		} finally {
			busy = false;
		}
	}
</script>

<section class="bg-[var(--color-surface)] rounded-lg p-4 space-y-3">
	<h2 class="text-sm uppercase text-[var(--color-text-dim)]">Thông báo đẩy (Web Push)</h2>

	{#if !supported}
		<p class="text-xs text-[var(--color-text-dim)]">
			Trình duyệt không hỗ trợ. Trên iPhone: Add to Home Screen + mở từ icon.
		</p>
	{:else if serverEnabled === false}
		<p class="text-xs">Server chưa bật Web Push (thiếu VAPID).</p>
	{:else if subscribed}
		<div class="flex items-center justify-between gap-3">
			<p class="text-sm">Đang nhận thông báo</p>
			<button
				type="button"
				onclick={disable}
				disabled={busy}
				class="min-h-[38px] px-4 rounded border border-[var(--color-border)] text-sm disabled:opacity-50"
			>
				{busy ? '...' : 'Tắt'}
			</button>
		</div>
	{:else}
		<div class="flex items-center justify-between gap-3">
			<p class="text-sm">Chưa bật thông báo</p>
			<button
				type="button"
				onclick={enable}
				disabled={busy}
				class="min-h-[38px] px-4 rounded bg-[var(--color-accent)] text-white text-sm disabled:opacity-50"
			>
				{busy ? '...' : 'Bật'}
			</button>
		</div>
	{/if}

	{#if status}
		<p class="text-[10px] font-mono text-[var(--color-text-dim)] break-all">{status}</p>
	{/if}

	{#if errorMsg}
		<p class="text-xs text-[var(--color-danger)] break-words">{errorMsg}</p>
	{/if}
</section>
