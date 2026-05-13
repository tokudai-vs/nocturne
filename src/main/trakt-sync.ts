import { EventEmitter } from 'events';
import { traktClient } from './trakt-client';
import { traktScrobbler } from './trakt-scrobbler';
import { embyClient } from './emby-client';
import { serverManager } from './server-manager';
import { getSettingValue, setSetting } from './settings';
import {
  bulkFindEpisodesBySeriesIds,
  bulkFindItemsByExternalIds,
  bulkGetItemsByEmbyIds,
  bulkMarkItemsPlayed,
  bulkUpsertTraktWatched,
  clearTraktWatched,
  countTraktWatched,
  countTraktWatchlist,
  deleteTraktWatchlistEntry,
  getCachedTraktRating,
  getItem as dbGetItem,
  getTraktWatchlistEntries,
  isInTraktWatchlist,
  replaceTraktWatchlist,
  setCachedTraktRating,
  type ItemRow,
  type TraktWatchedRow,
  type TraktWatchlistRow,
} from './database';
import type {
  TraktHistoryItem,
  TraktHistoryPreview,
  TraktMatchedEpisode,
  TraktMatchedMovie,
  TraktRatingResult,
  TraktUserStats,
  TraktWatchedMovie,
  TraktWatchedShow,
  TraktWatchlistMovie,
  TraktWatchlistShow,
} from './trakt-types';

const RATING_TTL_MS = 24 * 60 * 60 * 1000;
const HISTORY_SYNC_INTERVAL_MS = 6 * 60 * 60 * 1000;
const WATCHLIST_SYNC_INTERVAL_MS = 60 * 60 * 1000;

const APPLY_CONCURRENCY = 10;

class TraktSync extends EventEmitter {
  private historyTimer: ReturnType<typeof setInterval> | null = null;
  private watchlistTimer: ReturnType<typeof setInterval> | null = null;
  /** Set by `cancelApply()`; checked between Emby pushes during apply. */
  private applyCancelled = false;
  /** Single-flight guard for refreshWatchlist. Multiple concurrent triggers
   *  (boot + auth-success + 1h timer + add/remove + manual) attach to the
   *  in-flight promise instead of issuing duplicate Trakt round-trips. */
  private inFlightWatchlistRefresh: Promise<{ count: number }> | null = null;

  // ── Lifecycle ──────────────────────────────────────

  /** Start periodic sync timers. Safe to call repeatedly. */
  startTimers(): void {
    this.stopTimers();
    if (!traktClient.isConnected()) return;

    if (this.syncWatchedEnabled()) {
      this.historyTimer = setInterval(() => {
        void this.runBackgroundHistorySync();
      }, HISTORY_SYNC_INTERVAL_MS);
    }
    if (this.showWatchlistEnabled()) {
      this.watchlistTimer = setInterval(() => {
        void this.refreshWatchlist();
      }, WATCHLIST_SYNC_INTERVAL_MS);
    }
  }

  stopTimers(): void {
    if (this.historyTimer) { clearInterval(this.historyTimer); this.historyTimer = null; }
    if (this.watchlistTimer) { clearInterval(this.watchlistTimer); this.watchlistTimer = null; }
  }

  // ── Settings shortcuts ─────────────────────────────

  private syncWatchedEnabled(): boolean {
    const v = getSettingValue('traktSyncWatchedState');
    return v === undefined ? true : Boolean(v);
  }
  private showWatchlistEnabled(): boolean {
    const v = getSettingValue('traktShowWatchlistInSidebar');
    return v === undefined ? true : Boolean(v);
  }

  // ── Phase 2: Initial preview ───────────────────────

  /**
   * Fetch full Trakt watched-state and build a preview that the renderer
   * shows after initial connect. Also persists into trakt_watched_history
   * (for Phase 4 drift detection) regardless of whether the user accepts
   * the apply step.
   */
  async fetchInitialPreview(): Promise<TraktHistoryPreview> {
    // Hard 30s ceiling for the entire fetch + match pipeline. Bulk queries
    // bring this well under a second in practice, but the timeout exists so
    // a network hiccup (or a future regression) can never leave the renderer
    // stuck in the "Looking up your Trakt history…" state forever.
    let timer: ReturnType<typeof setTimeout> | undefined;
    const work = this.doFetchInitialPreview();
    try {
      return await Promise.race([
        work,
        new Promise<TraktHistoryPreview>((_, reject) => {
          timer = setTimeout(
            () => reject(new Error('Timed out fetching Trakt history (30s). Try again or check your connection.')),
            30_000,
          );
        }),
      ]);
    } finally {
      if (timer) clearTimeout(timer);
    }
  }

