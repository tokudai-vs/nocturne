import Database from 'better-sqlite3';
import { serverManager } from './server-manager';

// Type-safe handle to the singleton db lives in database.ts; the analytics
// helpers import only the function and run their own prepared statements.
// Importing the db object directly would cycle.
import { getDbForAnalytics } from './database';

export type AnalyticsSource = 'local' | 'trakt' | 'combined';

export interface AnalyticsRange {
  rangeStart: string; // ISO 8601
  rangeEnd: string;   // ISO 8601
}

export interface AnalyticsLifetimeBlock {
  movies: number;
  episodes: number;
  watchTimeMinutes: number;
  distinctShows: number;
}

export interface AnalyticsStats {
  source: AnalyticsSource;
  range: AnalyticsRange;
  totalWatched: { movies: number; episodes: number };
  totalWatchTimeSeconds: number;
  inProgressSeriesCount: number;
  avgPerWeekSeconds: number;
  activityByDay: Array<{ date: string; count: number; watchTimeSeconds: number }>;
  topSeries: Array<{ id: string; name: string; imageUrl: string | null; episodeCount: number }>;
  topMovies: Array<{ id: string; name: string; imageUrl: string | null; lastPlayed: string | null }>;
  genreBreakdown: Array<{ genre: string; watchTimeSeconds: number; pct: number }>;
  lifetime: AnalyticsLifetimeBlock | null;
  /** Set when source is 'trakt' or 'combined' and items can't be matched locally. */
  unmatchedTraktCount?: number;
}

const SECONDS_PER_TICK = 1 / 10_000_000;

// ── URL composition ─────────────────────────────────────

/**
 * Build an Emby Primary image URL for an item using its home server's URL
 * + access token. Used so combined-mode analytics still surface posters from
 * other servers' libraries (active server's token won't authorize those).
 */
function buildImageUrlForServer(
  serverId: string,
  itemId: string,
  primaryTag: string | null,
  maxWidth = 300,
): string | null {
  const server = serverManager.getServer(serverId);
  if (!server) return null;
  const params = new URLSearchParams();
  params.set('maxWidth', String(maxWidth));
  if (primaryTag) params.set('tag', primaryTag);
  params.set('quality', '90');
  params.set('api_key', server.accessToken);
  return `${server.url}/emby/Items/${encodeURIComponent(itemId)}/Images/Primary?${params.toString()}`;
}

function extractPrimaryTag(imageTagsJson: string | null): string | null {
  if (!imageTagsJson) return null;
  try {
    const parsed = JSON.parse(imageTagsJson) as Record<string, string>;
    return parsed.Primary || null;
  } catch {
    return null;
  }
}

// ── Local analytics ─────────────────────────────────────

interface RawSummaryRow { movies: number; episodes: number }
interface RawTimeRow { played_ticks: number | null; progress_ticks: number | null }
interface RawCountRow { c: number }
interface RawSeriesRow {
  series_id: string;
  name: string | null;
  server_id: string | null;
  emby_id: string | null;
  image_tags: string | null;
  episode_count: number;
}
interface RawMovieRow {
  emby_id: string;
  name: string;
  server_id: string;
  image_tags: string | null;
  last_played_date: string | null;
  play_count: number;
}
interface RawGenreRow { genres: string | null; runtime_ticks: number | null }

function localTotalWatched(db: Database.Database, range: AnalyticsRange): { movies: number; episodes: number } {
  const row = db
    .prepare(
      `SELECT
        SUM(CASE WHEN type = 'Movie' THEN 1 ELSE 0 END) AS movies,
        SUM(CASE WHEN type = 'Episode' THEN 1 ELSE 0 END) AS episodes
       FROM items
       WHERE played = 1
         AND last_played_date IS NOT NULL
         AND last_played_date BETWEEN ? AND ?`,
    )
    .get(range.rangeStart, range.rangeEnd) as RawSummaryRow | undefined;
  return { movies: row?.movies ?? 0, episodes: row?.episodes ?? 0 };
}

