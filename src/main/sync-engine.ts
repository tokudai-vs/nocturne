import { EventEmitter } from 'events';
import { embyClient } from './emby-client';
import {
  upsertItems,
  upsertItemsPreservingLibrary,
  getSyncState,
  setSyncState,
  deleteSyncState,
  hasAnyCachedItems,
  clearEpisodeSyncMarkers,
} from './database';
import { precacheImages } from './image-cache';
import { dedupEngine } from './dedup-engine';
import { serverManager } from './server-manager';

export interface SyncProgress {
  phase: string;
  current: number;
  total: number;
  detail: string;
  librariesDone?: number;
  librariesTotal?: number;
  percent: number;
}

export interface SyncStatus {
  running: boolean;
  phase: string | null;
  progress: SyncProgress | null;
  lastFullSync: string | null;
  hasCachedData: boolean;
  syncStatus: 'never' | 'in-progress' | 'partial' | 'complete';
  dedupStatus: 'never' | 'in-progress' | 'complete' | 'failed';
  lastDedupBuild: string | null;
  dedupRunning: boolean;
}

export interface DedupRunOutcome {
  success: boolean;
  groupsCreated?: number;
  itemsMerged?: number;
  error?: string;
}

interface LibraryView {
  Id: string;
  Name: string;
  CollectionType?: string;
}

interface Checkpoint {
  phase: number;
  libraryIndex: number;
  startIndex: number;
  seriesIndex?: number;
}

const BATCH_SIZE = 50;
const EPISODE_PAGE_SIZE = 1000;
const FULL_SYNC_DELAY = 500;
const INCREMENTAL_DELAY = 200;
const FIELDS = 'Overview,Genres,Studios,MediaSources,ProviderIds,DateCreated,UserData,ImageTags,BackdropImageTags,People,SortName';

function log(...args: unknown[]): void {
  console.log('[sync]', ...args);
}

class SyncEngine extends EventEmitter {
  private running = false;
  private cancelled = false;
  private dedupRunning = false;
  private currentProgress: SyncProgress | null = null;
  private currentPhase: string | null = null;
  // Handle for the pending background dedup rebuild scheduled by checkDedupDrift.
  // Tracked so a manual runDedup (or a second drift check) can cancel it before
  // it fires, avoiding duplicate dedup runs racing each other.
  private driftTimer: ReturnType<typeof setTimeout> | null = null;

  getStatus(): SyncStatus {
    const raw = getSyncState('syncStatus');
    const syncStatus: SyncStatus['syncStatus'] =
      raw === 'complete' || raw === 'partial' || raw === 'in-progress' ? raw : 'never';
    const rawDedup = getSyncState('dedupStatus');
    const dedupStatus: SyncStatus['dedupStatus'] =
      rawDedup === 'complete' || rawDedup === 'in-progress' || rawDedup === 'failed'
        ? rawDedup
        : 'never';
    return {
      running: this.running,
      phase: this.currentPhase,
      progress: this.currentProgress,
      lastFullSync: getSyncState('lastFullSync'),
      hasCachedData: hasAnyCachedItems(),
      syncStatus,
      dedupStatus,
      lastDedupBuild: getSyncState('lastDedupBuild'),
      dedupRunning: this.dedupRunning,
    };
  }

  /**
   * Rebuild dedup groups as a standalone, cancellable-independent phase.
   * Sync completion is already persisted before this runs, so a dedup
   * failure cannot roll back `syncStatus='complete'`.
   */
  async runDedup(): Promise<DedupRunOutcome> {
    if (this.dedupRunning) {
      log('runDedup: already running');
      return { success: false, error: 'Dedup is already running' };
    }
    // If a drift-triggered rebuild was pending, cancel it — this run supersedes it.
    if (this.driftTimer) {
      clearTimeout(this.driftTimer);
      this.driftTimer = null;
    }
    this.dedupRunning = true;
    const prevPhase = this.currentPhase;
    this.currentPhase = 'dedup';
    setSyncState('dedupStatus', 'in-progress');
    this.emitProgress('dedup', 0, 9, 'Starting dedup...');
    try {
      const result = await dedupEngine.buildDedupGroups((p) => {
        this.emitProgress('dedup', p.current, p.total, p.detail);
      });
      setSyncState('dedupStatus', 'complete');
      setSyncState('lastDedupBuild', new Date().toISOString());
      log(`runDedup: complete — ${result.groupsCreated} groups, ${result.itemsMerged} merged, ${result.episodesLinked} episodes linked`);
      this.emitProgress('dedup', 9, 9,
        `Dedup complete: ${result.groupsCreated} groups, ${result.itemsMerged} items merged`);
      this.emit('dedup-complete', result);
      return { success: true, groupsCreated: result.groupsCreated, itemsMerged: result.itemsMerged };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setSyncState('dedupStatus', 'failed');
      log(`runDedup: FAILED — ${message}`);
      this.emit('dedup-failed', { message });
      return { success: false, error: message };
    } finally {
      this.dedupRunning = false;
      if (!this.running) {
        this.currentPhase = prevPhase;
      }
    }
  }