  private async doFetchInitialPreview(): Promise<TraktHistoryPreview> {
    const t0 = Date.now();
    console.log('[trakt-sync] fetchInitialPreview: requesting /sync/watched/movies + /sync/watched/shows');
    const [movies, shows] = await Promise.all([
      traktClient.getWatchedMovies().then((m) => {
        console.log(`[trakt-sync]   getWatchedMovies → ${m.length} rows (+${Date.now() - t0}ms)`);
        return m;
      }),
      traktClient.getWatchedShows().then((s) => {
        const epCount = s.reduce((n, sh) => n + sh.seasons.reduce((m, se) => m + se.episodes.length, 0), 0);
        console.log(`[trakt-sync]   getWatchedShows → ${s.length} shows / ${epCount} episodes (+${Date.now() - t0}ms)`);
        return s;
      }),
    ]);

    const tPersist = Date.now();
    const historyRows = this.buildHistoryRows(movies, shows);
    if (historyRows.length > 0) {
      // Replace-style: full pull means current state is authoritative.
      clearTraktWatched();
      bulkUpsertTraktWatched(historyRows);
    }
    console.log(`[trakt-sync]   persisted ${historyRows.length} history rows (+${Date.now() - tPersist}ms)`);

    console.log(`[trakt-sync] fetchInitialPreview: matching against local cache (+${Date.now() - t0}ms)`);
    const tMatch = Date.now();
    const matchedMovies = this.matchMovies(movies);
    console.log(`[trakt-sync]   matched ${matchedMovies.length}/${movies.length} movies (+${Date.now() - tMatch}ms)`);
    const tEp = Date.now();
    const matchedEpisodes = this.matchEpisodes(shows);
    console.log(`[trakt-sync]   matched ${matchedEpisodes.length} episodes (+${Date.now() - tEp}ms)`);

    const totalEpisodesOnTrakt = shows.reduce((sum, s) =>
      sum + s.seasons.reduce((ss, season) => ss + season.episodes.length, 0), 0);

    console.log(`[trakt-sync] fetchInitialPreview: complete (+${Date.now() - t0}ms total)`);
    return {
      movies: {
        totalOnTrakt: movies.length,
        matchedInLibrary: matchedMovies.length,
        items: matchedMovies,
      },
      episodes: {
        totalOnTrakt: totalEpisodesOnTrakt,
        matchedInLibrary: matchedEpisodes.length,
        items: matchedEpisodes,
      },
    };
  }

  /**
   * Apply Trakt watched state to a chosen subset of local items.
   *
   * Pipeline (replaces an old serial loop that took ~5–10 minutes for 3k+
   * items):
   *   1. One bulk SELECT to fetch all rows by emby_id (chunked at 500 to
   *      stay under SQLite's 999-param limit).
   *   2. Filter to rows that aren't already played.
   *   3. ONE transactional UPDATE marks every row played in local cache.
   *      The UI now reflects the change immediately; users don't wait on
   *      the network for what they came to see.
   *   4. Push to each item's home Emby server in parallel with a
   *      concurrency cap. Per-item failures are tolerated; counts are
   *      collected and surfaced. Cancellation is checked between pushes.
   *
   * Critical: this path NEVER calls the `item:mark-played` IPC handler, so
   * it never re-pushes the same state to Trakt — that would create a sync
   * loop on the next 6h pull. The Emby calls go through `embyClient`
   * directly, bypassing the Trakt-push side-effect.
   */
  async applyWatchedState(
    embyIds: string[],
  ): Promise<{ applied: number; failed: number; cancelled: boolean }> {
    this.applyCancelled = false;
    const t0 = Date.now();
    const unique = Array.from(new Set(embyIds));

    // Phase 1: bulk-fetch matching rows.
    const rows = bulkGetItemsByEmbyIds(unique);
    const byId = new Map<string, ItemRow>(rows.map((r) => [r.emby_id, r]));
    const toApply = unique
      .map((id) => byId.get(id))
      .filter((r): r is ItemRow => r !== undefined && r.played === 0);

    const total = toApply.length;
    console.log(`[trakt-sync] applyWatchedState: ${unique.length} requested, ${total} need apply (${Date.now() - t0}ms)`);

    this.emit('apply-progress', { current: 0, total });

    if (total === 0) {
      setSetting('traktLastSyncAt', new Date().toISOString());
      this.emit('history-applied', { applied: 0, failed: 0 });
      return { applied: 0, failed: 0, cancelled: false };
    }

    // Phase 2: bulk-mark played in local SQLite (single transaction).
    const tLocal = Date.now();
    bulkMarkItemsPlayed(toApply.map((r) => r.emby_id));
    console.log(`[trakt-sync]   local mark-played for ${total} items (+${Date.now() - tLocal}ms)`);

    // Phase 3: push to each item's home server in parallel. 10 concurrent
    // is a safe sweet spot — Emby tolerates it without flooding, and axios
    // pools per host so we get keep-alive reuse.
    let applied = 0;
    let failed = 0;
    let lastEmit = 0;
    const step = Math.max(1, Math.floor(total / 100));

    const emitProgress = (force = false) => {
      const done = applied + failed;
      if (force || done - lastEmit >= step) {
        lastEmit = done;
        this.emit('apply-progress', { current: done, total });
      }
    };

    let nextIdx = 0;
    const activeServer = serverManager.getActiveServer();

    const worker = async (): Promise<void> => {
      while (true) {
        if (this.applyCancelled) return;
        const idx = nextIdx++;
        if (idx >= toApply.length) return;
        const item = toApply[idx];
        const server = serverManager.getServer(item.server_id);
        if (!server) {
          // Item's server is gone — local cache is already updated, count it
          // as applied so we don't surface a misleading failure.
          applied++;
          emitProgress();
          continue;
        }
        try {
          if (activeServer && server.id === activeServer.id) {
            await embyClient.markPlayed(item.emby_id);
          } else {
            await embyClient.markPlayedOnServer(
              server.url, server.accessToken, server.userId, item.emby_id,
            );
          }
          applied++;
        } catch (err) {
          failed++;
          console.warn(`[trakt-sync]   Emby mark-played failed for ${item.emby_id}:`, err);
        }
        emitProgress();
      }
    };

    const tPush = Date.now();
    await Promise.all(
      Array.from({ length: Math.min(APPLY_CONCURRENCY, toApply.length) }, () => worker()),
    );
    console.log(
      `[trakt-sync]   pushed to Emby — applied ${applied}, failed ${failed}`
        + (this.applyCancelled ? ', cancelled' : '')
        + ` (+${Date.now() - tPush}ms, ${Date.now() - t0}ms total)`,
    );

    emitProgress(true);

    setSetting('traktLastSyncAt', new Date().toISOString());
    this.emit('history-applied', { applied, failed });
    return { applied, failed, cancelled: this.applyCancelled };
  }

