/// <reference lib="webworker" />
import { precacheAndRoute, cleanupOutdatedCaches, addRoute } from 'workbox-precaching';

declare const self: ServiceWorkerGlobalScope;

// vite-plugin-pwa (injectManifest mode) replaces self.__WB_MANIFEST with the
// build manifest at compile time. globPatterns in vite.config covers .webmanifest
// + icons so we don't need a manual fallback list (which previously caused
// `add-to-cache-list-conflicting-entries` because URL keys overlapped with
// different revisions).
const buildManifest =
	(self as unknown as { __WB_MANIFEST?: Array<{ url: string; revision: string | null }> })
		.__WB_MANIFEST ?? [];

precacheAndRoute(buildManifest);
addRoute();
cleanupOutdatedCaches();

// Take control of clients as soon as possible (avoids stale SW serving old PWA).
self.addEventListener('install', () => {
	self.skipWaiting();
});
self.addEventListener('activate', (event) => {
	event.waitUntil(self.clients.claim());
});

interface PushPayload {
	title?: string;
	body?: string;
	url?: string;
	projectId?: string;
	ts?: string;
}

self.addEventListener('push', (event) => {
	if (!event.data) return;

	let payload: PushPayload & { clear?: boolean } = {};
	try {
		payload = event.data.json() as PushPayload & { clear?: boolean };
	} catch {
		payload = { title: 'CortexBridge', body: event.data.text() };
	}

	const tag = payload.projectId ?? 'cortexbridge';

	// Server can push { clear: true, projectId } when needsInput goes back to false
	// (user replied from another device, or Stop hook fired). Dismiss any lingering
	// lockscreen notification for this project — don't show a new one.
	if (payload.clear) {
		event.waitUntil((async () => {
			const list = await self.registration.getNotifications({ tag });
			for (const n of list) n.close();
		})());
		return;
	}

	const title = payload.title ?? 'CortexBridge';
	const options: NotificationOptions = {
		body: payload.body ?? 'Claude needs input',
		icon: '/icon-192.png',
		badge: '/icon-192.png',
		// Same projectId → replaces previous notification for this project (no spam)
		tag,
		data: { url: payload.url ?? '/', projectId: payload.projectId },
		requireInteraction: false
	};

	event.waitUntil(self.registration.showNotification(title, options));
});

// Allow PWA pages to ask SW to dismiss notifications for a given project tag
// (e.g. user just replied — kill the iPhone lockscreen notification that's still
// hanging around from a slightly-late Web Push delivery).
self.addEventListener('message', (event) => {
	const data = (event.data ?? {}) as { type?: string; tag?: string };
	if (data.type !== 'closeNotifications' || !data.tag) return;
	event.waitUntil((async () => {
		const list = await self.registration.getNotifications({ tag: data.tag });
		for (const n of list) n.close();
	})());
});

self.addEventListener('notificationclick', (event) => {
	event.notification.close();
	const data = (event.notification.data as { url?: string; projectId?: string } | undefined) ?? {};
	const url = data.url ?? '/';
	// iOS ignores Notification.actions so action buttons aren't surfaced — every tap
	// hits the body and just navigates to the chat view. In-PWA banner there renders
	// the 1/2/3 quick-reply buttons from needsInput state.

	event.waitUntil(
		(async () => {
			const allClients = await self.clients.matchAll({
				type: 'window',
				includeUncontrolled: true
			});
			for (const client of allClients) {
				try {
					const clientUrl = new URL(client.url);
					if (clientUrl.origin === self.location.origin) {
						await client.focus();
						if ('navigate' in client) {
							await (client as WindowClient).navigate(url);
						}
						return;
					}
				} catch {
					/* skip */
				}
			}
			await self.clients.openWindow(url);
		})()
	);
});
