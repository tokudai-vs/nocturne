import { getSettings } from './settings';
import { serverManager } from './server-manager';
import { traktClient } from './trakt-client';
import {
  countTraktWatchlist,
  findItemsByImdbId,
  findItemsByNameAndYear,
  findItemsByTmdbId,
  getDedupPrimaryItem,
  getItem,
  getItemsMultiLibrary,
  getItemsMultiLibraryDeduped,
  getLatestMultiLibrary,
  getLatestMultiLibraryDeduped,
  getHeroCandidates,
  countItemsInLibraries,
  getSyncState,
  getGroupVersions,
  getEpisodeVersions,
  getDedupStats,
  getTraktWatchlistEntries,
  type ItemRow,
} from './database';

export const TRAKT_WATCHLIST_VLIB_ID = 'trakt:watchlist';

export interface VirtualLibrary {
  id: string;
  name: string;
  icon: string;
  libraryIds: string[];
  isVirtual: boolean;
  totalItems: number;
}

interface VlibItemsOpts {
  startIndex?: number;
  limit?: number;
  sortBy?: string;
  sortOrder?: string;
  searchTerm?: string;
  itemType?: string;
}

export function getVirtualLibraries(): VirtualLibrary[] {
  const base = serverManager.isCombinedMode()
    ? getCombinedVirtualLibraries()
    : getSeparateVirtualLibraries();
  // Prepend the Trakt Watchlist sentinel when connected and the toggle is on.
  // The sidebar count comes from trakt_watchlist (cheap COUNT(*)), updated by
  // background refresh; renderer doesn't need a live Trakt round-trip.
  const settings = getSettings();
  if (
    settings.traktShowWatchlistInSidebar !== false &&
    traktClient.isConnected()
  ) {
    return [
      {
        id: TRAKT_WATCHLIST_VLIB_ID,
        name: 'Trakt Watchlist',
        icon: 'Star',
        libraryIds: [],
        isVirtual: true,
        totalItems: countTraktWatchlist(),
      },
      ...base,
    ];
  }
  return base;
}

function getSeparateVirtualLibraries(): VirtualLibrary[] {
  const settings = getSettings();
  const serverMappings = serverManager.getActiveLibraryMappings();
  const result: VirtualLibrary[] = [];

  // Add mapped groups for the active server
  for (const [groupId, mapping] of Object.entries(serverMappings)) {
    result.push({
      id: groupId,
      name: mapping.name,
      icon: mapping.icon,
      libraryIds: mapping.libraryIds,
      isVirtual: true,
      totalItems: countItemsInLibraries(mapping.libraryIds),
    });
  }

  // Add unmapped libraries if setting is enabled
  if (settings.showUnmappedLibraries) {
    const mappedIds = new Set(
      Object.values(serverMappings).flatMap((m) => m.libraryIds),
    );

    const libsRaw = getSyncState('libraries');
    if (libsRaw) {
      try {
        const libraries: { Id: string; Name: string }[] = JSON.parse(libsRaw);
        for (const lib of libraries) {
          if (!mappedIds.has(lib.Id)) {
            result.push({
              id: lib.Id,
              name: lib.Name,
              icon: 'Folder',
              libraryIds: [lib.Id],
              isVirtual: false,
              totalItems: countItemsInLibraries([lib.Id]),
            });
          }
        }
      } catch {
        // Invalid JSON, skip
      }
    }
  }

  return result;
}

function getCombinedVirtualLibraries(): VirtualLibrary[] {
  const settings = getSettings();
  const combined = settings.combinedMappings || {};
  const result: VirtualLibrary[] = [];

  // Diagnostic block — gated behind DEBUG_VLIB env var because the full
  // allLibs dump is ~2KB and `getVirtualLibraries()` runs on every
  // `vlib:get-all` IPC (multiple times per startup). Set DEBUG_VLIB=1 to
  // re-enable when investigating sidebar / combined-mode bugs.
  const diagAllLibs = serverManager.getAllServerLibraries();
  if (process.env.DEBUG_VLIB) {
    console.log('[vlib] combined', {
      mappingsCount: Object.keys(combined).length,
      showUnmapped: settings.showUnmappedLibraries,
      allLibsCount: diagAllLibs.length,
      allLibs: diagAllLibs,
    });
  }

  // Add combined groups (cross-server)
  for (const [groupId, mapping] of Object.entries(combined)) {
    const libraryIds = mapping.libraries.map((l) => l.libraryId);
    result.push({
      id: groupId,
      name: mapping.name,
      icon: mapping.icon,
      libraryIds,
      isVirtual: true,
      totalItems: countItemsInLibraries(libraryIds),
    });
  }

  // Append unmapped libraries as individual entries when the toggle is on.
  // With no groups, every library is "unmapped" — so this path also covers the
  // fresh-install / all-groups-deleted state. When the toggle is off and there
  // are no groups, we intentionally return an empty list.
  if (settings.showUnmappedLibraries) {
    const mappedIds = new Set(
      Object.values(combined).flatMap((m) => m.libraries.map((l) => l.libraryId)),
    );
    const allLibs = diagAllLibs;
    const unmapped = allLibs.filter((l) => !mappedIds.has(l.libraryId));

    const nameCounts = new Map<string, number>();
    for (const lib of unmapped) {
      nameCounts.set(lib.libraryName, (nameCounts.get(lib.libraryName) || 0) + 1);
    }
    for (const lib of unmapped) {
      const count = nameCounts.get(lib.libraryName) || 0;
      const displayName = count > 1 ? `${lib.libraryName} (${lib.serverName})` : lib.libraryName;
      result.push({
        id: lib.libraryId,
        name: displayName,
        icon: 'Folder',
        libraryIds: [lib.libraryId],
        isVirtual: false,
        totalItems: countItemsInLibraries([lib.libraryId]),
      });
    }
  }

  return result;
}