  /**
   * On app launch, if dedup hasn't run in > `maxAgeDays`, schedule a background
   * rebuild 30s after ready so it doesn't compete with startup work. Prevents
   * silent drift after many incremental syncs (which skip dedup).
   */
  checkDedupDrift(maxAgeDays = 7): void {
    const last = getSyncState('lastDedupBuild');
    if (!last) return;
    const ageDays = (Date.now() - new Date(last).getTime()) / (1000 * 60 * 60 * 24);
    if (ageDays > maxAgeDays) {
      // Clear any prior pending rebuild so repeated checks don't queue multiple.
      if (this.driftTimer) clearTimeout(this.driftTimer);
      log(`checkDedupDrift: last dedup was ${ageDays.toFixed(1)} days ago — scheduling background rebuild`);
      this.driftTimer = setTimeout(() => {
        this.driftTimer = null;
        void this.runDedup();
      }, 30000);
    }
  }

  cancel(): void {
    log('cancel requested');
    this.cancelled = true;
  }

  private emitProgress(phase: string, current: number, total: number, detail: string, extra?: Partial<SyncProgress>): void {
    const percent = total > 0 ? Math.round((current / total) * 100) : 0;
    this.currentProgress = { phase, current, total, detail, percent, ...extra };
    this.currentPhase = phase;
    this.emit('progress', this.currentProgress);
  }

  private async delay(ms: number): Promise<void> {
    return new Promise((r) => setTimeout(r, ms));
  }

  private async fetchWithRetry(fn: () => Promise<unknown>, maxRetries = 5, label = 'request'): Promise<unknown> {
    let retryDelay = 1000;
    let lastErr: unknown;
    for (let attempt = 0; attempt < maxRetries; attempt++) {
      try {
        return await fn();
      } catch (err: unknown) {
        lastErr = err;
        const e = err as { response?: { status?: number }; code?: string; message?: string };
        const status = e?.response?.status;
        const code = e?.code;
        const retryableStatus = status === 429 || status === 500 || status === 502 || status === 503 || status === 504;
        const retryableCode =
          code === 'ECONNABORTED' ||
          code === 'ETIMEDOUT' ||
          code === 'ECONNRESET' ||
          code === 'ENOTFOUND' ||
          code === 'ECONNREFUSED' ||
          code === 'EAI_AGAIN';
        const retryable = retryableStatus || retryableCode;

        if (retryable && attempt < maxRetries - 1) {
          log(`fetchWithRetry: ${label} attempt ${attempt + 1}/${maxRetries} failed (status=${status ?? '-'} code=${code ?? '-'} msg=${e?.message ?? ''}); retrying in ${retryDelay}ms`);
          await this.delay(retryDelay);
          retryDelay = Math.min(retryDelay * 2, 30000);
          continue;
        }
        log(`fetchWithRetry: ${label} FAILED after ${attempt + 1} attempt(s) status=${status ?? '-'} code=${code ?? '-'} msg=${e?.message ?? ''}`);
        throw err;
      }
    }
    throw lastErr instanceof Error ? lastErr : new Error('Max retries exceeded');
  }

  private getCheckpoint(key = 'syncCheckpoint'): Checkpoint | null {
    const raw = getSyncState(key);
    if (!raw) return null;
    try {
      return JSON.parse(raw);
    } catch {
      return null;
    }
  }

  private saveCheckpoint(cp: Checkpoint, key = 'syncCheckpoint'): void {
    setSyncState(key, JSON.stringify(cp));
  }

  private clearCheckpoint(key = 'syncCheckpoint'): void {
    deleteSyncState(key);
  }

  // ── Full Sync ───────────────────────────────────────────