function localTotalWatchTimeSeconds(db: Database.Database, range: AnalyticsRange): number {
  // Played-in-range contribution: full runtime per played row.
  const playedRow = db
    .prepare(
      `SELECT SUM(COALESCE(runtime_ticks, 0)) AS played_ticks
       FROM items
       WHERE played = 1
         AND last_played_date IS NOT NULL
         AND last_played_date BETWEEN ? AND ?
         AND type IN ('Movie', 'Episode')`,
    )
    .get(range.rangeStart, range.rangeEnd) as RawTimeRow | undefined;

  // In-progress contribution: position ticks. No date filter — by definition
  // these are still active. Avoids double-counting items already in "played".
  const progressRow = db
    .prepare(
      `SELECT SUM(COALESCE(playback_position_ticks, 0)) AS progress_ticks
       FROM items
       WHERE played = 0
         AND playback_position_ticks > 0
         AND type IN ('Movie', 'Episode')`,
    )
    .get() as RawTimeRow | undefined;

  const totalTicks = (playedRow?.played_ticks ?? 0) + (progressRow?.progress_ticks ?? 0);
  return Math.round(totalTicks * SECONDS_PER_TICK);
}

function localInProgressSeriesCount(db: Database.Database): number {
  const row = db
    .prepare(
      `SELECT COUNT(DISTINCT series_id) AS c
       FROM items
       WHERE type = 'Episode'
         AND played = 0
         AND playback_position_ticks > 0
         AND series_id IS NOT NULL`,
    )
    .get() as RawCountRow | undefined;
  return row?.c ?? 0;
}

function localActivityByDay(db: Database.Database, range: AnalyticsRange): AnalyticsStats['activityByDay'] {
  // DATE() truncates an ISO timestamp to YYYY-MM-DD in SQLite. Sum runtime
  // ticks alongside the count so the bar chart can show real hours instead
  // of items-per-day as a proxy.
  const rows = db
    .prepare(
      `SELECT DATE(last_played_date) AS date,
              COUNT(*) AS count,
              SUM(COALESCE(runtime_ticks, 0)) AS ticks
       FROM items
       WHERE played = 1
         AND last_played_date IS NOT NULL
         AND last_played_date BETWEEN ? AND ?
       GROUP BY DATE(last_played_date)
       ORDER BY date ASC`,
    )
    .all(range.rangeStart, range.rangeEnd) as Array<{ date: string; count: number; ticks: number | null }>;
  return rows.map((r) => ({
    date: r.date,
    count: r.count,
    watchTimeSeconds: Math.round((r.ticks ?? 0) * SECONDS_PER_TICK),
  }));
}

function localTopSeries(
  db: Database.Database,
  range: AnalyticsRange,
  limit = 10,
): AnalyticsStats['topSeries'] {
  // Group plays by series, then resolve each series's emby_id + image tags.
  const rows = db
    .prepare(
      `SELECT
         e.series_id AS series_id,
         s.name AS name,
         s.server_id AS server_id,
         s.emby_id AS emby_id,
         s.image_tags AS image_tags,
         COUNT(*) AS episode_count
       FROM items e
       LEFT JOIN items s ON s.emby_id = e.series_id
       WHERE e.type = 'Episode'
         AND e.played = 1
         AND e.last_played_date IS NOT NULL
         AND e.last_played_date BETWEEN ? AND ?
         AND e.series_id IS NOT NULL
       GROUP BY e.series_id
       ORDER BY episode_count DESC, s.name ASC
       LIMIT ?`,
    )
    .all(range.rangeStart, range.rangeEnd, limit) as RawSeriesRow[];

  return rows.map((r) => ({
    id: r.series_id,
    name: r.name ?? '(unknown series)',
    imageUrl: r.emby_id && r.server_id
      ? buildImageUrlForServer(r.server_id, r.emby_id, extractPrimaryTag(r.image_tags))
      : null,
    episodeCount: r.episode_count,
  }));
}

function localTopMovies(
  db: Database.Database,
  range: AnalyticsRange,
  limit = 10,
): AnalyticsStats['topMovies'] {
  const rows = db
    .prepare(
      `SELECT emby_id, name, server_id, image_tags, last_played_date, play_count
       FROM items
       WHERE type = 'Movie'
         AND played = 1
         AND last_played_date IS NOT NULL
         AND last_played_date BETWEEN ? AND ?
       ORDER BY play_count DESC, last_played_date DESC
       LIMIT ?`,
    )
    .all(range.rangeStart, range.rangeEnd, limit) as RawMovieRow[];

  return rows.map((r) => ({
    id: r.emby_id,
    name: r.name,
    imageUrl: buildImageUrlForServer(r.server_id, r.emby_id, extractPrimaryTag(r.image_tags)),
    lastPlayed: r.last_played_date,
  }));
}

