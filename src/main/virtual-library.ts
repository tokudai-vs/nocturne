import { getSettings } from './settings';
import { serverManager } from './server-manager';
import {
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
  type ItemRow,
} from './database';

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
  if (serverManager.isCombinedMode()) {
    return getCombinedVirtualLibraries();
  }
  return getSeparateVirtualLibraries();
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

  // TEMP DIAGNOSTIC — remove once sidebar-empty bug is confirmed.
  const diagAllLibs = serverManager.getAllServerLibraries();
  console.log('[vlib] combined', {
    mappingsCount: Object.keys(combined).length,
    showUnmapped: settings.showUnmappedLibraries,
    allLibsCount: diagAllLibs.length,
    allLibs: diagAllLibs,
  });

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
