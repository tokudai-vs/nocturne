import type { IpcResponse } from './types';

export async function getImageUrl(
  itemId: string,
  imageType: string,
  params?: Record<string, unknown>,
): Promise<IpcResponse<string>> {
  return window.api.image.getUrl(itemId, imageType, params);
}

export async function getPersonImageUrl(
  personId: string,
  params?: Record<string, unknown>,
): Promise<IpcResponse<string>> {
  return window.api.image.getUrl(personId, 'Primary', params);
}
