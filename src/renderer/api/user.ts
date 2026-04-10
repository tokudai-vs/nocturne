import type { EmbyUser, IpcResponse } from './types';

export async function getCurrentUser(): Promise<IpcResponse<EmbyUser>> {
  return window.api.user.getCurrentUser();
}

export async function markPlayed(itemId: string): Promise<IpcResponse<void>> {
  return window.api.user.markPlayed(itemId);
}

export async function markUnplayed(itemId: string): Promise<IpcResponse<void>> {
  return window.api.user.markUnplayed(itemId);
}

export async function updateFavorite(
  itemId: string,
  isFavorite: boolean,
): Promise<IpcResponse<void>> {
  return window.api.user.updateFavorite(itemId, isFavorite);
}
