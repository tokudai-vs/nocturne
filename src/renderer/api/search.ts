import type { ItemsResult, IpcResponse } from './types';

export async function search(
  term: string,
  filters?: Record<string, unknown>,
): Promise<IpcResponse<ItemsResult>> {
  return window.api.search.query(term, filters);
}
