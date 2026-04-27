import { EventEmitter } from 'events';
import { traktClient } from './trakt-client';
import { traktScrobbler } from './trakt-scrobbler';
import { embyClient } from './emby-client';
import { serverManager } from './server-manager';
import { getSettingValue, setSetting } from './settings';
import {
  bulkUpsertTraktWatched,
  clearTraktWatched,
  countTraktWatched,
  countTraktWatchlist,
  deleteTraktWatchlistEntry,
  findEpisodesByShowTmdb,
  findItemsByImdbId,
  findItemsByTmdbId,
  getCachedTraktRating,
  getItem as dbGetItem,
  getTraktWatchlistEntries,
  isInTraktWatchlist,
  replaceTraktWatchlist,
  setCachedTraktRating,
  updateItemUserData,
  type ItemRow,
  type TraktWatchedRow,
  type TraktWatchlistRow,
} from './database';
import type {
  TraktHistoryPreview,
  TraktMatchedEpisode,
  TraktMatchedMovie,
  TraktRatingResult,
  TraktWatchedMovie,
  TraktWatchedShow,
  TraktWatchlistMovie,
  TraktWatchlistShow,
} from './trakt-types';

const RATING_TTL_MS = 24 * 60 * 60 * 1000;
const HISTORY_SYNC_INTERVAL_MS = 6 * 60 * 60 * 1000;
const WATCHLIST_SYNC_INTERVAL_MS = 60 * 60 * 1000;

class TraktSync extends EventEmitter {
  private historyTimer: ReturnType<typeof setInterval> | null = null;
  private watchlistTimer: ReturnType<typeof setInterval> | null = null;

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
    const [movies, shows] = await Promise.all([
      traktClient.getWatchedMovies(),
      traktClient.getWatchedShows(),
    ]);

    // Persist watched mirror
    const historyRows = this.buildHistoryRows(movies, shows);
    if (historyRows.length > 0) {
      // Replace-style: full pull means current state is authoritative.
      clearTraktWatched();
      bulkUpsertTraktWatched(historyRows);
    }

    const matchedMovies = this.matchMovies(movies);
    const matchedEpisodes = this.matchEpisodes(shows);

    const totalEpisodesOnTrakt = shows.reduce((sum, s) =>
      sum + s.seasons.reduce((ss, season) => ss + season.episodes.length, 0), 0);

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
   * Each entry is { embyIds: string[] } — callers expand each Trakt-watched
   * row into all matched local copies (across servers) and we mark every
   * copy played without echoing the change back to Trakt.
   */
  async applyWatchedState(embyIds: string[]): Promise<{ applied: number; failed: number }> {
    let applied = 0;
    let failed = 0;
    // De-dupe to avoid wasted work when caller passes overlapping lists.
    const unique = Array.from(new Set(embyIds));

    for (const embyId of unique) {
      try {
        await this.markPlayedSilent(embyId);
        applied++;
      } catch (err) {
        console.warn(`[trakt-sync] applyWatchedState: ${embyId} failed:`, err);
        failed++;
      }
    }
    setSetting('traktLastSyncAt', new Date().toISOString());
    this.emit('history-applied', { applied, failed });
    return { applied, failed };
  }

  /**
   * Mark an item played without pushing the change back to Trakt
   * (used when the source of truth IS Trakt — initial pull, background sync).
   * Updates local cache + pushes to Emby on the item's home server.
   */
  private async markPlayedSilent(embyId: string): Promise<void> {
    const item = dbGetItem(embyId);
    if (!item) return;
    if (item.played) return; // already played locally
    updateItemUserData(embyId, { played: 1, playback_position_ticks: 0, played_percentage: 0 });
    const server = serverManager.getServer(item.server_id);
    if (!server) return;
    const activeServer = serverManager.getActiveServer();
    try {
      if (activeServer && server.id === activeServer.id) {
        await embyClient.markPlayed(embyId);
      } else {
        await embyClient.markPlayedOnServer(server.url, server.accessToken, server.userId, embyId);
      }
    } catch (err) {
      // Local cache stays optimistically marked; next sync will reconcile.
      console.warn(`[trakt-sync] Emby mark-played failed for ${embyId}:`, err);
    }
  }

  // ── Phase 2: Push (Nocturne → Trakt) ───────────────

  /** Called when the user marks an item played in the Nocturne UI. */
  async pushHistoryAdd(embyId: string): Promise<void> {
    if (!this.syncWatchedEnabled() || !traktClient.isConnected()) return;
    const body = this.buildHistoryBody(embyId);
    if (!body) return;
    try {
      await traktClient.addHistory(body);
    } catch (err) {
      console.warn(`[trakt-sync] history-add failed, queueing:`, err);
      traktScrobbler.enqueueHistoryAction('history-add', body, embyId, errorMessage(err));
    }
  }

  /** Called when the user marks an item unwatched in the Nocturne UI. */
  async pushHistoryRemove(embyId: string): Promise<void> {
    if (!this.syncWatchedEnabled() || !traktClient.isConnected()) return;
    const body = this.buildHistoryBody(embyId);
    if (!body) return;
    try {
      await traktClient.removeHistory(body);
    } catch (err) {
      console.warn(`[trakt-sync] history-remove failed, queueing:`, err);
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
    if (!traktClient.isConnected()) return { count: 0 };
    try {
      const [movies, shows] = await Promise.all([
        traktClient.getWatchlistMovies(),
        traktClient.getWatchlistShows(),
      ]);
      const rows = this.buildWatchlistRows(movies, shows);
      replaceTraktWatchlist(rows);
      setSetting('traktLastWatchlistSyncAt', new Date().toISOString());
      this.emit('watchlist-updated', { count: rows.length });
      return { count: rows.length };
    } catch (err) {
      console.warn('[trakt-sync] watchlist refresh failed:', err);
      return { count: countTraktWatchlist() };
    }
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
      if (watchlistKey) deleteTraktWatchlistEntry(watchlistKey);
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

  private matchMovies(movies: TraktWatchedMovie[]): TraktMatchedMovie[] {
    const matched: TraktMatchedMovie[] = [];
    for (const m of movies) {
      const tmdb = m.movie.ids.tmdb ? String(m.movie.ids.tmdb) : null;
      const imdb = m.movie.ids.imdb || null;
      let rows: ItemRow[] = [];
      if (tmdb) rows = findItemsByTmdbId(tmdb, 'Movie');
      if (rows.length === 0 && imdb) rows = findItemsByImdbId(imdb, 'Movie');
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

  private matchEpisodes(shows: TraktWatchedShow[]): TraktMatchedEpisode[] {
    const matched: TraktMatchedEpisode[] = [];
    for (const s of shows) {
      const showTmdb = s.show.ids.tmdb ? String(s.show.ids.tmdb) : null;
      if (!showTmdb) continue;
      for (const season of s.seasons) {
        for (const ep of season.episodes) {
          const rows = findEpisodesByShowTmdb(showTmdb, season.number, ep.number);
          if (rows.length === 0) continue;
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

  /** Build a /sync/history request body for a single local item. */
  private buildHistoryBody(embyId: string): Record<string, unknown> | null {
    const item = dbGetItem(embyId);
    if (!item) return null;
    if (item.type === 'Movie') {
      const tmdb = item.tmdb_id ? Number(item.tmdb_id) : undefined;
      const imdb = item.imdb_id || undefined;
      if (!tmdb && !imdb) return null;
      return { movies: [{ ids: { tmdb, imdb } }] };
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
          ids: { tmdb, imdb, tvdb },
          seasons: [{
            number: item.season_number,
            episodes: [{ number: item.episode_number }],
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