  async startFullSync(): Promise<void> {
    if (this.running) {
      log('startFullSync: already running, ignoring');
      return;
    }
    this.running = true;
    this.cancelled = false;
    this.currentPhase = 'starting';
    setSyncState('syncStatus', 'in-progress');

    // Fresh-start detection: no active checkpoints → truly starting from scratch.
    // Clear episode markers so every series is re-fetched. On resume, keep markers.
    const hasPhaseCheckpoint = !!this.getCheckpoint('syncCheckpoint');
    const hasServerCheckpoint = !!this.getCheckpoint('syncCheckpoint_servers');
    const isFreshStart = !hasPhaseCheckpoint && !hasServerCheckpoint;
    if (isFreshStart) {
      const cleared = clearEpisodeSyncMarkers();
      log(`startFullSync: fresh start — cleared ${cleared} episodes_synced_* markers`);
    } else {
      log(`startFullSync: resuming (phaseCheckpoint=${hasPhaseCheckpoint} serverCheckpoint=${hasServerCheckpoint}) — keeping episode markers`);
    }

    try {
      if (serverManager.isCombinedMode()) {
        log('startFullSync: combined mode');
        await this.fullSyncAllServers();
      } else {
        log('startFullSync: single-server mode');
        await this.fullSyncCurrentServer();
      }
    } catch (err) {
      log('startFullSync: unhandled error', err);
      this.emit('error', err instanceof Error ? err : new Error(String(err)));
    } finally {
      this.running = false;
      this.currentPhase = null;
      this.currentProgress = null;
      log('startFullSync: finished');
    }
  }

  private async fullSyncAllServers(): Promise<void> {
    const servers = serverManager.getServers();
    let totalItemsFetched = 0;
    const failedServers: { serverId: string; serverName: string }[] = [];

    // Resume from server-level checkpoint (separate key to avoid collision with episode checkpoints)
    const serverCheckpoint = this.getCheckpoint('syncCheckpoint_servers');
    const startServerIdx = serverCheckpoint?.phase === 10 ? (serverCheckpoint.seriesIndex || 0) : 0;
    log(`fullSyncAllServers: ${servers.length} server(s), startIdx=${startServerIdx}`);

    for (let sIdx = startServerIdx; sIdx < servers.length; sIdx++) {
      if (this.cancelled) {
        log('fullSyncAllServers: cancelled');
        break;
      }
      const server = servers[sIdx];
      let serverSucceeded = false;

      this.emitProgress('servers', sIdx, servers.length,
        `Connecting to ${server.name}...`);

      // Save active-server context before switching (restored in finally)
      embyClient.pushContext();
      try {
        // Point EmbyClient at this server for sync
        embyClient.setServer(server.url);
        embyClient.setAuth(server.accessToken, server.userId);

        // Validate connection
        await embyClient.getCurrentUser();

        log(`fullSyncAllServers: syncing server ${sIdx + 1}/${servers.length} (${server.name})`);
        totalItemsFetched += await this.fullSyncSingleServer(server.id, server.name, sIdx, servers.length);
        serverSucceeded = !this.cancelled;
      } catch (err) {
        const e = err as { message?: string };
        const isValidation = e?.message?.includes('reach');
        log(`fullSyncAllServers: server ${server.name} FAILED — ${e?.message ?? 'unknown'}`);
        this.emitProgress('servers', sIdx + 1, servers.length,
          `Skipped ${server.name} (${isValidation ? 'unreachable' : 'sync failed'})`);
        this.emit('server-error', { serverId: server.id, serverName: server.name, message: `Couldn't sync ${server.name}` });
        failedServers.push({ serverId: server.id, serverName: server.name });
      } finally {
        // Always restore original server context
        embyClient.popContext();
      }

      // Advance server checkpoint only on genuine success (or deliberate user cancel).
      // Never advance past a failed server — next run should retry it.
      if (serverSucceeded) {
        this.saveCheckpoint({ phase: 10, libraryIndex: 0, startIndex: 0, seriesIndex: sIdx + 1 }, 'syncCheckpoint_servers');
        log(`fullSyncAllServers: advanced server checkpoint past ${server.name}`);
      }
    }

    if (this.cancelled) return;

    if (failedServers.length > 0) {
      log(`fullSyncAllServers: ${failedServers.length} server(s) failed — marking syncStatus=partial, NOT updating lastFullSync`);
      setSyncState('lastItemCount', String(totalItemsFetched));
      setSyncState('syncStatus', 'partial');
      // Keep checkpoints so next run resumes the failed server(s)
      this.emit('partial', { failedServers, totalItemsFetched });
      return;
    }

    log(`fullSyncAllServers: all ${servers.length} server(s) succeeded — marking complete (${totalItemsFetched} items)`);
    setSyncState('lastFullSync', new Date().toISOString());
    setSyncState('lastItemCount', String(totalItemsFetched));
    setSyncState('syncStatus', 'complete');
    this.clearCheckpoint();
    this.clearCheckpoint('syncCheckpoint_servers');
    this.emit('complete');

    // Rebuild dedup in background — don't block sync completion on dedup state.
    void this.runDedup();
  }