  /** Cancel an in-flight applyWatchedState. Workers check the flag between
   *  pushes; the local SQLite update has already completed by the time
   *  any cancel can land, so the user-visible state is consistent. */
  cancelApply(): void {
    this.applyCancelled = true;
  }

  // ── Phase 2: Push (Nocturne → Trakt) ───────────────

  /** Called when the user marks an item played in the Nocturne UI. */
  async pushHistoryAdd(embyId: string): Promise<void> {
    const item = dbGetItem(embyId);
    console.log(
      `[trakt-history-push] item:mark-played triggered, item={id=${embyId}, name="${item?.name ?? '?'}", `
        + `type=${item?.type ?? '?'}, tmdb_id=${item?.tmdb_id ?? '-'}, imdb_id=${item?.imdb_id ?? '-'}}`,
    );
    if (!this.syncWatchedEnabled()) {
      console.log(`[trakt-history-push] skip ${embyId}: traktSyncWatchedState=false`);
      return;
    }
    if (!traktClient.isConnected()) {
      console.log(`[trakt-history-push] skip ${embyId}: not connected to Trakt`);
      return;
    }
    const body = this.buildHistoryBody(embyId);
    if (!body) {
      // Most common cause: Emby metadata didn't populate tmdb_id / imdb_id.
      // Without either, Trakt has nothing to match against.
      console.warn(
        `[trakt-history-push] SKIP ${embyId} (${item?.type ?? '?'} "${item?.name ?? '?'}"): `
          + `no tmdb_id/imdb_id on local item — refresh Emby metadata for this title`,
      );
      return;
    }
    console.log(
      `[trakt-history-push] enqueued history-add for tmdb_id=${item?.tmdb_id ?? '-'} `
        + `(${item?.type} "${item?.name}")`,
    );
    console.log(
      `[trakt-history-push] queue drain: sending POST /sync/history with body=${JSON.stringify(body)}`,
    );
    try {
      const response = await traktClient.addHistory(body);
      const r = response as { added?: { movies?: number; episodes?: number }; not_found?: { movies?: unknown[]; episodes?: unknown[] } };
      const addedMovies = r?.added?.movies ?? 0;
      const addedEpisodes = r?.added?.episodes ?? 0;
      const notFoundMovies = r?.not_found?.movies?.length ?? 0;
      const notFoundEpisodes = r?.not_found?.episodes?.length ?? 0;
      console.log(
        `[trakt-history-push] response: 201 — added=${addedMovies + addedEpisodes}, `
          + `not_found_movies=${notFoundMovies}, not_found_episodes=${notFoundEpisodes}`,
      );
      if (addedMovies + addedEpisodes === 0) {
        // Trakt returned 200/201 but didn't add the item — almost always
        // because Trakt's catalog couldn't find a matching entry. Surface
        // this so the user knows their click had no remote effect.
        console.warn(
          `[trakt-history-push] Trakt did NOT add the item. body=${JSON.stringify(body)} response=${JSON.stringify(response).slice(0, 400)}`,
        );
      }
    } catch (err) {
      console.warn(`[trakt-history-push] history-add FAILED, queueing for retry:`, err);
      traktScrobbler.enqueueHistoryAction('history-add', body, embyId, errorMessage(err));
    }
  }

