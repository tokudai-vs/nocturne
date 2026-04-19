import { randomUUID } from 'crypto';
import {
  clearDedupGroups,
  insertDedupGroup,
  setItemDedupGroup,
  findTmdbDuplicates,
  findImdbDuplicates,
  findNameYearDuplicates,
  dissolveSingletonGroups,
  backfillEpisodeDedupGroups,
  getItem,
  withWriteTx,
  type ItemRow,
} from './database';

export type DedupPassName =
  | 'tmdb-movie'
  | 'imdb-movie'
  | 'name-movie'
  | 'tmdb-series'
  | 'imdb-series'
  | 'name-series';

export type DedupPhase = DedupPassName | 'clear' | 'backfill-episodes' | 'dissolve';

export interface DedupProgress {
  phase: DedupPhase;
  current: number;
  total: number;
  detail: string;
}

export interface DedupPassResult {
  success: boolean;
  raw: number;
  groups: number;
  itemsMerged: number;
  error?: string;
}

export interface DedupRunResult {
  groupsCreated: number;
  itemsMerged: number;
  episodesLinked: number;
  passResults: Record<DedupPassName, DedupPassResult>;
}

/** Yield to the event loop so other IPC/UI work can run between passes. */
function yieldTick(): Promise<void> {
  return new Promise((r) => setImmediate(r));
}

function emptyPassResult(): DedupPassResult {
  return { success: false, raw: 0, groups: 0, itemsMerged: 0 };
}

/** Score an item for "primary" selection within a dedup group. */
function scorePrimary(item: ItemRow): number {
  let score = 0;
  if (item.backdrop_tags && item.backdrop_tags !== '[]') score += 3;
  if (item.image_tags) {
    try {
      const tags = JSON.parse(item.image_tags);
      if (tags.Primary) score += 2;
    } catch { /* ignore */ }
  }
  if (item.overview) score += 2;
  if (item.community_rating) score += 1;
  if (item.genres && item.genres !== '[]') score += 1;
  if (item.studios && item.studios !== '[]') score += 1;
  return score;
}

/** Pick the best item as primary within a group */
function choosePrimary(itemIds: string[]): { primaryId: string; primaryItem: ItemRow | undefined } {
  let best: ItemRow | undefined;
  let bestScore = -1;

  for (const id of itemIds) {
    const item = getItem(id);
    if (!item) continue;
    const s = scorePrimary(item);
    if (s > bestScore) {
      bestScore = s;
      best = item;
    }
  }

  return { primaryId: best?.emby_id ?? itemIds[0], primaryItem: best };
}

export class DedupEngine {
  /**
   * Run deduplication across all items. Each pass runs inside a single write
   * transaction (so N group writes become 1 WAL fsync, not N), and we yield to
   * the event loop between passes so UI/IPC stay responsive. Per-pass try/catch
   * isolates failures.
   */
  async buildDedupGroups(onProgress?: (p: DedupProgress) => void): Promise<DedupRunResult> {
    const TOTAL_STEPS = 9; // 1 clear + 6 passes + 1 backfill + 1 dissolve

    const emit = (phase: DedupPhase, current: number, detail: string): void => {
      onProgress?.({ phase, current, total: TOTAL_STEPS, detail });
    };

    emit('clear', 0, 'Clearing previous dedup groups...');
    clearDedupGroups();
    await yieldTick();

    const groupedIds = new Set<string>();
    const passResults: Record<DedupPassName, DedupPassResult> = {
      'tmdb-movie': emptyPassResult(),
      'imdb-movie': emptyPassResult(),
      'name-movie': emptyPassResult(),
      'tmdb-series': emptyPassResult(),
      'imdb-series': emptyPassResult(),
      'name-series': emptyPassResult(),
    };

    const passes: {
      name: DedupPassName;
      label: string;
      run: () => { raw: number; groups: number; itemsMerged: number };
    }[] = [
      { name: 'tmdb-movie',  label: 'Matching movies by TMDB ID...',    run: () => this.groupByTmdb('Movie', groupedIds) },
      { name: 'imdb-movie',  label: 'Matching movies by IMDB ID...',    run: () => this.groupByImdb('Movie', groupedIds, false) },
      { name: 'name-movie',  label: 'Matching movies by name + year...', run: () => this.groupByNameYear('Movie', groupedIds, false) },
      { name: 'tmdb-series', label: 'Matching series by TMDB ID...',    run: () => this.groupByTmdb('Series', groupedIds, true) },
      { name: 'imdb-series', label: 'Matching series by IMDB ID...',    run: () => this.groupByImdb('Series', groupedIds, true) },
      { name: 'name-series', label: 'Matching series by name + year...', run: () => this.groupByNameYear('Series', groupedIds, true) },
    ];

    let step = 1;
    for (const pass of passes) {
      emit(pass.name, step, pass.label);
      passResults[pass.name] = this.runPass(pass.name, pass.run);
      step++;
      await yieldTick();
    }

    // Backfill episode dedup_group_id from their parent series' group so that
    // resume/search queries collapse episodes across series versions.
    emit('backfill-episodes', step, 'Linking episodes to series groups...');
    let episodesLinked = 0;
    try {
      episodesLinked = await backfillEpisodeDedupGroups();
      console.log(`[dedup] backfill-episodes: linked ${episodesLinked} episodes`);
    } catch (err) {
      console.error('[dedup] backfill-episodes failed:', err);
    }
    step++;
    await yieldTick();

    emit('dissolve', step, 'Dissolving singleton groups...');
    try {
      dissolveSingletonGroups();
    } catch (err) {
      console.error('[dedup] dissolveSingletonGroups failed:', err);
    }

    let groupsCreated = 0;
    let itemsMerged = 0;
    for (const r of Object.values(passResults)) {
      groupsCreated += r.groups;
      itemsMerged += r.itemsMerged;
    }

    return { groupsCreated, itemsMerged, episodesLinked, passResults };
  }