  private async fullSyncSingleServer(
    serverId: string,
    serverName: string,
    serverIdx: number,
    totalServers: number,
  ): Promise<number> {
    // Fetch library views for this server
    const viewsResult = await this.fetchWithRetry(() => embyClient.getViews(), 5, `getViews(${serverName})`) as { Items: LibraryView[] };
    const libraries = viewsResult.Items.filter((v) =>
      v.CollectionType === 'movies' || v.CollectionType === 'tvshows' || !v.CollectionType
    );
    log(`fullSyncSingleServer[${serverName}]: ${libraries.length} libraries`);

    const libData = JSON.stringify(libraries.map((l) => ({ Id: l.Id, Name: l.Name })));
    setSyncState(`libraries_${serverId}`, libData);

    // Resume point for this server (per-server checkpoint key)
    const cpKey = `syncCheckpoint_${serverId}`;
    const checkpoint = this.getCheckpoint(cpKey);
    const startLibIdx = checkpoint?.phase === 2 ? checkpoint.libraryIndex : 0;
    const startItemIdx = checkpoint?.phase === 2 ? checkpoint.startIndex : 0;
    log(`fullSyncSingleServer[${serverName}]: resume libIdx=${startLibIdx} itemIdx=${startItemIdx}`);

    let totalItemsFetched = 0;

    // Fetch items per library
    for (let libIdx = startLibIdx; libIdx < libraries.length; libIdx++) {
      if (this.cancelled) return totalItemsFetched;
      const lib = libraries[libIdx];
      const initialStartIndex = libIdx === startLibIdx ? startItemIdx : 0;

      const countResult = await this.fetchWithRetry(() =>
        embyClient.getItems(lib.Id, {
          Recursive: true,
          IncludeItemTypes: 'Movie,Series',
          Limit: 0,
        }),
        5, `countItems[${serverName}/${lib.Name}]`
      ) as { TotalRecordCount: number };

      const totalInLib = countResult.TotalRecordCount;
      let startIndex = initialStartIndex;
      log(`fullSyncSingleServer[${serverName}/${lib.Name}]: totalInLib=${totalInLib} from=${startIndex}`);

      while (startIndex < totalInLib) {
        if (this.cancelled) return totalItemsFetched;

        const batch = await this.fetchWithRetry(() =>
          embyClient.getItems(lib.Id, {
            Recursive: true,
            IncludeItemTypes: 'Movie,Series',
            StartIndex: startIndex,
            Limit: BATCH_SIZE,
            Fields: FIELDS,
            SortBy: 'SortName',
            SortOrder: 'Ascending',
          }),
          5, `getItems[${serverName}/${lib.Name}@${startIndex}]`
        ) as { Items: Record<string, unknown>[] };

        if (batch.Items.length === 0) {
          log(`fullSyncSingleServer[${serverName}/${lib.Name}]: empty page at ${startIndex}, ending library`);
          break;
        }

        upsertItems(batch.Items, serverId, lib.Id, lib.Name);
        startIndex += batch.Items.length;
        totalItemsFetched += batch.Items.length;

        this.emitProgress('libraries', startIndex, totalInLib,
          `[${serverName}] ${lib.Name}: ${startIndex}/${totalInLib}`, {
            librariesDone: serverIdx,
            librariesTotal: totalServers,
          });

        // Per-batch checkpoint so interrupted combined-mode syncs can resume this server
        this.saveCheckpoint({ phase: 2, libraryIndex: libIdx, startIndex }, cpKey);

        await this.delay(FULL_SYNC_DELAY);
      }
    }

    if (this.cancelled) return totalItemsFetched;

    // Fetch episodes
    await this.syncEpisodes(serverId, serverName, libraries, cpKey);

    // Fetch fresh resume state (library /Items responses can have stale UserData on
    // large libraries; /Items/Resume returns authoritative watch positions).
    if (!this.cancelled) {
      try {
        const resumeResult = await this.fetchWithRetry(
          () => embyClient.getResumeItems(), 3, `getResumeItems[${serverName}]`
        ) as { Items: Record<string, unknown>[] };
        if (resumeResult.Items.length > 0) {
          upsertItemsPreservingLibrary(resumeResult.Items, serverId, libraries[0]?.Id || 'unknown', libraries[0]?.Name);
          log(`fullSyncSingleServer[${serverName}]: refreshed ${resumeResult.Items.length} resume items`);
        }
      } catch (err) {
        log(`fullSyncSingleServer[${serverName}]: resume fetch failed`, err);
      }
    }

    // Clear per-server checkpoint on success
    this.clearCheckpoint(cpKey);
    log(`fullSyncSingleServer[${serverName}]: done, ${totalItemsFetched} items`);

    return totalItemsFetched;
  }