function localGenreBreakdown(db: Database.Database, range: AnalyticsRange): AnalyticsStats['genreBreakdown'] {
  const rows = db
    .prepare(
      `SELECT genres, COALESCE(runtime_ticks, 0) AS runtime_ticks
       FROM items
       WHERE played = 1
         AND last_played_date IS NOT NULL
         AND last_played_date BETWEEN ? AND ?
         AND type IN ('Movie', 'Episode')
         AND genres IS NOT NULL`,
    )
    .all(range.rangeStart, range.rangeEnd) as RawGenreRow[];

  // Episodes typically don't carry per-episode genres in Emby; only the Series
  // row has them. For each played Episode, fall back to its series's genres.
  // Cheap because the row count here is bounded by the range filter.
  const epRows = db
    .prepare(
      `SELECT s.genres AS genres, COALESCE(e.runtime_ticks, 0) AS runtime_ticks
       FROM items e
       LEFT JOIN items s ON s.emby_id = e.series_id
       WHERE e.type = 'Episode'
         AND e.played = 1
         AND e.last_played_date IS NOT NULL
         AND e.last_played_date BETWEEN ? AND ?
         AND e.genres IS NULL
         AND s.genres IS NOT NULL`,
    )
    .all(range.rangeStart, range.rangeEnd) as RawGenreRow[];

  return aggregateGenres([...rows, ...epRows]);
}

function aggregateGenres(rows: RawGenreRow[]): AnalyticsStats['genreBreakdown'] {
  const totals = new Map<string, number>();
  for (const r of rows) {
    if (!r.genres) continue;
    let list: string[];
    try {
      const parsed = JSON.parse(r.genres);
      if (!Array.isArray(parsed)) continue;
      list = parsed.filter((g): g is string => typeof g === 'string');
    } catch {
      continue;
    }
    if (list.length === 0) continue;
    // Apportion runtime across the item's genres equally so a 90-minute
    // Action/Comedy movie doesn't get counted twice (180 min total).
    // runtime_ticks is COALESCE'd to 0 in the SQL but TS still types it
    // nullable — guard so a stray null can't NaN-poison the totals map.
    const slice = (r.runtime_ticks ?? 0) * SECONDS_PER_TICK / list.length;
    for (const g of list) {
      totals.set(g, (totals.get(g) ?? 0) + slice);
    }
  }
  const sorted = Array.from(totals.entries())
    .map(([genre, sec]) => ({ genre, watchTimeSeconds: Math.round(sec) }))
    .sort((a, b) => b.watchTimeSeconds - a.watchTimeSeconds);

  // Top 8 + "Other" bucket per spec.
  const top = sorted.slice(0, 8);
  const tail = sorted.slice(8);
  if (tail.length > 0) {
    const otherSec = tail.reduce((sum, x) => sum + x.watchTimeSeconds, 0);
    if (otherSec > 0) top.push({ genre: 'Other', watchTimeSeconds: otherSec });
  }
  const totalSec = top.reduce((sum, x) => sum + x.watchTimeSeconds, 0);
  return top.map((x) => ({
    ...x,
    pct: totalSec > 0 ? Math.round((x.watchTimeSeconds / totalSec) * 1000) / 10 : 0,
  }));
}

// ── Public entry point ──────────────────────────────────

export function computeLocalAnalytics(range: AnalyticsRange): AnalyticsStats {
  const db = getDbForAnalytics();
  const totalWatched = localTotalWatched(db, range);
  const totalWatchTimeSeconds = localTotalWatchTimeSeconds(db, range);
  const inProgressSeriesCount = localInProgressSeriesCount(db);
  const activityByDay = localActivityByDay(db, range);
  const topSeries = localTopSeries(db, range);
  const topMovies = localTopMovies(db, range);
  const genreBreakdown = localGenreBreakdown(db, range);

  const rangeMs = new Date(range.rangeEnd).getTime() - new Date(range.rangeStart).getTime();
  const weeks = Math.max(1, rangeMs / (7 * 24 * 60 * 60 * 1000));
  const avgPerWeekSeconds = Math.round(totalWatchTimeSeconds / weeks);

  return {
    source: 'local',
    range,
    totalWatched,
    totalWatchTimeSeconds,
    inProgressSeriesCount,
    avgPerWeekSeconds,
    activityByDay,
    topSeries,
    topMovies,
    genreBreakdown,
    lifetime: null,
  };
}

