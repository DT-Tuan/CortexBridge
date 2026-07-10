import { api } from './client';

// ADR-025 Phase 4 Slice 2 — read side of the memory cockpit. The bridge proxies
// CortexPlexus's MCP memory tools; these go through lib/api/client.ts (bearer
// auto-added). A CortexPlexus outage surfaces as a 503 cortexplexus.unavailable.

export type CortexMemory = {
	id: string | null;
	content: string | null;
	scope: string | null;
	repository: string | null;
	topic: string | null;
	importance: number | null;
	score: number | null;
	createdAt: string | null;
	lastAccessedAt: string | null;
	accessCount: number | null;
};

export type CortexMemoryList = {
	count: number;
	memories: CortexMemory[];
};

export type CortexSaveInput = {
	content: string;
	scope: 'project' | 'global';
	topic: 'preference' | 'pattern' | 'decision' | 'bug' | 'todo' | 'note';
	repo?: string;
	importance?: number;
};

export const cortexApi = {
	// scope 'all' (cross-project) or 'project' (requires repo = workspace dir;
	// the bridge remaps any divergent pair via CortexPlexus:RepoNameMap). Returns the recent set
	// (list_memories); the cockpit filters it client-side. Semantic recall is
	// NOT used here — it's 30–50 s on this LXC (deferred to an async slice).
	memories: (opts: { scope: 'project' | 'all'; repo?: string; limit?: number }) => {
		const p = new URLSearchParams({ scope: opts.scope });
		if (opts.repo) p.set('repo', opts.repo);
		if (opts.limit) p.set('limit', String(opts.limit));
		return api.get<CortexMemoryList>(`/api/cortex/memories?${p.toString()}`);
	},
	repositories: () => api.get<string[]>('/api/cortex/repositories'),
	// Save a new memory — ASYNC (202). The bridge embeds the content off-request
	// (50–70 s on the LXC); the row appears on a later refetch. scope=project needs repo.
	save: (input: CortexSaveInput) => api.post<{ accepted: boolean }>('/api/cortex/memories', input),
	// Forget a memory by id (instant).
	forget: (id: string) =>
		api.delete<{ forgotten: boolean; id: string }>(
			`/api/cortex/memories/${encodeURIComponent(id)}`
		)
};