  private async fullSyncCurrentServer(): Promise<void> {
    // Phase 1: Fetch library views
    this.emitProgress('views', 0, 1, 'Fetching libraries...');
    const viewsResult = await this.fetchWithRetry(() => embyClient.getViews(), 5, 'getViews') as { Items: LibraryView[] };
    const libraries = viewsResult.Items.filter((v) =>
      v.CollectionType === 'movies' || v.CollectionType === 'tvshows' || !v.CollectionType
    );
    log(`fullSyncCurrentServer: ${libraries.length} libraries`);

    if (this.cancelled) return;

    const serverId = serverManager.getActiveServerId();
    const libData = JSON.stringify(libraries.map((l) => ({ Id: l.Id, Name: l.Name })));
    setSyncState('libraries', libData);
    setSyncState(`libraries_${serverId}`, libData);

    // Check for resume checkpoint
    const checkpoint = this.getCheckpoint();
    const startLibIdx = checkpoint?.phase === 2 ? checkpoint.libraryIndex : 0;
    const startItemIdx = checkpoint?.phase === 2 ? checkpoint.startIndex : 0;
    log(`fullSyncCurrentServer: resume libIdx=${startLibIdx} itemIdx=${startItemIdx}`);

    // Phase 2: Fetch items per library
    let totalItemsFetched = 0;
    for (let libIdx = startLibIdx; libIdx < libraries.length; libIdx++) {
      if (this.cancelled) return;
      const lib = libraries[libIdx];
      const initialStartIndex = libIdx === startLibIdx ? startItemIdx : 0;

      // Get total count for this library
      const countResult = await this.fetchWithRetry(() =>
        embyClient.getItems(lib.Id, {
          Recursive: true,
          IncludeItemTypes: 'Movie,Series',
          Limit: 0,
        }),
        5, `countItems[${lib.Name}]`
      ) as { TotalRecordCount: number };

      const totalInLib = countResult.TotalRecordCount;
      let startIndex = initialStartIndex;
      log(`fullSyncCurrentServer[${lib.Name}]: totalInLib=${totalInLib} from=${startIndex}`);

      while (startIndex < totalInLib) {
        if (this.cancelled) return;

        const batch = await this.fetchWithRetry(() =>
          embyClient.getItems(lib.Id, {
            Recursive: true,
            IncludeItemTypes: 'Movie,Series',
            StartIndex: startIndex,
            Limit: BATCH_SIZE,
            Fields: FIELDS,
            SortBy: 'SortName',
            SortOrder: 'Ascending',
          }),
          5, `getItems[${lib.Name}@${startIndex}]`
        ) as { Items: Record<string, unknown>[] };

        if (batch.Items.length === 0) {
          log(`fullSyncCurrentServer[${lib.Name}]: empty page at ${startIndex}, ending library`);
          break;
        }

        upsertItems(batch.Items, serverId, lib.Id, lib.Name);
        startIndex += batch.Items.length;
        totalItemsFetched += batch.Items.length;

        this.emitProgress('libraries', startIndex, totalInLib,
          `Syncing ${lib.Name}: ${startIndex}/${totalInLib}`, {
            librariesDone: libIdx,
            librariesTotal: libraries.length,
          });

        this.saveCheckpoint({ phase: 2, libraryIndex: libIdx, startIndex });
        await this.delay(FULL_SYNC_DELAY);
      }
    }

    if (this.cancelled) return;

    // Phase 3: Fetch episodes for series
    await this.syncEpisodes(serverId, null, libraries, 'syncCheckpoint');

    if (this.cancelled) return;

    // Phase 3.5: Refresh resume items (library /Items can ship stale UserData on
    // large libraries; /Items/Resume is authoritative for watch positions).
    try {
      const resumeResult = await this.fetchWithRetry(
        () => embyClient.getResumeItems(), 3, 'getResumeItems'
      ) as { Items: Record<string, unknown>[] };
      if (resumeResult.Items.length > 0) {
        upsertItemsPreservingLibrary(resumeResult.Items, serverId, libraries[0]?.Id || 'unknown', libraries[0]?.Name);
        log(`fullSyncCurrentServer: refreshed ${resumeResult.Items.length} resume items`);
      }
    } catch (err) {
      log('fullSyncCurrentServer: resume fetch failed', err);
    }

    if (this.cancelled) return;

    // Phase 4: Precache homepage images
    await this.precacheHomepageImages(libraries);

    if (this.cancelled) return;

    // Mark sync complete BEFORE dedup so a dedup failure cannot roll back sync state
    log(`fullSyncCurrentServer: complete, ${totalItemsFetched} items`);
    setSyncState('lastFullSync', new Date().toISOString());
    setSyncState('lastItemCount', String(totalItemsFetched));
    setSyncState('syncStatus', 'complete');
    this.clearCheckpoint();
    this.emit('complete');

    // Phase 5: Rebuild dedup in background — don't block sync completion on dedup.
    void this.runDedup();
  }