  /** Called when the user marks an item unwatched in the Nocturne UI. */
  async pushHistoryRemove(embyId: string): Promise<void> {
    if (!this.syncWatchedEnabled() || !traktClient.isConnected()) return;
    const item = dbGetItem(embyId);
    const body = this.buildHistoryBody(embyId);
    if (!body) {
      console.warn(
        `[trakt-history-push] SKIP remove ${embyId} (${item?.type ?? '?'} "${item?.name ?? '?'}"): no tmdb_id/imdb_id`,
      );
      return;
    }
    console.log(
      `[trakt-history-push] item:mark-unplayed → POST /sync/history/remove `
        + `(${item?.type} "${item?.name}", tmdb=${item?.tmdb_id ?? '-'})`,
    );
    try {
      const response = await traktClient.removeHistory(body);
      const r = response as { deleted?: { movies?: number; episodes?: number } };
      const deleted = (r?.deleted?.movies ?? 0) + (r?.deleted?.episodes ?? 0);
      console.log(`[trakt-history-push] response: deleted=${deleted}`);
    } catch (err) {
      console.warn(`[trakt-history-push] history-remove FAILED, queueing for retry:`, err);
      traktScrobbler.enqueueHistoryAction('history-remove', body, embyId, errorMessage(err));
    }
  }

  // ── Phase 2: Background sync ───────────────────────

  /**
   * Re-pull the full Trakt watched mirror and apply any newly-watched items
   * to local cache + Emby. Cheap: two paginated GETs, then JS-side diff.
   */
  async runBackgroundHistorySync(): Promise<{ newlyWatched: number; failed: number }> {
    if (!traktClient.isConnected()) return { newlyWatched: 0, failed: 0 };
    if (!this.syncWatchedEnabled()) return { newlyWatched: 0, failed: 0 };

    try {
      const [movies, shows] = await Promise.all([
        traktClient.getWatchedMovies(),
        traktClient.getWatchedShows(),
      ]);

      const historyRows = this.buildHistoryRows(movies, shows);
      clearTraktWatched();
      bulkUpsertTraktWatched(historyRows);

      // Find rows matched in our cache that AREN'T already played locally.
      const matchedMovies = this.matchMovies(movies);
      const matchedEpisodes = this.matchEpisodes(shows);

      const toMark: string[] = [];
      for (const m of matchedMovies) if (!m.alreadyPlayed) toMark.push(...m.embyIds);
      for (const e of matchedEpisodes) if (!e.alreadyPlayed) toMark.push(...e.embyIds);

      const result = toMark.length === 0
        ? { applied: 0, failed: 0 }
        : await this.applyWatchedState(toMark);

      setSetting('traktLastSyncAt', new Date().toISOString());
      this.emit('background-sync-complete', { newlyWatched: result.applied, failed: result.failed });
      return { newlyWatched: result.applied, failed: result.failed };
    } catch (err) {
      console.warn('[trakt-sync] background history sync failed:', err);
      this.emit('background-sync-failed', { message: errorMessage(err) });
      return { newlyWatched: 0, failed: 0 };
    }
  }

  // ── Phase 3: Watchlist ─────────────────────────────

  async refreshWatchlist(): Promise<{ count: number }> {
    if (!traktClient.isConnected()) {
      console.log('[trakt-watchlist] refresh skipped — not connected');
      return { count: 0 };
    }
    // Single-flight: piggy-back on an existing in-flight refresh instead of
    // racing duplicate `GET /watchlist/{movies,shows}` round-trips. Boot,
    // auth-success, the 1h timer, add/remove, and manual `trakt:sync-now`
    // all funnel through here, so without this an authed-on-launch user
    // can fire 2-3 simultaneous refreshes during cold start.
    if (this.inFlightWatchlistRefresh) {
      console.log('[trakt-watchlist] refresh already in flight — attaching to existing promise');
      return this.inFlightWatchlistRefresh;
    }
    const t0 = Date.now();
    console.log('[trakt-watchlist] refresh: GET /users/me/watchlist/{movies,shows}?extended=full');
    const work = (async () => {
      try {
        const [movies, shows] = await Promise.all([
          traktClient.getWatchlistMovies(),
          traktClient.getWatchlistShows(),
        ]);
        const rows = this.buildWatchlistRows(movies, shows);
        replaceTraktWatchlist(rows);
        setSetting('traktLastWatchlistSyncAt', new Date().toISOString());
        console.log(
          `[trakt-watchlist] refresh complete: ${rows.length} entries `
            + `(${movies.length} movies, ${shows.length} shows) in ${Date.now() - t0}ms`,
        );
        this.emit('watchlist-updated', { count: rows.length });
        return { count: rows.length };
      } catch (err) {
        console.warn(`[trakt-watchlist] refresh failed after ${Date.now() - t0}ms:`, err);
        return { count: countTraktWatchlist() };
      } finally {
        this.inFlightWatchlistRefresh = null;
      }
    })();
    this.inFlightWatchlistRefresh = work;
    return work;
  }

