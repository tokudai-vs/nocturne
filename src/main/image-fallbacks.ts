import { serverManager } from './server-manager';
import { getGroupVersions, type ItemRow } from './database';

export interface FallbackBundle {
  imageFallbacks: string[];
  backdropFallbacks: string[];
}

const IMAGE_MAX_WIDTH = 500;
const BACKDROP_MAX_WIDTH = 1920;

function buildEmbyImageUrl(
  serverId: string,
  embyId: string,
  imageType: 'Primary' | 'Backdrop' | 'Thumb',
  tag: string | null,
  maxWidth: number,
): string | null {
  const server = serverManager.getServer(serverId);
  if (!server) return null;
  const params = new URLSearchParams();
  params.set('maxWidth', String(maxWidth));
  if (tag) params.set('tag', tag);
  params.set('quality', '90');
  params.set('api_key', server.accessToken);
  return `${server.url}/emby/Items/${encodeURIComponent(embyId)}/Images/${imageType}?${params.toString()}`;
}

function safeParseTags(json: string | null): Record<string, string> {
  if (!json) return {};
  try {
    const parsed = JSON.parse(json);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function safeParseBackdrops(json: string | null): string[] {
  if (!json) return [];
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? parsed.filter((t): t is string => typeof t === 'string') : [];
  } catch {
    return [];
  }
}

/**
 * For a given primary item, return the URLs of every dedup-sibling's
 * Primary/Backdrop images, ordered by `getGroupVersions` (the same library_name
 * order used elsewhere for version selection). Siblings without a tag for
 * the requested image type are skipped — fallbacks are only built from
 * versions that actually have an image to serve.
 *
 * Empty arrays for items not in a dedup group, or items whose siblings have
 * no image tags.
 */
export function buildImageFallbackUrls(primary: ItemRow): FallbackBundle {
  if (!primary.dedup_group_id) {
    return { imageFallbacks: [], backdropFallbacks: [] };
  }
  const versions = getGroupVersions(primary.dedup_group_id, primary);
  const imageFallbacks: string[] = [];
  const backdropFallbacks: string[] = [];

  for (const v of versions) {
    if (v.emby_id === primary.emby_id) continue; // skip self
    const tags = safeParseTags(v.image_tags);
    const primaryTag = tags.Primary || tags.Thumb || null;
    if (primaryTag) {
      const url = buildEmbyImageUrl(v.server_id, v.emby_id, 'Primary', primaryTag, IMAGE_MAX_WIDTH);
      if (url) imageFallbacks.push(url);
    }
    const backdrops = safeParseBackdrops(v.backdrop_tags);
    if (backdrops.length > 0) {
      const url = buildEmbyImageUrl(v.server_id, v.emby_id, 'Backdrop', backdrops[0], BACKDROP_MAX_WIDTH);
      if (url) backdropFallbacks.push(url);
    }
  }

  if (imageFallbacks.length > 0 || backdropFallbacks.length > 0) {
    console.log(
      `[image-fallback] built ${imageFallbacks.length} primary + ${backdropFallbacks.length} backdrop `
        + `fallback URLs for ${primary.emby_id} (group ${primary.dedup_group_id}, primary "${primary.name}")`,
    );
  }
  return { imageFallbacks, backdropFallbacks };
}

/**
 * Bulk-attach fallback arrays to an array of rows. Mutates rows in place by
 * adding `image_fallbacks` and `backdrop_fallbacks` columns; non-dedup rows
 * get empty arrays (omitted by JSON serialization-of-undefined would be
 * fine too, but explicit empty keeps the renderer's shape inspection
 * predictable).
 */
export function attachFallbacksToItems<T extends ItemRow>(rows: T[]): T[] {
  for (const row of rows) {
    if (!row.dedup_group_id) continue;
    const bundle = buildImageFallbackUrls(row);
    if (bundle.imageFallbacks.length === 0 && bundle.backdropFallbacks.length === 0) continue;
    (row as T & { image_fallbacks?: string[]; backdrop_fallbacks?: string[] }).image_fallbacks = bundle.imageFallbacks;
    (row as T & { image_fallbacks?: string[]; backdrop_fallbacks?: string[] }).backdrop_fallbacks = bundle.backdropFallbacks;
  }
  return rows;
}
