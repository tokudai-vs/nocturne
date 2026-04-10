import type { BaseItemDto, ItemsResult, IpcResponse } from './types';

export async function getViews(): Promise<IpcResponse<ItemsResult>> {
  return window.api.library.getViews();
}

export async function getItems(
  parentId: string,
  params?: Record<string, unknown>,
): Promise<IpcResponse<ItemsResult>> {
  return window.api.library.getItems(parentId, params);
}

export async function getItem(itemId: string): Promise<IpcResponse<BaseItemDto>> {
  return window.api.library.getItem(itemId);
}

export async function getLatest(
  parentId: string,
  limit?: number,
): Promise<IpcResponse<BaseItemDto[]>> {
  return window.api.library.getLatest(parentId, limit);
}

export async function getResume(): Promise<IpcResponse<ItemsResult>> {
  return window.api.library.getResume();
}

export async function getNextUp(): Promise<IpcResponse<ItemsResult>> {
  return window.api.library.getNextUp();
}

export async function getSimilar(itemId: string): Promise<IpcResponse<ItemsResult>> {
  return window.api.library.getSimilar(itemId);
}

export async function getSeasons(seriesId: string): Promise<IpcResponse<ItemsResult>> {
  return window.api.library.getSeasons(seriesId);
}

export async function getEpisodes(
  seriesId: string,
  seasonId: string,
): Promise<IpcResponse<ItemsResult>> {
  return window.api.library.getEpisodes(seriesId, seasonId);
}