export function getVirtualLibraryItems(
  vlibId: string,
  opts: VlibItemsOpts = {},
): { items: ItemRow[]; total: number } {
  const libraryIds = resolveLibraryIds(vlibId);
  // Use deduped query for multi-library vlibs
  if (libraryIds.length > 1) {
    return getItemsMultiLibraryDeduped(libraryIds, opts);
  }
  return getItemsMultiLibrary(libraryIds, opts);
}

export function getVirtualLibraryLatest(vlibId: string, limit = 20): ItemRow[] {
  const libraryIds = resolveLibraryIds(vlibId);
  if (libraryIds.length > 1) {
    return getLatestMultiLibraryDeduped(libraryIds, limit);
  }
  return getLatestMultiLibrary(libraryIds, limit);
}

export function getVirtualLibraryHeroes(vlibId: string | null, limit = 20): ItemRow[] {
  if (!vlibId) {
    // All libraries
    return getHeroCandidates([], limit);
  }
  const libraryIds = resolveLibraryIds(vlibId);
  return getHeroCandidates(libraryIds, limit);
}

export function getItemVersions(itemId: string): ItemRow[] {
  const item = getItem(itemId);
  if (!item?.dedup_group_id) return [];
  return getGroupVersions(item.dedup_group_id, item);
}

export function getSeriesEpisodeVersions(
  seriesItemId: string,
  seasonNumber: number,
): { season_number: number; episode_number: number; items: ItemRow[] }[] {
  const item = getItem(seriesItemId);
  if (!item?.dedup_group_id) return [];
  return getEpisodeVersions(item.dedup_group_id, seasonNumber);
}

export function getVirtualLibraryDedupStats(): { groupCount: number; mergedItems: number } {
  return getDedupStats();
}

type TraktWatchlistItem = ItemRow & { is_external?: boolean; trakt_key?: string; trakt_type?: string };

// In-memory cache of the matched watchlist. The match scan is deterministic
// given trakt_watchlist + cached_items + dedup_groups, so we recompute it
// only when one of those changes (refresh, add/remove, sync, dedup) instead
// of on every IPC read. Without this the matcher (and its `[trakt-watchlist]
// match:` log) re-runs on every HomePage row fetch and every renderer-side
// store update.
let watchlistCache: TraktWatchlistItem[] | null = null;

export function invalidateTraktWatchlistCache(): void {
  watchlistCache = null;
}

/**
 * Watchlist as virtual library. Each Trakt watchlist entry is mapped to the
 * matched local CachedItem when one exists; otherwise a synthetic row with
 * `is_external: true` so the renderer can grey it out and offer "find this".
 */
