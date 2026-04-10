import { useAuthStore } from '../stores/auth-store';

export function buildImageUrl(
  itemId: string,
  imageType: string,
  opts: { maxWidth?: number; maxHeight?: number; tag?: string; quality?: number } = {},
): string {
  const { serverUrl, accessToken } = useAuthStore.getState();
  if (!serverUrl) return '';

  const params = new URLSearchParams();
  if (opts.maxWidth) params.set('maxWidth', String(opts.maxWidth));
  if (opts.maxHeight) params.set('maxHeight', String(opts.maxHeight));
  if (opts.tag) params.set('tag', String(opts.tag));
  params.set('quality', String(opts.quality ?? 90));
  if (accessToken) params.set('api_key', accessToken);

  return `${serverUrl}/emby/Items/${itemId}/Images/${imageType}?${params}`;
}

export function buildPersonImageUrl(
  personId: string,
  opts: { maxWidth?: number; tag?: string } = {},
): string {
  return buildImageUrl(personId, 'Primary', opts);
}