  private async syncEpisodes(
    serverId: string,
    serverName: string | null,
    libraries: LibraryView[],
    checkpointKey: string,
  ): Promise<void> {
    const tag = serverName ? `[${serverName}]` : '';
    const checkpoint = this.getCheckpoint(checkpointKey);
    const resumeLibIdx = checkpoint?.phase === 3 ? (checkpoint.libraryIndex ?? 0) : 0;
    const resumeSeriesIdx = checkpoint?.phase === 3 ? (checkpoint.seriesIndex ?? 0) : 0;
    log(`syncEpisodes${tag}: resume libIdx=${resumeLibIdx} seriesIdx=${resumeSeriesIdx} (key=${checkpointKey})`);

    for (let libIdx = resumeLibIdx; libIdx < libraries.length; libIdx++) {
      if (this.cancelled) return;
      const lib = libraries[libIdx];

      // Only skip libraries that are explicitly movies. Keep 'tvshows' AND untyped/mixed libraries.
      if (lib.CollectionType === 'movies') {
        log(`syncEpisodes${tag}: skipping movie library ${lib.Name}`);
        continue;
      }

      // Fetch all series IDs from this library
      let seriesStartIdx = 0;

      const countRes = await this.fetchWithRetry(() =>
        embyClient.getItems(lib.Id, {
          Recursive: true,
          IncludeItemTypes: 'Series',
          Limit: 0,
        }),
        5, `countSeries${tag}[${lib.Name}]`
      ) as { TotalRecordCount: number };
      const totalSeries = countRes.TotalRecordCount;
      log(`syncEpisodes${tag}[${lib.Name}]: ${totalSeries} series`);

      if (totalSeries === 0) continue;

      // Fetch series in batches
      const allSeriesIds: { id: string; name: string }[] = [];
      while (seriesStartIdx < totalSeries) {
        if (this.cancelled) return;
        const batch = await this.fetchWithRetry(() =>
          embyClient.getItems(lib.Id, {
            Recursive: true,
            IncludeItemTypes: 'Series',
            StartIndex: seriesStartIdx,
            Limit: 100,
            Fields: 'ProviderIds',
          }),
          5, `getSeries${tag}[${lib.Name}@${seriesStartIdx}]`
        ) as { Items: { Id: string; Name: string }[] };

        for (const s of batch.Items) {
          allSeriesIds.push({ id: s.Id, name: s.Name });
        }
        seriesStartIdx += batch.Items.length;
        if (batch.Items.length === 0) break;
      }

      // Resume seriesIndex only on the library we interrupted; all later libraries start at 0.
      const startSeriesIdx = libIdx === resumeLibIdx ? resumeSeriesIdx : 0;
      log(`syncEpisodes${tag}[${lib.Name}]: fetched ${allSeriesIds.length} series, starting at ${startSeriesIdx}`);

      // Fetch episodes for each series
      for (let sIdx = startSeriesIdx; sIdx < allSeriesIds.length; sIdx++) {
        if (this.cancelled) return;
        const series = allSeriesIds[sIdx];

        // Check if we already synced episodes for this series
        const episodeSyncKey = `episodes_synced_${series.id}`;
        if (getSyncState(episodeSyncKey)) {
          this.saveCheckpoint({ phase: 3, libraryIndex: libIdx, startIndex: 0, seriesIndex: sIdx + 1 }, checkpointKey);
          continue;
        }

        try {
          // Fetch all episodes (+ seasons) across all pages
          let epStart = 0;
          let fetchedThisSeries = 0;
          while (true) {
            if (this.cancelled) return;
            const episodesResult = await this.fetchWithRetry(() =>
              embyClient.getItems(series.id, {
                Recursive: true,
                IncludeItemTypes: 'Episode,Season',
                StartIndex: epStart,
                Limit: EPISODE_PAGE_SIZE,
                Fields: FIELDS,
              }),
              5, `getEpisodes${tag}[${series.name}@${epStart}]`
            ) as { Items: Record<string, unknown>[] };

            const page = episodesResult.Items;
            if (page.length === 0) break;

            upsertItems(page, serverId, lib.Id, lib.Name);
            fetchedThisSeries += page.length;
            epStart += page.length;

            // Short page means we're done
            if (page.length < EPISODE_PAGE_SIZE) break;
          }

          setSyncState(episodeSyncKey, '1');
          if (fetchedThisSeries > 0) {
            log(`syncEpisodes${tag}[${lib.Name}]: ${series.name} → ${fetchedThisSeries} episodes/seasons`);
          }
        } catch (err) {
          const e = err as { message?: string; code?: string; response?: { status?: number } };
          log(`syncEpisodes${tag}[${lib.Name}]: ${series.name} FAILED — status=${e?.response?.status ?? '-'} code=${e?.code ?? '-'} msg=${e?.message ?? ''}`);
          // Do NOT mark episodes_synced — next run retries this series
        }

        this.emitProgress('episodes', sIdx + 1, allSeriesIds.length,
          `${tag ? `${tag} ` : ''}Episodes: ${series.name} (${sIdx + 1}/${allSeriesIds.length})`);

        this.saveCheckpoint({ phase: 3, libraryIndex: libIdx, startIndex: 0, seriesIndex: sIdx + 1 }, checkpointKey);
        await this.delay(FULL_SYNC_DELAY);
      }

      // Reset seriesIndex when crossing into the next library
      this.saveCheckpoint({ phase: 3, libraryIndex: libIdx + 1, startIndex: 0, seriesIndex: 0 }, checkpointKey);
    }
    log(`syncEpisodes${tag}: done`);
  }

