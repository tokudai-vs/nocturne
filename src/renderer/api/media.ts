import type { MediaSource, IpcResponse } from './types';

export async function getPlaybackInfo(
  itemId: string,
): Promise<IpcResponse<{ MediaSources: MediaSource[] }>> {
  return window.api.media.getPlaybackInfo(itemId);
}

export async function getStreamUrl(
  itemId: string,
  mediaSourceId: string,
): Promise<IpcResponse<string>> {
  return window.api.media.getStreamUrl(itemId, mediaSourceId);
}

export async function reportPlaybackStart(
  data: Record<string, unknown>,
): Promise<IpcResponse<void>> {
  return window.api.media.reportStart(data);
}

export async function reportPlaybackProgress(
  data: Record<string, unknown>,
): Promise<IpcResponse<void>> {
  return window.api.media.reportProgress(data);
}

export async function reportPlaybackStopped(
  data: Record<string, unknown>,
): Promise<IpcResponse<void>> {
  return window.api.media.reportStop(data);
}
