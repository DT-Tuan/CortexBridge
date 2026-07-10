import { describe, it, expect, beforeEach, vi } from 'vitest';
import { api, ApiException } from '$lib/api/client';
import { auth } from '$lib/stores/auth.svelte';

describe('api client', () => {
	beforeEach(() => {
		auth.clear();
		vi.restoreAllMocks();
	});

	it('attaches Authorization header when authed', async () => {
		auth.set('cb_test_token');
		const fetchMock = vi.fn().mockResolvedValue(new Response('{"ok":true}', { status: 200 }));
		vi.stubGlobal('fetch', fetchMock);

		await api.get<{ ok: boolean }>('/api/health');

		expect(fetchMock).toHaveBeenCalledOnce();
		const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>;
		expect(headers['Authorization']).toBe('Bearer cb_test_token');
	});

	it('omits Authorization header when not authed', async () => {
		const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }));
		vi.stubGlobal('fetch', fetchMock);

		await api.get('/api/health');

		const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>;
		expect(headers['Authorization']).toBeUndefined();
	});

	it('throws ApiException with parsed error body on 401', async () => {
		auth.set('cb_bad');
		const errBody = JSON.stringify({ error: { code: 'auth.invalid_token', message: 'bad' } });
		// Fresh Response each call — Response.body is single-use
		vi.stubGlobal('fetch', vi.fn().mockImplementation(() =>
			Promise.resolve(new Response(errBody, { status: 401 }))));

		try {
			await api.get('/api/sessions');
			throw new Error('should have thrown');
		} catch (e) {
			expect(e).toBeInstanceOf(ApiException);
			const ae = e as ApiException;
			expect(ae.status).toBe(401);
			expect(ae.error.code).toBe('auth.invalid_token');
		}
	});

	it('falls back to http.<status> code on non-JSON error body', async () => {
		auth.set('cb_test');
		vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('Internal Server Error', { status: 500 })));

		try {
			await api.get('/api/health');
			throw new Error('should have thrown');
		} catch (e) {
			const ae = e as ApiException;
			expect(ae.status).toBe(500);
			expect(ae.error.code).toBe('http.500');
		}
	});

	it('serializes JSON body on POST', async () => {
		auth.set('cb_test');
		const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }));
		vi.stubGlobal('fetch', fetchMock);

		await api.post('/api/sessions/x/reply', { text: 'Xin chào' });

		const init = fetchMock.mock.calls[0][1];
		expect(init.method).toBe('POST');
		expect(init.body).toBe('{"text":"Xin chào"}');
		expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json');
	});
});
