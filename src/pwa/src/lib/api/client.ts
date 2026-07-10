import { auth } from '$lib/stores/auth.svelte';

export interface ApiError {
	code: string;
	message: string;
}

export class ApiException extends Error {
	constructor(
		public status: number,
		public error: ApiError,
		public raw: string
	) {
		super(`${error.code}: ${error.message}`);
	}
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
	const headers: Record<string, string> = {};
	if (auth.token) headers['Authorization'] = `Bearer ${auth.token}`;
	if (body !== undefined) headers['Content-Type'] = 'application/json';

	const r = await fetch(path, {
		method,
		headers,
		body: body !== undefined ? JSON.stringify(body) : undefined
	});

	const text = await r.text();
	if (!r.ok) {
		let parsed: ApiError = { code: 'http.' + r.status, message: text };
		try {
			const j = JSON.parse(text);
			if (j?.error?.code) parsed = j.error;
		} catch {
			/* leave default */
		}
		throw new ApiException(r.status, parsed, text);
	}
	if (!text) return undefined as unknown as T;
	return JSON.parse(text) as T;
}

export const api = {
	get: <T>(path: string) => request<T>('GET', path),
	post: <T>(path: string, body?: unknown) => request<T>('POST', path, body),
	put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body),
	delete: <T>(path: string) => request<T>('DELETE', path)
};
