import { useAuthStore } from '../stores/auth-store';
import type { ServerConfig } from '../api/types';

// serverId → { url, token } so image URLs resolve against the server that
// owns the item. In combined mode cached rows come from multiple servers;
// building every URL against the active server 404s for foreign item ids
// (the dedup fallback chain was silently papering over this for grouped
// items — ungrouped foreign items fell to the letter placeholder).
let serverMap: Record<string, { url: string; token: string }> = {};

export async function refreshImageServerMap(): Promise<void> {
  const res = await window.api.servers.getAll();
  if (res.success && Array.isArray(res.data)) {
    const map: Record<string, { url: string; token: string }> = {};
    for (const s of res.data as ServerConfig[]) {
      map[s.id] = { url: s.url, token: s.accessToken };
    }
    serverMap = map;
  }
}

function resolveServer(serverId?: string): { url: string; token: string } | null {
  if (serverId && serverMap[serverId]) return serverMap[serverId];
  const { serverUrl, accessToken } = useAuthStore.getState();
  if (!serverUrl) return null;
  return { url: serverUrl, token: accessToken ?? '' };
}

export function buildImageUrl(
  itemId: string,
  imageType: string,
  opts: { maxWidth?: number; maxHeight?: number; tag?: string; quality?: number } = {},
  serverId?: string,
): string {
  const server = resolveServer(serverId);
  if (!server) return '';

  const params = new URLSearchParams();
  if (opts.maxWidth) params.set('maxWidth', String(opts.maxWidth));
  if (opts.maxHeight) params.set('maxHeight', String(opts.maxHeight));
  if (opts.tag) params.set('tag', String(opts.tag));
  params.set('quality', String(opts.quality ?? 90));
  if (server.token) params.set('api_key', server.token);

  return `${server.url}/emby/Items/${itemId}/Images/${imageType}?${params}`;
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
  serverId?: string,
): string | null {
  const server = resolveServer(serverId);
  if (!server) return null;

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
    if (server.token) params.set('api_key', server.token);
    return `${server.url}/emby/Items/${embyId}/Images/Backdrop/0?${params}`;
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
  if (server.token) params.set('api_key', server.token);
  return `${server.url}/emby/Items/${embyId}/Images/${imageType}?${params}`;
}