  /**
   * Add a local Nocturne item to Trakt watchlist (Phase 4 detail-page action).
   * Resolves the item's TMDB id and movie/show type, then POSTs.
   */
  async addItemToWatchlist(embyId: string): Promise<{ ok: boolean; error?: string }> {
    if (!traktClient.isConnected()) return { ok: false, error: 'Not connected' };
    const item = dbGetItem(embyId);
    if (!item) return { ok: false, error: 'Item not found' };

    // Episodes/Seasons → roll up to series for watchlist.
    const target = item.type === 'Episode' || item.type === 'Season'
      ? (item.series_id ? dbGetItem(item.series_id) : null)
      : item;
    if (!target) return { ok: false, error: 'Series not found in cache' };
    if (target.type !== 'Movie' && target.type !== 'Series') {
      return { ok: false, error: 'Only movies and shows can be watchlisted' };
    }
    const tmdb = target.tmdb_id ? Number(target.tmdb_id) : undefined;
    const imdb = target.imdb_id || undefined;
    if (!tmdb && !imdb) return { ok: false, error: 'No Trakt-compatible IDs' };

    const isShow = target.type === 'Series';
    const body = isShow
      ? { shows: [{ ids: { tmdb, imdb } }] }
      : { movies: [{ ids: { tmdb, imdb } }] };

    try {
      await traktClient.addToWatchlist(body);
      // Optimistically refresh — don't wait the full hour
      await this.refreshWatchlist();
      return { ok: true };
    } catch (err) {
      return { ok: false, error: errorMessage(err) };
    }
  }

  /**
   * Remove from Trakt watchlist. Accepts either a local emby_id or an
   * external-only entry identified by trakt_type + tmdb_id (the unmatched
   * entries shown in the watchlist sidebar).
   */
  async removeItemFromWatchlist(args:
    | { embyId: string }
    | { traktType: 'movie' | 'show'; tmdbId: string; key?: string }
  ): Promise<{ ok: boolean; error?: string }> {
    if (!traktClient.isConnected()) return { ok: false, error: 'Not connected' };

    let body: Record<string, unknown> | null = null;
    let watchlistKey: string | null = null;

    if ('embyId' in args) {
      const item = dbGetItem(args.embyId);
      if (!item) return { ok: false, error: 'Item not found' };
      const target = item.type === 'Episode' || item.type === 'Season'
        ? (item.series_id ? dbGetItem(item.series_id) : null)
        : item;
      if (!target) return { ok: false, error: 'Series not found' };
      const tmdb = target.tmdb_id ? Number(target.tmdb_id) : undefined;
      const imdb = target.imdb_id || undefined;
      if (!tmdb && !imdb) return { ok: false, error: 'No Trakt-compatible IDs' };
      const isShow = target.type === 'Series';
      body = isShow
        ? { shows: [{ ids: { tmdb, imdb } }] }
        : { movies: [{ ids: { tmdb, imdb } }] };
      if (target.tmdb_id) {
        watchlistKey = `${isShow ? 'show' : 'movie'}:${target.tmdb_id}`;
      }
    } else {
      const tmdb = Number(args.tmdbId);
      body = args.traktType === 'show'
        ? { shows: [{ ids: { tmdb } }] }
        : { movies: [{ ids: { tmdb } }] };
      watchlistKey = args.key ?? `${args.traktType}:${args.tmdbId}`;
    }

    try {
      await traktClient.removeFromWatchlist(body!);
      // Optimistically prune the local row immediately so the renderer can
      // re-query without waiting on the network round-trip.
      if (watchlistKey) deleteTraktWatchlistEntry(watchlistKey);
      // Then mirror Trakt's actual state — this also emits 'watchlist-updated'
      // so the sidebar count and any open watchlist library refresh in <2s
      // instead of waiting for the next 1h timer.
      await this.refreshWatchlist();
      return { ok: true };
    } catch (err) {
      return { ok: false, error: errorMessage(err) };
    }
  }

  /** True iff the local item is currently in the user's Trakt watchlist. */
  isInWatchlist(embyId: string): boolean {
    const item = dbGetItem(embyId);
    if (!item) return false;
    const target = item.type === 'Episode' || item.type === 'Season'
      ? (item.series_id ? dbGetItem(item.series_id) : null)
      : item;
    if (!target?.tmdb_id) return false;
    const traktType = target.type === 'Series' ? 'show' : 'movie';
    return isInTraktWatchlist(traktType, target.tmdb_id);
  }

  getWatchlistEntries(): TraktWatchlistRow[] {
    return getTraktWatchlistEntries();
  }

  // ── Phase 4: Ratings ───────────────────────────────

  async getRating(tmdbId: string, type: 'movie' | 'show'): Promise<TraktRatingResult | null> {
    const cached = getCachedTraktRating(tmdbId, type, RATING_TTL_MS);
    if (cached) return { rating: cached.rating, votes: cached.votes };

    if (!traktClient.isConnected()) return null;
    try {
      const details = await traktClient.getItemDetailsByTmdb(tmdbId, type);
      if (!details) {
        // Cache the negative so we don't hammer Trakt for missing items.
        setCachedTraktRating(tmdbId, type, null, null);
        return null;
      }
      setCachedTraktRating(tmdbId, type, details.rating ?? null, details.votes ?? null);
      return { rating: details.rating ?? null, votes: details.votes ?? null };
    } catch (err) {
      console.warn('[trakt-sync] rating fetch failed:', err);
      return null;
    }
  }

  // ── Analytics: lifetime stats (cached) + history backfill ──

