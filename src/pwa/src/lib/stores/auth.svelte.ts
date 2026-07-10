const STORAGE_KEY = 'cb_token';

function load(): string | null {
	if (typeof localStorage === 'undefined') return null;
	return localStorage.getItem(STORAGE_KEY);
}

function createAuthStore() {
	let token = $state<string | null>(load());
	return {
		get token() {
			return token;
		},
		get isAuthed() {
			return !!token;
		},
		set(v: string) {
			token = v;
			localStorage.setItem(STORAGE_KEY, v);
		},
		clear() {
			token = null;
			localStorage.removeItem(STORAGE_KEY);
		}
	};
}

export const auth = createAuthStore();