  private runPass(
    name: DedupPassName,
    fn: () => { raw: number; groups: number; itemsMerged: number },
  ): DedupPassResult {
    try {
      const { raw, groups, itemsMerged } = withWriteTx(fn);
      console.log(`[dedup] ${name}: raw=${raw} groups=${groups} merged=${itemsMerged}`);
      return { success: true, raw, groups, itemsMerged };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      console.error(`[dedup] ${name} FAILED: ${message}`);
      return { success: false, raw: 0, groups: 0, itemsMerged: 0, error: message };
    }
  }

  private groupByTmdb(
    type: 'Movie' | 'Series',
    groupedIds: Set<string>,
    buildEpisodeXref = false,
  ): { raw: number; groups: number; itemsMerged: number } {
    const dups = findTmdbDuplicates(type);
    let groups = 0;
    let itemsMerged = 0;

    for (const dup of dups) {
      const groupId = `tmdb-${type.toLowerCase()}-${dup.tmdb_id}`;
      const { primaryId, primaryItem } = choosePrimary(dup.item_ids);

      insertDedupGroup({
        group_id: groupId,
        tmdb_id: dup.tmdb_id,
        imdb_id: null,
        type,
        name: primaryItem?.name ?? dup.names[0] ?? '',
        year: primaryItem?.production_year ?? dup.years[0] ?? null,
        primary_item_id: primaryId,
      });
      setItemDedupGroup(dup.item_ids, groupId);

      for (const id of dup.item_ids) groupedIds.add(id);
      groups++;
      itemsMerged += dup.item_ids.length;

      if (buildEpisodeXref) this.buildEpisodeCrossRef(groupId, dup.item_ids);
    }

    return { raw: dups.length, groups, itemsMerged };
  }

  private groupByImdb(
    type: 'Movie' | 'Series',
    groupedIds: Set<string>,
    buildEpisodeXref: boolean,
  ): { raw: number; groups: number; itemsMerged: number } {
    const dups = findImdbDuplicates(type, groupedIds);
    let groups = 0;
    let itemsMerged = 0;

    for (const dup of dups) {
      const groupId = `imdb-${type.toLowerCase()}-${dup.imdb_id}`;
      const { primaryId, primaryItem } = choosePrimary(dup.item_ids);

      insertDedupGroup({
        group_id: groupId,
        tmdb_id: null,
        imdb_id: dup.imdb_id,
        type,
        name: primaryItem?.name ?? '',
        year: primaryItem?.production_year ?? null,
        primary_item_id: primaryId,
      });
      setItemDedupGroup(dup.item_ids, groupId);

      for (const id of dup.item_ids) groupedIds.add(id);
      groups++;
      itemsMerged += dup.item_ids.length;

      if (buildEpisodeXref) this.buildEpisodeCrossRef(groupId, dup.item_ids);
    }

    return { raw: dups.length, groups, itemsMerged };
  }

  private groupByNameYear(
    type: 'Movie' | 'Series',
    groupedIds: Set<string>,
    buildEpisodeXref: boolean,
  ): { raw: number; groups: number; itemsMerged: number } {
    const dups = findNameYearDuplicates(type);
    let groups = 0;
    let itemsMerged = 0;

    for (const dup of dups) {
      const groupId = `name-${type.toLowerCase()}-${randomUUID().slice(0, 8)}`;
      const { primaryId, primaryItem } = choosePrimary(dup.item_ids);

      insertDedupGroup({
        group_id: groupId,
        tmdb_id: null,
        imdb_id: null,
        type,
        name: primaryItem?.name ?? dup.name,
        year: primaryItem?.production_year ?? dup.year,
        primary_item_id: primaryId,
      });
      setItemDedupGroup(dup.item_ids, groupId);

      for (const id of dup.item_ids) groupedIds.add(id);
      groups++;
      itemsMerged += dup.item_ids.length;

      if (buildEpisodeXref) this.buildEpisodeCrossRef(groupId, dup.item_ids);
    }

    return { raw: dups.length, groups, itemsMerged };
  }

  /**
   * Episode cross-references are resolved at query time in getEpisodeVersions
   * via the series-level dedup_group_id — no additional storage needed here.
   */
  private buildEpisodeCrossRef(_seriesGroupId: string, _seriesItemIds: string[]): void {
    // intentional no-op; kept as a hook for future precomputed episode joins
  }
}

export const dedupEngine = new DedupEngine();