// ── Trakt analytics ─────────────────────────────────────

interface EnrichedTraktEvent {
  trakt_type: 'movie' | 'episode';
  tmdb_id: string | null;
  show_tmdb_id: string | null;
  season_number: number | null;
  episode_number: number | null;
  watched_at: string;
  // Joined from items table where match exists; null when Trakt-only.
  matched_emby_id: string | null;
  matched_server_id: string | null;
  matched_name: string | null;
  matched_image_tags: string | null;
  matched_runtime_ticks: number | null;
  matched_genres: string | null;
}

/**
 * Pull every Trakt watched event in range, left-joining to local items so
 * each row carries the matched item's name / runtime / genres / image. The
 * join uses tmdb_id for movies, (series.tmdb_id, season, ep) for episodes.
 * Items not in the local library have matched_* columns NULL — those drop
 * out of runtime/genre aggregation but still count as plays.
 */
function fetchTraktEvents(db: Database.Database, range: AnalyticsRange): EnrichedTraktEvent[] {
  return db
    .prepare(
      `SELECT
         thw.trakt_type,
         thw.tmdb_id,
         thw.show_tmdb_id,
         thw.season_number,
         thw.episode_number,
         thw.watched_at,
         CASE
           WHEN thw.trakt_type = 'movie' THEN m.emby_id
           WHEN thw.trakt_type = 'episode' THEN e.emby_id
         END AS matched_emby_id,
         CASE
           WHEN thw.trakt_type = 'movie' THEN m.server_id
           WHEN thw.trakt_type = 'episode' THEN e.server_id
         END AS matched_server_id,
         CASE
           WHEN thw.trakt_type = 'movie' THEN m.name
           WHEN thw.trakt_type = 'episode' THEN e.name
         END AS matched_name,
         CASE
           WHEN thw.trakt_type = 'movie' THEN m.image_tags
           WHEN thw.trakt_type = 'episode' THEN e.image_tags
         END AS matched_image_tags,
         CASE
           WHEN thw.trakt_type = 'movie' THEN m.runtime_ticks
           WHEN thw.trakt_type = 'episode' THEN e.runtime_ticks
         END AS matched_runtime_ticks,
         CASE
           WHEN thw.trakt_type = 'movie' THEN m.genres
           WHEN thw.trakt_type = 'episode' THEN COALESCE(e.genres, series.genres)
         END AS matched_genres
       FROM trakt_watched_history thw
       LEFT JOIN items m
         ON thw.trakt_type = 'movie'
         AND m.type = 'Movie'
         AND m.tmdb_id IS NOT NULL
         AND m.tmdb_id = thw.tmdb_id
       LEFT JOIN items series
         ON thw.trakt_type = 'episode'
         AND series.type = 'Series'
         AND series.tmdb_id IS NOT NULL
         AND series.tmdb_id = thw.show_tmdb_id
       LEFT JOIN items e
         ON thw.trakt_type = 'episode'
         AND e.type = 'Episode'
         AND e.series_id = series.emby_id
         AND e.season_number = thw.season_number
         AND e.episode_number = thw.episode_number
       WHERE thw.watched_at BETWEEN ? AND ?
       ORDER BY thw.watched_at ASC`,
    )
    .all(range.rangeStart, range.rangeEnd) as EnrichedTraktEvent[];
}