  /** GET /users/me/stats with a 1h disk cache. The renderer hits this on
   *  every Analytics page mount; without the cache, tab switches would
   *  hammer Trakt. Returns null when offline / not connected. */
  async getCachedUserStats(forceRefresh = false): Promise<TraktUserStats | null> {
    if (!traktClient.isConnected()) return null;
    if (!forceRefresh) {
      const cachedJson = getSettingValue('traktUserStatsCache');
      const cachedAt = getSettingValue('traktUserStatsCachedAt');
      if (cachedJson && cachedAt) {
        const age = Date.now() - new Date(cachedAt).getTime();
        if (age < 60 * 60 * 1000) {
          try { return JSON.parse(cachedJson) as TraktUserStats; } catch { /* fall through */ }
        }
      }
    }
    try {
      const stats = await traktClient.getUserStats();
      setSetting('traktUserStatsCache', JSON.stringify(stats));
      setSetting('traktUserStatsCachedAt', new Date().toISOString());
      return stats;
    } catch (err) {
      console.warn('[trakt-sync] getUserStats failed:', err);
      return null;
    }
  }

  /**
   * Page through GET /sync/history and UPSERT every event into
   * `trakt_watched_history`. Idempotent — the synthesized `key` column
   * collapses re-runs. Emits 'backfill-progress' events between pages so
   * the renderer can show "Syncing Trakt history… 1230/4500".
   *
   * Honors `traktHistoryBackfillCap`:
   *   - 'two-years': start_at = (now - 730 days), single bounded run
   *   - 'full':      no start_at, walks back to user's earliest history
   */
  async backfillHistory(): Promise<{ inserted: number; total: number }> {
    if (!traktClient.isConnected()) return { inserted: 0, total: 0 };

    const cap = (getSettingValue('traktHistoryBackfillCap') as 'two-years' | 'full' | undefined) ?? 'two-years';
    const startAt = cap === 'two-years'
      ? new Date(Date.now() - 730 * 24 * 60 * 60 * 1000).toISOString()
      : undefined;

    const PAGE_LIMIT = 1000;
    let page = 1;
    let total = 0;
    let inserted = 0;
    this.emit('backfill-progress', { current: 0, total: 0 });

    while (true) {
      let res;
      try {
        res = await traktClient.getHistory({ startAt, page, limit: PAGE_LIMIT });
      } catch (err) {
        console.warn(`[trakt-sync] backfill page ${page} failed:`, err);
        this.emit('backfill-failed', { message: errorMessage(err) });
        return { inserted, total };
      }

      if (page === 1) total = res.itemCount;

      const rows = this.historyItemsToWatchedRows(res.items);
      if (rows.length > 0) {
        bulkUpsertTraktWatched(rows);
        inserted += rows.length;
      }

      this.emit('backfill-progress', { current: inserted, total });

      if (page >= res.pageCount || res.items.length === 0) break;
      page++;
    }

    setSetting('traktHistoryBackfilled', true);
    this.emit('backfill-complete', { inserted, total });
    return { inserted, total };
  }

  private historyItemsToWatchedRows(items: TraktHistoryItem[]): TraktWatchedRow[] {
    const out: TraktWatchedRow[] = [];
    for (const ev of items) {
      if (ev.type === 'movie' && ev.movie) {
        const tmdb = ev.movie.ids.tmdb ? String(ev.movie.ids.tmdb) : null;
        const imdb = ev.movie.ids.imdb || null;
        if (!tmdb && !imdb) continue;
        out.push({
          key: tmdb ? `movie:${tmdb}` : `movie:imdb:${imdb}`,
          trakt_type: 'movie',
          tmdb_id: tmdb,
          imdb_id: imdb,
          show_tmdb_id: null,
          season_number: null,
          episode_number: null,
          watched_at: ev.watched_at,
        });
      } else if (ev.type === 'episode' && ev.episode && ev.show) {
        const showTmdb = ev.show.ids.tmdb ? String(ev.show.ids.tmdb) : null;
        if (!showTmdb) continue;
        out.push({
          key: `episode:${showTmdb}:${ev.episode.season}:${ev.episode.number}`,
          trakt_type: 'episode',
          tmdb_id: null,
          imdb_id: null,
          show_tmdb_id: showTmdb,
          season_number: ev.episode.season,
          episode_number: ev.episode.number,
          watched_at: ev.watched_at,
        });
      }
    }
    return out;
  }

  // ── Stats ──────────────────────────────────────────

  getStats(): { watched: { movies: number; episodes: number }; watchlist: number; lastHistorySync: string | null; lastWatchlistSync: string | null } {
    return {
      watched: countTraktWatched(),
      watchlist: countTraktWatchlist(),
      lastHistorySync: (getSettingValue('traktLastSyncAt') as string | null) || null,
      lastWatchlistSync: (getSettingValue('traktLastWatchlistSyncAt') as string | null) || null,
    };
  }

  // ── Internals ──────────────────────────────────────

