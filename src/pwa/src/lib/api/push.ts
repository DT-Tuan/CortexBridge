import { api } from './client';

export interface VapidKeyResponse {
	publicKey: string;
	enabled: boolean;
}

export interface PushStatusResponse {
	subscribed: boolean;
	totalCount: number;
}

function urlBase64ToUint8Array(base64: string): ArrayBuffer {
	const padding = '='.repeat((4 - (base64.length % 4)) % 4);
	const b64 = (base64 + padding).replace(/-/g, '+').replace(/_/g, '/');
	const raw = atob(b64);
	const buf = new ArrayBuffer(raw.length);
	const arr = new Uint8Array(buf);
	for (let i = 0; i < raw.length; i++) arr[i] = raw.charCodeAt(i);
	return buf;
}

export const pushApi = {
	supported(): boolean {
		return (
			typeof window !== 'undefined' &&
			'serviceWorker' in navigator &&
			'PushManager' in window &&
			'Notification' in window
		);
	},

	permission(): NotificationPermission {
		if (typeof window === 'undefined' || !('Notification' in window)) return 'denied';
		return Notification.permission;
	},

	async getRegistration(): Promise<ServiceWorkerRegistration | null> {
		if (!this.supported()) return null;
		return (await navigator.serviceWorker.ready) ?? null;
	},

	async currentSubscription(): Promise<PushSubscription | null> {
		const reg = await this.getRegistration();
		if (!reg) return null;
		return reg.pushManager.getSubscription();
	},

	async vapidKey(): Promise<VapidKeyResponse> {
		return api.get<VapidKeyResponse>('/api/push/vapid-key');
	},

	async status(endpoint: string): Promise<PushStatusResponse> {
		const q = encodeURIComponent(endpoint);
		return api.get<PushStatusResponse>(`/api/push/status?endpoint=${q}`);
	},

	async subscribe(deviceLabel: string): Promise<PushSubscription> {
		if (!this.supported()) throw new Error('Trình duyệt không hỗ trợ push');
		const perm = await Notification.requestPermission();
		if (perm !== 'granted') throw new Error('Quyền thông báo bị từ chối');

		const reg = await this.getRegistration();
		if (!reg) throw new Error('Service worker chưa sẵn sàng');

		const vapid = await this.vapidKey();
		if (!vapid.enabled || !vapid.publicKey) throw new Error('Server chưa bật Web Push');

		// Reuse existing subscription if endpoint already registered (idempotent)
		let sub = await reg.pushManager.getSubscription();
		if (!sub) {
			sub = await reg.pushManager.subscribe({
				userVisibleOnly: true,
				applicationServerKey: urlBase64ToUint8Array(vapid.publicKey)
			});
		}

		const json = sub.toJSON();
		await api.post('/api/push/subscribe', {
			subscription: {
				endpoint: sub.endpoint,
				keys: {
					p256dh: json.keys?.p256dh ?? '',
					auth: json.keys?.auth ?? ''
				},
				expirationTime: sub.expirationTime ?? null
			},
			deviceLabel
		});

		return sub;
	},

	async unsubscribe(): Promise<void> {
		const sub = await this.currentSubscription();
		if (!sub) return;
		try {
			const q = encodeURIComponent(sub.endpoint);
			await fetch(`/api/push/subscribe?endpoint=${q}`, {
				method: 'DELETE',
				headers: { Authorization: `Bearer ${localStorage.getItem('cb_token') ?? ''}` }
			});
		} finally {
			await sub.unsubscribe();
		}
	}
};