function computeTraktAnalytics(range: AnalyticsRange, lifetime: AnalyticsLifetimeBlock | null): AnalyticsStats {
  const db = getDbForAnalytics();
  const events = fetchTraktEvents(db, range);

  // ── Totals ──
  let movieCount = 0;
  let episodeCount = 0;
  let totalTicks = 0;
  let unmatchedCount = 0;
  for (const e of events) {
    if (e.trakt_type === 'movie') movieCount++;
    else episodeCount++;
    if (e.matched_runtime_ticks) totalTicks += e.matched_runtime_ticks;
    if (!e.matched_emby_id) unmatchedCount++;
  }
  const totalWatchTimeSeconds = Math.round(totalTicks * SECONDS_PER_TICK);

  // ── Activity by day ──
  // Track count + summed runtime ticks per day. Unmatched events contribute
  // to count but 0 to seconds (no local runtime to credit).
  const byDay = new Map<string, { count: number; ticks: number }>();
  for (const e of events) {
    const day = e.watched_at.slice(0, 10);
    const cur = byDay.get(day) ?? { count: 0, ticks: 0 };
    cur.count++;
    if (e.matched_runtime_ticks) cur.ticks += e.matched_runtime_ticks;
    byDay.set(day, cur);
  }
  const activityByDay = Array.from(byDay.entries())
    .map(([date, v]) => ({ date, count: v.count, watchTimeSeconds: Math.round(v.ticks * SECONDS_PER_TICK) }))
    .sort((a, b) => a.date.localeCompare(b.date));

  // ── Top series (by episode count) ──
  const seriesAgg = new Map<string, {
    count: number;
    emby_id: string | null;
    server_id: string | null;
    name: string | null;
    image_tags: string | null;
  }>();
  for (const e of events) {
    if (e.trakt_type !== 'episode' || !e.show_tmdb_id) continue;
    const cur = seriesAgg.get(e.show_tmdb_id) ?? {
      count: 0, emby_id: null, server_id: null, name: null, image_tags: null,
    };
    cur.count++;
    // Resolve series poster via the series row joined as part of episode match.
    // The episode-level join exposes only the episode's tags — we need a
    // separate series lookup. Cheap because seriesAgg is small (top-N).
    seriesAgg.set(e.show_tmdb_id, cur);
  }
  // Pull series posters in one round-trip.
  const showTmdbIds = Array.from(seriesAgg.keys());
  if (showTmdbIds.length > 0) {
    const seriesRows = db
      .prepare(
        `SELECT tmdb_id, emby_id, server_id, name, image_tags
         FROM items
         WHERE type = 'Series'
           AND tmdb_id IS NOT NULL
           AND tmdb_id IN (${showTmdbIds.map(() => '?').join(',')})`,
      )
      .all(...showTmdbIds) as Array<{ tmdb_id: string; emby_id: string; server_id: string; name: string; image_tags: string | null }>;
    for (const r of seriesRows) {
      const cur = seriesAgg.get(r.tmdb_id);
      if (cur) {
        cur.emby_id = r.emby_id;
        cur.server_id = r.server_id;
        cur.name = r.name;
        cur.image_tags = r.image_tags;
      }
    }
  }
  const topSeries = Array.from(seriesAgg.entries())
    .map(([tmdb, v]) => ({
      id: v.emby_id ?? `trakt:show:${tmdb}`,
      name: v.name ?? `(unmatched show ${tmdb})`,
      imageUrl: v.emby_id && v.server_id
        ? buildImageUrlForServer(v.server_id, v.emby_id, extractPrimaryTag(v.image_tags))
        : null,
      episodeCount: v.count,
    }))
    .sort((a, b) => b.episodeCount - a.episodeCount || a.name.localeCompare(b.name))
    .slice(0, 10);

  // ── Top movies ──
  const movieAgg = new Map<string, {
    count: number;
    lastPlayed: string;
    emby_id: string | null;
    server_id: string | null;
    name: string | null;
    image_tags: string | null;
  }>();
  for (const e of events) {
    if (e.trakt_type !== 'movie' || !e.tmdb_id) continue;
    const cur = movieAgg.get(e.tmdb_id) ?? {
      count: 0,
      lastPlayed: e.watched_at,
      emby_id: e.matched_emby_id,
      server_id: e.matched_server_id,
      name: e.matched_name,
      image_tags: e.matched_image_tags,
    };
    cur.count++;
    if (e.watched_at > cur.lastPlayed) cur.lastPlayed = e.watched_at;
    movieAgg.set(e.tmdb_id, cur);
  }
  const topMovies = Array.from(movieAgg.entries())
    .map(([tmdb, v]) => ({
      id: v.emby_id ?? `trakt:movie:${tmdb}`,
      name: v.name ?? `(unmatched movie ${tmdb})`,
      imageUrl: v.emby_id && v.server_id
        ? buildImageUrlForServer(v.server_id, v.emby_id, extractPrimaryTag(v.image_tags))
        : null,
      lastPlayed: v.lastPlayed,
    }))
    .sort((a, b) => (b.lastPlayed ?? '').localeCompare(a.lastPlayed ?? ''))
    .slice(0, 10);

  // ── Genre breakdown ──
  // Reuse local genre aggregator. Unmatched events contribute to "Unknown".
  const genreRows: RawGenreRow[] = [];
  let unknownTicks = 0;
  for (const e of events) {
    if (e.matched_genres && e.matched_runtime_ticks) {
      genreRows.push({ genres: e.matched_genres, runtime_ticks: e.matched_runtime_ticks });
    } else if (!e.matched_emby_id) {
      // No local match → no runtime known either. Best-effort: assume 90 min
      // for movies, 30 min for episodes so the "Unknown" bucket isn't
      // perpetually empty even when most of Trakt history isn't in library.
      unknownTicks += e.trakt_type === 'movie' ? 90 * 60 * 10_000_000 : 30 * 60 * 10_000_000;
    }
  }
  const aggregated = aggregateGenres(genreRows);
  if (unknownTicks > 0) {
    const unknownSec = Math.round(unknownTicks * SECONDS_PER_TICK);
    aggregated.push({ genre: 'Unknown', watchTimeSeconds: unknownSec, pct: 0 });
    const totalSec = aggregated.reduce((sum, x) => sum + x.watchTimeSeconds, 0);
    for (const a of aggregated) {
      a.pct = totalSec > 0 ? Math.round((a.watchTimeSeconds / totalSec) * 1000) / 10 : 0;
    }
  }

  const rangeMs = new Date(range.rangeEnd).getTime() - new Date(range.rangeStart).getTime();
  const weeks = Math.max(1, rangeMs / (7 * 24 * 60 * 60 * 1000));
  const avgPerWeekSeconds = Math.round(totalWatchTimeSeconds / weeks);

  return {
    source: 'trakt',
    range,
    totalWatched: { movies: movieCount, episodes: episodeCount },
    totalWatchTimeSeconds,
    // Trakt doesn't track per-item in-progress state. Use the local cache's
    // in-progress count even in Trakt mode — same semantic ("currently
    // watching") and it's the one signal we actually have.
    inProgressSeriesCount: localInProgressSeriesCount(db),
    avgPerWeekSeconds,
    activityByDay,
    topSeries,
    topMovies,
    genreBreakdown: aggregated,
    lifetime,
    unmatchedTraktCount: unmatchedCount,
  };
}