  private async precacheHomepageImages(libraries: LibraryView[]): Promise<void> {
    this.emitProgress('images', 0, 1, 'Caching homepage images...');

    // Image URLs embed the api_key for the HTTP GET; image-cache.ts strips it
    // before using the URL as the SQLite cache key, so the token never lands
    // on disk even though we include it here for fetch.
    const imageUrls: string[] = [];
    const serverUrl = embyClient.baseUrl;
    const token = embyClient.token;
    if (!serverUrl || !token) return;

    // Gather backdrop URLs for hero banner candidates
    for (const lib of libraries.slice(0, 3)) {
      try {
        const latest = await this.fetchWithRetry(() =>
          embyClient.getLatestItems(lib.Id, 10), 3, `getLatestItems[${lib.Name}]`
        ) as { Id: string; BackdropImageTags?: string[]; ImageTags?: Record<string, string> }[];

        for (const item of latest) {
          if (item.BackdropImageTags?.length) {
            imageUrls.push(
              `${serverUrl}/emby/Items/${item.Id}/Images/Backdrop?maxWidth=1920&quality=90&tag=${item.BackdropImageTags[0]}&api_key=${token}`
            );
          }
          if (item.ImageTags?.Primary) {
            imageUrls.push(
              `${serverUrl}/emby/Items/${item.Id}/Images/Primary?maxWidth=300&quality=90&tag=${item.ImageTags.Primary}&api_key=${token}`
            );
          }
        }
      } catch {
        // Skip on error
      }
    }

    if (imageUrls.length > 0) {
      this.emitProgress('images', 0, imageUrls.length, `Caching ${imageUrls.length} images...`);
      let cached = 0;
      for (let i = 0; i < imageUrls.length; i++) {
        if (this.cancelled) return;
        // precacheImages handles one at a time internally; we track progress here
        await precacheImages([imageUrls[i]]);
        cached++;
        this.emitProgress('images', cached, imageUrls.length, `Cached ${cached}/${imageUrls.length} images`);
      }
    }
  }

  // ── Incremental Sync ──────────────────────────────────

  async startIncrementalSync(): Promise<void> {
    if (this.running) return;
    this.running = true;
    this.cancelled = false;
    this.currentPhase = 'incremental';

    try {
      const syncStatus = getSyncState('syncStatus') || 'never';
      // 'partial' and 'in-progress' both mean a full sync is not finished — redo it (it will resume).
      if (syncStatus !== 'complete') {
        log(`startIncrementalSync: syncStatus=${syncStatus} — redirecting to full sync`);
        this.running = false;
        return this.startFullSync();
      }
      const lastSync = getSyncState('lastFullSync');
      if (!lastSync) {
        // Defensive: syncStatus is 'complete' but lastFullSync is missing — full sync
        log('startIncrementalSync: no lastFullSync despite complete status — redirecting to full sync');
        this.running = false;
        return this.startFullSync();
      }

      if (serverManager.isCombinedMode()) {
        await this.incrementalSyncAllServers(lastSync);
      } else {
        await this.incrementalSyncCurrentServer(lastSync);
      }
    } catch (err) {
      this.emit('error', err instanceof Error ? err : new Error(String(err)));
    } finally {
      this.running = false;
      this.currentPhase = null;
      this.currentProgress = null;
    }
  }