  private buildHistoryRows(movies: TraktWatchedMovie[], shows: TraktWatchedShow[]): TraktWatchedRow[] {
    const out: TraktWatchedRow[] = [];
    for (const m of movies) {
      const tmdb = m.movie.ids.tmdb ? String(m.movie.ids.tmdb) : null;
      const imdb = m.movie.ids.imdb || null;
      if (!tmdb && !imdb) continue;
      const key = tmdb ? `movie:${tmdb}` : `movie:imdb:${imdb}`;
      out.push({
        key,
        trakt_type: 'movie',
        tmdb_id: tmdb,
        imdb_id: imdb,
        show_tmdb_id: null,
        season_number: null,
        episode_number: null,
        watched_at: m.last_watched_at,
      });
    }
    for (const s of shows) {
      const tmdb = s.show.ids.tmdb ? String(s.show.ids.tmdb) : null;
      if (!tmdb) continue; // episodes need show's tmdb to match locally
      for (const season of s.seasons) {
        for (const ep of season.episodes) {
          out.push({
            key: `episode:${tmdb}:${season.number}:${ep.number}`,
            trakt_type: 'episode',
            tmdb_id: null,
            imdb_id: null,
            show_tmdb_id: tmdb,
            season_number: season.number,
            episode_number: ep.number,
            watched_at: ep.last_watched_at,
          });
        }
      }
    }
    return out;
  }

  /**
   * Movie matching — was per-row SQL (one IN-clauseless lookup per Trakt
   * movie); now: one bulk IN query for tmdb, one for imdb, then in-memory
   * map lookups. ~331 row scans collapse into ~1-2 round trips.
   */
  private matchMovies(movies: TraktWatchedMovie[]): TraktMatchedMovie[] {
    if (movies.length === 0) return [];

    const tmdbIds = new Set<string>();
    const imdbIds = new Set<string>();
    for (const m of movies) {
      if (m.movie.ids.tmdb) tmdbIds.add(String(m.movie.ids.tmdb));
      if (m.movie.ids.imdb) imdbIds.add(m.movie.ids.imdb);
    }

    const localMovies = bulkFindItemsByExternalIds(
      Array.from(tmdbIds),
      Array.from(imdbIds),
      'Movie',
    );

    // Multiple local copies per id are common — same movie on two servers.
    const byTmdb = new Map<string, ItemRow[]>();
    const byImdb = new Map<string, ItemRow[]>();
    for (const r of localMovies) {
      if (r.tmdb_id) {
        const list = byTmdb.get(r.tmdb_id) ?? [];
        list.push(r);
        byTmdb.set(r.tmdb_id, list);
      }
      if (r.imdb_id) {
        const list = byImdb.get(r.imdb_id) ?? [];
        list.push(r);
        byImdb.set(r.imdb_id, list);
      }
    }

    const matched: TraktMatchedMovie[] = [];
    for (const m of movies) {
      const tmdb = m.movie.ids.tmdb ? String(m.movie.ids.tmdb) : null;
      const imdb = m.movie.ids.imdb || null;
      let rows: ItemRow[] = [];
      if (tmdb) rows = byTmdb.get(tmdb) ?? [];
      if (rows.length === 0 && imdb) rows = byImdb.get(imdb) ?? [];
      if (rows.length === 0) continue;
      matched.push({
        tmdbId: tmdb,
        imdbId: imdb,
        title: m.movie.title,
        year: m.movie.year,
        watchedAt: m.last_watched_at,
        embyIds: rows.map((r) => r.emby_id),
        alreadyPlayed: rows.every((r) => r.played === 1),
      });
    }
    return matched;
  }

