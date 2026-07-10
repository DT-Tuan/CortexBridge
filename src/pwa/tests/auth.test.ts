import { describe, it, expect, beforeEach } from 'vitest';
import { auth } from '$lib/stores/auth.svelte';

describe('auth store', () => {
	beforeEach(() => {
		auth.clear();
	});

	it('starts unauthed when localStorage is empty', () => {
		expect(auth.isAuthed).toBe(false);
		expect(auth.token).toBeNull();
	});

	it('persists token to localStorage on set', () => {
		auth.set('cb_test123');
		expect(auth.token).toBe('cb_test123');
		expect(auth.isAuthed).toBe(true);
		expect(localStorage.getItem('cb_token')).toBe('cb_test123');
	});

	it('clears token from store and localStorage', () => {
		auth.set('cb_abc');
		auth.clear();
		expect(auth.token).toBeNull();
		expect(auth.isAuthed).toBe(false);
		expect(localStorage.getItem('cb_token')).toBeNull();
	});
});
