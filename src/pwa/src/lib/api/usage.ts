import { api } from './client';
import type { UsageResponse, UsageHistoryResponse } from './types';

export const usageApi = {
	get: () => api.get<UsageResponse>('/api/usage'),
	history: (range: '24h' | '7d' = '24h') =>
		api.get<UsageHistoryResponse>(`/api/usage/history?range=${range}`)
};