  /**
   * Episode matching — was per-episode JOIN against items twice (3,446 calls
   * × multi-thousand-row table = main-thread freeze for tens of seconds);
   * now: one IN query for matching Series rows, one IN query pulling every
   * Episode of those series, then a nested Map<showTmdb, season, ep> for
   * O(1) lookups during the iteration over Trakt's nested shape.
   */
  private matchEpisodes(shows: TraktWatchedShow[]): TraktMatchedEpisode[] {
    if (shows.length === 0) return [];

    // Phase 1: collect show TMDB ids. Episodes are matched via show.tmdb only
    // (Trakt episodes don't carry standalone external IDs reliably).
    const showTmdbIds = new Set<string>();
    for (const s of shows) {
      if (s.show.ids.tmdb) showTmdbIds.add(String(s.show.ids.tmdb));
    }
    if (showTmdbIds.size === 0) return [];

    // Phase 2: bulk-fetch matching local Series rows.
    const localSeries = bulkFindItemsByExternalIds(
      Array.from(showTmdbIds),
      [],
      'Series',
    );
    if (localSeries.length === 0) return [];

    // emby_id → tmdb_id map so we can key each episode back to its Trakt show.
    const seriesEmbyToTmdb = new Map<string, string>();
    for (const r of localSeries) {
      if (r.tmdb_id) seriesEmbyToTmdb.set(r.emby_id, r.tmdb_id);
    }

    // Phase 3: bulk-fetch ALL episodes belonging to the matched series.
    const localEpisodes = bulkFindEpisodesBySeriesIds(Array.from(seriesEmbyToTmdb.keys()));

    // Build nested map: showTmdb → season → episode → ItemRow[]
    const epMap = new Map<string, Map<number, Map<number, ItemRow[]>>>();
    for (const e of localEpisodes) {
      if (!e.series_id || e.season_number == null || e.episode_number == null) continue;
      const showTmdb = seriesEmbyToTmdb.get(e.series_id);
      if (!showTmdb) continue;
      let bySeason = epMap.get(showTmdb);
      if (!bySeason) { bySeason = new Map(); epMap.set(showTmdb, bySeason); }
      let byEp = bySeason.get(e.season_number);
      if (!byEp) { byEp = new Map(); bySeason.set(e.season_number, byEp); }
      let list = byEp.get(e.episode_number);
      if (!list) { list = []; byEp.set(e.episode_number, list); }
      list.push(e);
    }

    const matched: TraktMatchedEpisode[] = [];
    for (const s of shows) {
      const showTmdb = s.show.ids.tmdb ? String(s.show.ids.tmdb) : null;
      if (!showTmdb) continue;
      const bySeason = epMap.get(showTmdb);
      if (!bySeason) continue;
      for (const season of s.seasons) {
        const byEp = bySeason.get(season.number);
        if (!byEp) continue;
        for (const ep of season.episodes) {
          const rows = byEp.get(ep.number);
          if (!rows || rows.length === 0) continue;
          matched.push({
            showTmdbId: showTmdb,
            showTitle: s.show.title,
            season: season.number,
            episode: ep.number,
            watchedAt: ep.last_watched_at,
            embyIds: rows.map((r) => r.emby_id),
            alreadyPlayed: rows.every((r) => r.played === 1),
          });
        }
      }
    }
    return matched;
  }

  private buildWatchlistRows(movies: TraktWatchlistMovie[], shows: TraktWatchlistShow[]): TraktWatchlistRow[] {
    const out: TraktWatchlistRow[] = [];
    for (const m of movies) {
      const tmdb = m.movie.ids.tmdb ? String(m.movie.ids.tmdb) : null;
      const imdb = m.movie.ids.imdb || null;
      const key = tmdb ? `movie:${tmdb}` : (imdb ? `movie:imdb:${imdb}` : null);
      if (!key) continue;
      out.push({
        key,
        trakt_type: 'movie',
        tmdb_id: tmdb,
        imdb_id: imdb,
        trakt_id: m.movie.ids.trakt ?? null,
        title: m.movie.title,
        year: m.movie.year,
        overview: m.movie.overview ?? null,
        added_at: m.listed_at,
      });
    }
    for (const s of shows) {
      const tmdb = s.show.ids.tmdb ? String(s.show.ids.tmdb) : null;
      const imdb = s.show.ids.imdb || null;
      const key = tmdb ? `show:${tmdb}` : (imdb ? `show:imdb:${imdb}` : null);
      if (!key) continue;
      out.push({
        key,
        trakt_type: 'show',
        tmdb_id: tmdb,
        imdb_id: imdb,
        trakt_id: s.show.ids.trakt ?? null,
        title: s.show.title,
        year: s.show.year,
        overview: s.show.overview ?? null,
        added_at: s.listed_at,
      });
    }
    return out;
  }

  /** Build a /sync/history request body for a single local item.
   *  Includes title/year alongside IDs so Trakt can fall back to fuzzy match
   *  when a tmdb_id isn't in their catalog, and an explicit `watched_at` so
   *  the entry lands in the user's history page at the time they clicked
   *  Mark Played (rather than Trakt's default "released" date for movies). */
  private buildHistoryBody(embyId: string): Record<string, unknown> | null {
    const item = dbGetItem(embyId);
    if (!item) return null;
    const watchedAt = new Date().toISOString();
    if (item.type === 'Movie') {
      const tmdb = item.tmdb_id ? Number(item.tmdb_id) : undefined;
      const imdb = item.imdb_id || undefined;
      if (!tmdb && !imdb) return null;
      return {
        movies: [{
          watched_at: watchedAt,
          title: item.name,
          year: item.production_year ?? undefined,
          ids: { tmdb, imdb },
        }],
      };
    }
    if (item.type === 'Episode') {
      if (!item.series_id || item.season_number == null || item.episode_number == null) return null;
      const series = dbGetItem(item.series_id);
      if (!series) return null;
      const tmdb = series.tmdb_id ? Number(series.tmdb_id) : undefined;
      const imdb = series.imdb_id || undefined;
      const tvdb = series.tvdb_id ? Number(series.tvdb_id) : undefined;
      if (!tmdb && !imdb && !tvdb) return null;
      return {
        shows: [{
          title: series.name,
          year: series.production_year ?? undefined,
          ids: { tmdb, imdb, tvdb },
          seasons: [{
            number: item.season_number,
            episodes: [{
              watched_at: watchedAt,
              number: item.episode_number,
            }],
          }],
        }],
      };
    }
    return null;
  }
}

function errorMessage(err: unknown): string {
  if (err instanceof Error) return err.message;
  if (typeof err === 'string') return err;
  return 'unknown';
}

export const traktSync = new TraktSync();