export function getTraktWatchlistAsCachedItems(): TraktWatchlistItem[] {
  if (watchlistCache) return watchlistCache;
  const entries = getTraktWatchlistEntries();
  const out: TraktWatchlistItem[] = [];
  const seenEmbyIds = new Set<string>();
  let externalCount = 0;

  for (const entry of entries) {
    const localType: 'Movie' | 'Series' = entry.trakt_type === 'show' ? 'Series' : 'Movie';
    let matches: ItemRow[] = [];
    let matchSource: 'tmdb' | 'imdb' | 'name+year' | null = null;
    if (entry.tmdb_id) {
      matches = findItemsByTmdbId(entry.tmdb_id, localType);
      if (matches.length > 0) matchSource = 'tmdb';
    }
    if (matches.length === 0 && entry.imdb_id) {
      matches = findItemsByImdbId(entry.imdb_id, localType);
      if (matches.length > 0) matchSource = 'imdb';
    }
    // Bug 4 fallback: titles whose Emby metadata doesn't have tmdb/imdb (the
    // `Hoppers` case). Last-resort exact (case-insensitive, trimmed) match
    // on title + production_year. Year-anchored to avoid same-titled false
    // positives.
    if (matches.length === 0 && entry.title && entry.year) {
      matches = findItemsByNameAndYear(entry.title, entry.year, localType);
      if (matches.length > 0) matchSource = 'name+year';
    }

    if (matches.length > 0) {
      // Pick the dedup-group's canonical primary so the watchlist card
      // shows the SAME row the library grid uses (with proper image_tags
      // and the right server_id). Without this, we'd often pick a
      // secondary copy whose Emby metadata didn't include image tags,
      // and the card would fall through to the letter placeholder.
      const groupId = matches.find((m) => m.dedup_group_id)?.dedup_group_id ?? null;
      const dedupPrimary = groupId ? getDedupPrimaryItem(groupId) : undefined;
      const primary = dedupPrimary ?? matches[0];
      if (seenEmbyIds.has(primary.emby_id)) continue;
      seenEmbyIds.add(primary.emby_id);
      // Diagnostic so we can confirm matched rows have the metadata
      // MediaCard needs (image_tags + server_id). If `tag` is `-`, the
      // local item has no Primary image cached and the card will show
      // its letter placeholder regardless of the matching path.
      let primaryTag: string | null = null;
      try {
        const tags = primary.image_tags ? JSON.parse(primary.image_tags) as Record<string, string> : {};
        primaryTag = tags.Primary ?? null;
      } catch { /* ignore */ }
      console.log(
        `[trakt-watchlist] match: trakt={title="${entry.title ?? '?'}", year=${entry.year ?? '?'}, tmdb=${entry.tmdb_id ?? '-'}} `
          + `→ matched local emby_id=${primary.emby_id} tag=${primaryTag ?? '-'} server=${primary.server_id || '-'} (via ${matchSource ?? '?'}${dedupPrimary ? ', dedup-primary' : ''})`,
      );
      out.push({ ...primary, trakt_key: entry.key, trakt_type: entry.trakt_type });
    } else {
      externalCount++;
      // One concise log per unmatched entry — actionable for diagnosing why
      // a title shows up as "Not in library" when the user knows it is.
      console.log(
        `[trakt-watchlist] external "${entry.title ?? '?'}" (${entry.year ?? '?'}): `
          + `tmdb=${entry.tmdb_id ?? '-'}, imdb=${entry.imdb_id ?? '-'} — `
          + `no local match by tmdb / imdb / name+year`,
      );
      // Synthetic external row — not playable.
      const synthetic: ItemRow & { is_external: boolean; trakt_key: string; trakt_type: string } = {
        emby_id: `trakt:${entry.key}`,
        server_id: '',
        library_id: TRAKT_WATCHLIST_VLIB_ID,
        library_name: 'Trakt Watchlist',
        type: localType,
        name: entry.title ?? '(unknown)',
        sort_name: entry.title,
        overview: entry.overview,
        tmdb_id: entry.tmdb_id,
        imdb_id: entry.imdb_id,
        tvdb_id: null,
        production_year: entry.year,
        premiere_date: null,
        community_rating: null,
        official_rating: null,
        runtime_ticks: null,
        genres: null,
        studios: null,
        image_tags: null,
        backdrop_tags: null,
        series_id: null,
        series_name: null,
        season_id: null,
        season_number: null,
        episode_number: null,
        media_sources: null,
        played: 0,
        play_count: 0,
        is_favorite: 0,
        playback_position_ticks: 0,
        played_percentage: 0,
        date_created: entry.added_at,
        date_modified: null,
        cached_at: null,
        dedup_group_id: null,
        is_external: true,
        trakt_key: entry.key,
        trakt_type: entry.trakt_type,
      };
      out.push(synthetic);
    }
  }
  if (externalCount > 0) {
    console.log(`[trakt-watchlist] ${externalCount}/${entries.length} entries unmatched (rendered as external)`);
  }
  watchlistCache = out;
  return out;
}

function resolveLibraryIds(vlibId: string): string[] {
  if (serverManager.isCombinedMode()) {
    // Check combined mappings first
    const combined = serverManager.getCombinedMappings();
    const mapping = combined[vlibId];
    if (mapping) {
      return mapping.libraries.map((l) => l.libraryId);
    }
    // Fall through to raw ID
    return [vlibId];
  }

  // Separate mode: check per-server mappings
  const serverMappings = serverManager.getActiveLibraryMappings();
  const mapping = serverMappings[vlibId];
  if (mapping) {
    return mapping.libraryIds;
  }

  // Otherwise treat as a raw Emby library ID
  return [vlibId];
}
