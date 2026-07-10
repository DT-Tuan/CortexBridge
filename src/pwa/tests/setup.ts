// Vitest global setup
import '@testing-library/svelte/vitest';

// jsdom doesn't ship localStorage in older versions — newer versions do, this is a safety net.
if (typeof globalThis.localStorage === 'undefined') {
	const store = new Map<string, string>();
	globalThis.localStorage = {
		getItem: (k: string) => store.get(k) ?? null,
		setItem: (k: string, v: string) => void store.set(k, String(v)),
		removeItem: (k: string) => void store.delete(k),
		clear: () => store.clear(),
		key: (i: number) => Array.from(store.keys())[i] ?? null,
		get length() { return store.size; }
	} as Storage;
}