  private async incrementalSyncAllServers(lastSync: string): Promise<void> {
    const servers = serverManager.getServers();
    const failedServers: string[] = [];

    for (const server of servers) {
      if (this.cancelled) return;

      embyClient.pushContext();
      try {
        embyClient.setServer(server.url);
        embyClient.setAuth(server.accessToken, server.userId);

        await embyClient.getCurrentUser();

        const librariesRaw = getSyncState(`libraries_${server.id}`);
        if (!librariesRaw) continue;

        const libraries: { Id: string; Name: string }[] = JSON.parse(librariesRaw);
        await this.incrementalSyncLibraries(server.id, server.name, libraries, lastSync);

        // Refresh watch status for this server (resume/continue-watching items)
        if (this.cancelled) continue;
        try {
          const resumeResult = await this.fetchWithRetry(() => embyClient.getResumeItems(), 3, `getResumeItems[${server.name}]`) as { Items: Record<string, unknown>[] };
          if (resumeResult.Items.length > 0) {
            upsertItemsPreservingLibrary(resumeResult.Items, server.id, libraries[0]?.Id || 'unknown', libraries[0]?.Name);
          }
        } catch {
          // Non-critical — continue with other servers
        }
      } catch {
        failedServers.push(server.name);
        this.emit('server-error', { serverId: server.id, serverName: server.name, message: `Couldn't reach ${server.name}` });
      } finally {
        embyClient.popContext();
      }
    }

    if (this.cancelled) return;

    // Only advance the incremental watermark when every server was reachable.
    // Advancing it past a failed server would permanently skip everything
    // that changed on that server during the outage (MinDateLastSaved filters
    // against this timestamp) until the user runs a manual full sync.
    if (failedServers.length > 0) {
      log(`incrementalSyncAllServers: ${failedServers.join(', ')} failed — keeping lastFullSync=${lastSync} so the next run re-covers the gap`);
    } else {
      setSyncState('lastFullSync', new Date().toISOString());
    }
    this.emit('complete');

    // Skip dedup on incremental sync — duplicates are rare at this scale. User
    // can hit "Rebuild Dedup" manually; checkDedupDrift triggers a background
    // rebuild on next app launch if the last dedup is more than 7 days old.
    log('incremental sync complete — skipping dedup rebuild (use Rebuild Dedup to refresh groups)');
  }

  private async incrementalSyncCurrentServer(lastSync: string): Promise<void> {
    const serverId = serverManager.getActiveServerId();
    const librariesRaw = getSyncState('libraries');
    if (!librariesRaw) {
      this.running = false;
      return this.startFullSync();
    }

    const libraries: { Id: string; Name: string }[] = JSON.parse(librariesRaw);
    await this.incrementalSyncLibraries(serverId, null, libraries, lastSync);

    if (this.cancelled) return;

    // Refresh user data for recently watched items
    this.emitProgress('incremental', libraries.length, libraries.length, 'Refreshing watch status...');
    try {
      const resumeResult = await this.fetchWithRetry(() => embyClient.getResumeItems(), 3, 'getResumeItems') as { Items: Record<string, unknown>[] };
      if (resumeResult.Items.length > 0) {
        upsertItemsPreservingLibrary(resumeResult.Items, serverId, libraries[0]?.Id || 'unknown', libraries[0]?.Name);
      }
    } catch {
      // Non-critical
    }

    setSyncState('lastFullSync', new Date().toISOString());
    this.emit('complete');

    // Skip dedup on incremental sync — duplicates are rare at this scale. User
    // can hit "Rebuild Dedup" manually; checkDedupDrift triggers a background
    // rebuild on next app launch if the last dedup is more than 7 days old.
    log('incremental sync complete — skipping dedup rebuild (use Rebuild Dedup to refresh groups)');
  }

  private async incrementalSyncLibraries(
    serverId: string,
    serverName: string | null,
    libraries: { Id: string; Name: string }[],
    lastSync: string,
  ): Promise<void> {
    const prefix = serverName ? `[${serverName}] ` : '';
    this.emitProgress('incremental', 0, libraries.length, `${prefix}Checking for updates...`);

    let totalUpdated = 0;
    for (let i = 0; i < libraries.length; i++) {
      if (this.cancelled) return;
      const lib = libraries[i];

      let startIndex = 0;
      let hasMore = true;

      while (hasMore) {
        if (this.cancelled) return;

        const result = await this.fetchWithRetry(() =>
          embyClient.getItems(lib.Id, {
            Recursive: true,
            IncludeItemTypes: 'Movie,Series,Episode,Season',
            StartIndex: startIndex,
            Limit: BATCH_SIZE,
            Fields: FIELDS,
            SortBy: 'DateModified',
            SortOrder: 'Descending',
            MinDateLastSaved: lastSync,
          }),
          5, `incremental${prefix}[${lib.Name}@${startIndex}]`
        ) as { Items: Record<string, unknown>[]; TotalRecordCount: number };

        if (result.Items.length === 0) {
          hasMore = false;
          break;
        }

        upsertItems(result.Items, serverId, lib.Id, lib.Name);
        totalUpdated += result.Items.length;
        startIndex += result.Items.length;

        if (startIndex >= result.TotalRecordCount) {
          hasMore = false;
        }

        this.emitProgress('incremental', i + 1, libraries.length,
          `${prefix}${lib.Name}: ${totalUpdated} items updated`);

        await this.delay(INCREMENTAL_DELAY);
      }
    }
  }
}

export const syncEngine = new SyncEngine();
