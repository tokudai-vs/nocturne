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

export function buildCachedItemImageUrl(
  embyId: string,
  imageTagsJson: string | null,
  backdropTagsJson: string | null,
  imageType: 'Primary' | 'Backdrop' | 'Thumb',
  opts: { maxWidth?: number } = {},
): string | null {
  const { serverUrl, accessToken } = useAuthStore.getState();
  if (!serverUrl) return null;

  if (imageType === 'Backdrop') {
    let tags: string[] = [];
    try {
      tags = backdropTagsJson ? JSON.parse(backdropTagsJson) : [];
    } catch { /* ignore */ }
    if (tags.length === 0) return null;

    const params = new URLSearchParams();
    params.set('maxWidth', String(opts.maxWidth || 1920));
    params.set('tag', tags[0]);
    params.set('quality', '90');
    if (accessToken) params.set('api_key', accessToken);
    return `${serverUrl}/emby/Items/${embyId}/Images/Backdrop/0?${params}`;
  }

  let imageTags: Record<string, string> = {};
  try {
    imageTags = imageTagsJson ? JSON.parse(imageTagsJson) : {};
  } catch { /* ignore */ }

  const tag = imageTags[imageType];
  if (!tag) return null;

  const params = new URLSearchParams();
  params.set('maxWidth', String(opts.maxWidth || 300));
  params.set('tag', tag);
  params.set('quality', '90');
  if (accessToken) params.set('api_key', accessToken);
  return `${serverUrl}/emby/Items/${embyId}/Images/${imageType}?${params}`;
}