// ── Combined merge ──────────────────────────────────────

/**
 * Union the Local + Trakt views, deduping by item identity. Prefers Trakt's
 * watched_at when both sources have an entry (Trakt history is canonical
 * across devices).
 *
 * Implementation note: rather than do a real SQL UNION join, we compute each
 * source independently and merge in JS. Activity-by-day and totals sum the
 * Trakt-only events (i.e. plays Trakt knows about but local doesn't) on top
 * of the local view. The vast majority of dedup keys overlap so this stays
 * cheap.
 */
function computeCombinedAnalytics(range: AnalyticsRange, lifetime: AnalyticsLifetimeBlock | null): AnalyticsStats {
  const local = computeLocalAnalytics(range);
  const trakt = computeTraktAnalytics(range, lifetime);

  // Identity sets so we can detect Trakt-only events.
  const db = getDbForAnalytics();
  const events = fetchTraktEvents(db, range);

  // Build set of (type, identity) keys local already covers, to avoid
  // double-counting when local + Trakt both have the same row.
  const localPlayed = db
    .prepare(
      `SELECT type, tmdb_id, series_id, season_number, episode_number
       FROM items
       WHERE played = 1
         AND last_played_date IS NOT NULL
         AND last_played_date BETWEEN ? AND ?`,
    )
    .all(range.rangeStart, range.rangeEnd) as Array<{
      type: string; tmdb_id: string | null; series_id: string | null;
      season_number: number | null; episode_number: number | null;
    }>;

  // Look up series.tmdb_id once for each unique series_id present locally.
  const seriesIds = new Set(localPlayed.filter((r) => r.type === 'Episode' && r.series_id).map((r) => r.series_id!));
  let seriesTmdbBySeriesId = new Map<string, string>();
  if (seriesIds.size > 0) {
    const seriesRows = db
      .prepare(
        `SELECT emby_id, tmdb_id FROM items
         WHERE type = 'Series' AND emby_id IN (${Array.from(seriesIds).map(() => '?').join(',')})`,
      )
      .all(...Array.from(seriesIds)) as Array<{ emby_id: string; tmdb_id: string | null }>;
    seriesTmdbBySeriesId = new Map(seriesRows.filter((r) => r.tmdb_id).map((r) => [r.emby_id, r.tmdb_id!]));
  }
  const localKeys = new Set<string>();
  for (const r of localPlayed) {
    if (r.type === 'Movie' && r.tmdb_id) {
      localKeys.add(`movie:${r.tmdb_id}`);
    } else if (r.type === 'Episode' && r.series_id && r.season_number != null && r.episode_number != null) {
      const showTmdb = seriesTmdbBySeriesId.get(r.series_id);
      if (showTmdb) localKeys.add(`episode:${showTmdb}:${r.season_number}:${r.episode_number}`);
    }
  }

  // Trakt-only deltas to layer on top of local.
  let extraMovies = 0;
  let extraEpisodes = 0;
  let extraTicks = 0;
  const extraByDay = new Map<string, { count: number; ticks: number }>();
  for (const e of events) {
    const key = e.trakt_type === 'movie'
      ? (e.tmdb_id ? `movie:${e.tmdb_id}` : null)
      : (e.show_tmdb_id && e.season_number != null && e.episode_number != null
          ? `episode:${e.show_tmdb_id}:${e.season_number}:${e.episode_number}`
          : null);
    if (!key || localKeys.has(key)) continue;
    if (e.trakt_type === 'movie') extraMovies++;
    else extraEpisodes++;
    if (e.matched_runtime_ticks) extraTicks += e.matched_runtime_ticks;
    const day = e.watched_at.slice(0, 10);
    const cur = extraByDay.get(day) ?? { count: 0, ticks: 0 };
    cur.count++;
    if (e.matched_runtime_ticks) cur.ticks += e.matched_runtime_ticks;
    extraByDay.set(day, cur);
  }

  // Merge activityByDay. Sum both count and seconds across the two sources.
  const dayMap = new Map<string, { count: number; watchTimeSeconds: number }>();
  for (const d of local.activityByDay) {
    dayMap.set(d.date, { count: d.count, watchTimeSeconds: d.watchTimeSeconds });
  }
  for (const [date, v] of extraByDay) {
    const cur = dayMap.get(date) ?? { count: 0, watchTimeSeconds: 0 };
    cur.count += v.count;
    cur.watchTimeSeconds += Math.round(v.ticks * SECONDS_PER_TICK);
    dayMap.set(date, cur);
  }
  const activityByDay = Array.from(dayMap.entries())
    .map(([date, v]) => ({ date, count: v.count, watchTimeSeconds: v.watchTimeSeconds }))
    .sort((a, b) => a.date.localeCompare(b.date));

  const totalWatchTimeSeconds = local.totalWatchTimeSeconds + Math.round(extraTicks * SECONDS_PER_TICK);
  const rangeMs = new Date(range.rangeEnd).getTime() - new Date(range.rangeStart).getTime();
  const weeks = Math.max(1, rangeMs / (7 * 24 * 60 * 60 * 1000));

  // For top lists / genres, prefer the union by simple sum — Trakt's lists
  // are usually a superset and the dedup above caps double counting on
  // overlap. Series/movies that only Trakt knows about already appear in
  // `trakt.topSeries`/`topMovies` with `id: 'trakt:show:<tmdb>'` so the UI
  // letter-falls-back posters automatically.
  return {
    source: 'combined',
    range,
    totalWatched: {
      movies: local.totalWatched.movies + extraMovies,
      episodes: local.totalWatched.episodes + extraEpisodes,
    },
    totalWatchTimeSeconds,
    inProgressSeriesCount: local.inProgressSeriesCount,
    avgPerWeekSeconds: Math.round(totalWatchTimeSeconds / weeks),
    activityByDay,
    topSeries: trakt.topSeries.length >= local.topSeries.length ? trakt.topSeries : local.topSeries,
    topMovies: trakt.topMovies.length >= local.topMovies.length ? trakt.topMovies : local.topMovies,
    genreBreakdown: trakt.genreBreakdown.length > 0 ? trakt.genreBreakdown : local.genreBreakdown,
    lifetime,
    unmatchedTraktCount: trakt.unmatchedTraktCount,
  };
}

// ── Dispatcher ──────────────────────────────────────────

export function computeAnalytics(
  range: AnalyticsRange,
  source: AnalyticsSource,
  lifetime: AnalyticsLifetimeBlock | null,
): AnalyticsStats {
  if (source === 'trakt') return computeTraktAnalytics(range, lifetime);
  if (source === 'combined') return computeCombinedAnalytics(range, lifetime);
  return computeLocalAnalytics(range);
}
