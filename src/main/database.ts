import Database from 'better-sqlite3';
import { app } from 'electron';
import { join } from 'path';

let db: Database.Database | null = null;

// Wired at startup from ipc-handlers.ts to avoid a top-level circular import
// against virtual-library (which depends on this module). The bundler flattens
// to a single file in production builds, so a lazy require('./virtual-library')
// at call time can't resolve — the callback pattern dodges that entirely.
let onWatchlistMutated: (() => void) | null = null;
export function setWatchlistMutationHook(cb: () => void): void {
  onWatchlistMutated = cb;
}

// Escape LIKE wildcards so user input like "50%" matches the literal substring
// instead of acting as a wildcard. Paired with `ESCAPE '\'` in the SQL clause.
function escapeLike(s: string): string {
  return s.replace(/[\\%_]/g, '\\$&');
}

export interface ItemRow {
  emby_id: string;
  server_id: string;
  library_id: string;
  library_name: string | null;
  type: string;
  name: string;
  sort_name: string | null;
  overview: string | null;
  tmdb_id: string | null;
  imdb_id: string | null;
  tvdb_id: string | null;
  production_year: number | null;
  premiere_date: string | null;
  community_rating: number | null;
  official_rating: string | null;
  runtime_ticks: number | null;
  genres: string | null;
  studios: string | null;
  image_tags: string | null;
  backdrop_tags: string | null;
  series_id: string | null;
  series_name: string | null;
  season_id: string | null;
  season_number: number | null;
  episode_number: number | null;
  media_sources: string | null;
  played: number;
  play_count: number;
  is_favorite: number;
  playback_position_ticks: number;
  played_percentage: number;
  date_created: string | null;
  date_modified: string | null;
  last_played_date: string | null;
  cached_at: string | null;
  dedup_group_id: string | null;
  // Attached on IPC return only — never persisted. Holds dedup-sibling image
  // URLs the renderer cycles through if the primary fails to load.
  image_fallbacks?: string[];
  backdrop_fallbacks?: string[];
}

export interface ItemFilters {
  type?: string;
  libraryId?: string;
  limit?: number;
  offset?: number;
  sortBy?: string;
  sortOrder?: 'ASC' | 'DESC';
  search?: string;
  isFavorite?: boolean;
  isPlayed?: boolean;
}

export interface DbStats {
  totalItems: number;
  itemsByLibrary: { library_id: string; library_name: string | null; count: number }[];
  lastSyncTime: string | null;
}

function getDb(): Database.Database {
  if (!db) throw new Error('Database not initialized');
  return db;
}

// ── Sort sanitization ──────────────────────────────────────

const ALLOWED_SORT_COLUMNS = ['name', 'sort_name', 'date_created', 'production_year', 'community_rating', 'runtime_ticks', 'premiere_date'];
const ALLOWED_SORT_ORDERS = ['ASC', 'DESC'];

/** Map Emby-style sort names to DB column names */
const EMBY_SORT_MAP: Record<string, string> = {
  DateCreated: 'date_created',
  SortName: 'sort_name',
  CommunityRating: 'community_rating',
  ProductionYear: 'production_year',
  RunTimeTicks: 'runtime_ticks',
  Name: 'name',
  name: 'name',
  date_created: 'date_created',
};

function sanitizeSort(
  sortBy: string,
  sortOrder: string,
  defaultSortBy = 'date_created',
  defaultSortOrder = 'DESC',
): { safeSortBy: string; safeSortOrder: string } {
  const safeSortBy = ALLOWED_SORT_COLUMNS.includes(sortBy.toLowerCase()) ? sortBy.toLowerCase() : defaultSortBy;
  const safeSortOrder = ALLOWED_SORT_ORDERS.includes(sortOrder.toUpperCase()) ? sortOrder.toUpperCase() : defaultSortOrder;
  return { safeSortBy, safeSortOrder };
}

export function initDatabase(): void {
  const dbPath = join(app.getPath('userData'), 'nocturne.db');
  db = new Database(dbPath);

  db.pragma('journal_mode = WAL');
  db.pragma('foreign_keys = ON');

  db.exec(`
    CREATE TABLE IF NOT EXISTS items (
      emby_id TEXT PRIMARY KEY,
      server_id TEXT NOT NULL,
      library_id TEXT NOT NULL,
      library_name TEXT,
      type TEXT NOT NULL,
      name TEXT NOT NULL,
      sort_name TEXT,
      overview TEXT,
      tmdb_id TEXT,
      imdb_id TEXT,
      tvdb_id TEXT,
      production_year INTEGER,
      premiere_date TEXT,
      community_rating REAL,
      official_rating TEXT,
      runtime_ticks INTEGER,
      genres TEXT,
      studios TEXT,
      image_tags TEXT,
      backdrop_tags TEXT,
      series_id TEXT,
      series_name TEXT,
      season_id TEXT,
      season_number INTEGER,
      episode_number INTEGER,
      media_sources TEXT,
      played INTEGER DEFAULT 0,
      play_count INTEGER DEFAULT 0,
      is_favorite INTEGER DEFAULT 0,
      playback_position_ticks INTEGER DEFAULT 0,
      played_percentage REAL DEFAULT 0,
      date_created TEXT,
      date_modified TEXT,
      last_played_date TEXT,
      cached_at TEXT DEFAULT (datetime('now')),
      dedup_group_id TEXT
    );

    CREATE INDEX IF NOT EXISTS idx_items_type ON items(type);
    CREATE INDEX IF NOT EXISTS idx_items_library ON items(library_id);
    CREATE INDEX IF NOT EXISTS idx_items_tmdb ON items(tmdb_id);
    CREATE INDEX IF NOT EXISTS idx_items_imdb ON items(imdb_id);
    CREATE INDEX IF NOT EXISTS idx_items_series ON items(series_id);
    CREATE INDEX IF NOT EXISTS idx_items_dedup ON items(dedup_group_id);
    CREATE INDEX IF NOT EXISTS idx_items_name ON items(name);
    -- idx_items_last_played is created AFTER the ALTER-TABLE migration below,
    -- so existing DBs predating the column don't hit a missing-column error
    -- while this bulk exec runs.

    CREATE TABLE IF NOT EXISTS dedup_groups (
      group_id TEXT PRIMARY KEY,
      tmdb_id TEXT,
      imdb_id TEXT,
      type TEXT,
      primary_item_id TEXT,
      name TEXT,
      year INTEGER
    );

    CREATE TABLE IF NOT EXISTS dedup_episode_groups (
      group_id TEXT PRIMARY KEY,
      series_group_id TEXT NOT NULL,
      season_number INTEGER,
      episode_number INTEGER
    );
    CREATE INDEX IF NOT EXISTS idx_dedup_ep_series ON dedup_episode_groups(series_group_id);

    CREATE TABLE IF NOT EXISTS sync_state (
      key TEXT PRIMARY KEY,
      value TEXT
    );

    CREATE TABLE IF NOT EXISTS image_cache (
      url TEXT PRIMARY KEY,
      local_path TEXT NOT NULL,
      cached_at TEXT DEFAULT (datetime('now')),
      size_bytes INTEGER
    );

    CREATE TABLE IF NOT EXISTS trakt_scrobble_queue (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      action TEXT NOT NULL,
      payload TEXT NOT NULL,
      emby_id TEXT,
      attempts INTEGER DEFAULT 0,
      next_retry_at TEXT,
      last_error TEXT,
      created_at TEXT DEFAULT (datetime('now'))
    );
    CREATE INDEX IF NOT EXISTS idx_trakt_queue_retry ON trakt_scrobble_queue(next_retry_at);

    -- Phase 2: Trakt watched-state mirror.
    -- Synthesized "key" column avoids COALESCE-on-NULL primary-key pitfalls.
    -- Movies → 'movie:<tmdb_id>' (or 'movie:imdb:<imdb_id>' if no tmdb).
    -- Episodes → 'episode:<show_tmdb_id>:<season>:<ep>'.
    CREATE TABLE IF NOT EXISTS trakt_watched_history (
      key TEXT PRIMARY KEY,
      trakt_type TEXT NOT NULL,
      tmdb_id TEXT,
      imdb_id TEXT,
      show_tmdb_id TEXT,
      season_number INTEGER,
      episode_number INTEGER,
      watched_at TEXT NOT NULL,
      synced_at TEXT DEFAULT (datetime('now'))
    );
    CREATE INDEX IF NOT EXISTS idx_thw_movie ON trakt_watched_history(tmdb_id) WHERE trakt_type = 'movie';
    CREATE INDEX IF NOT EXISTS idx_thw_episode ON trakt_watched_history(show_tmdb_id, season_number, episode_number) WHERE trakt_type = 'episode';

    -- Phase 3: Trakt watchlist mirror.
    CREATE TABLE IF NOT EXISTS trakt_watchlist (
      key TEXT PRIMARY KEY,
      trakt_type TEXT NOT NULL,
      tmdb_id TEXT,
      imdb_id TEXT,
      trakt_id INTEGER,
      title TEXT,
      year INTEGER,
      overview TEXT,
      added_at TEXT,
      synced_at TEXT DEFAULT (datetime('now'))
    );
    CREATE INDEX IF NOT EXISTS idx_twl_tmdb ON trakt_watchlist(tmdb_id);

    -- Phase 4: Trakt rating cache (24h TTL — pruned lazily).
    CREATE TABLE IF NOT EXISTS trakt_ratings (
      tmdb_id TEXT NOT NULL,
      trakt_type TEXT NOT NULL,
      rating REAL,
      votes INTEGER,
      fetched_at TEXT NOT NULL,
      PRIMARY KEY (tmdb_id, trakt_type)
    );
  `);

  // Migrations for existing DBs predating columns added above. SQLite has no
  // "ADD COLUMN IF NOT EXISTS", so we probe table_info first.
  const itemCols = db.prepare(`PRAGMA table_info(items)`).all() as Array<{ name: string }>;
  const itemColNames = new Set(itemCols.map((c) => c.name));
  if (!itemColNames.has('last_played_date')) {
    db.exec(`ALTER TABLE items ADD COLUMN last_played_date TEXT`);
  }
  // Indexes referencing migrated columns must run AFTER the ALTER above. Fresh
  // installs hit this too (no-op via IF NOT EXISTS).
  db.exec(`CREATE INDEX IF NOT EXISTS idx_items_last_played ON items(last_played_date) WHERE last_played_date IS NOT NULL`);
}

export function closeDatabase(): void {
  if (db) {
    db.close();
    db = null;
  }
}

// ── Item operations ─────────────────────────────────────

const UPSERT_SQL = `
  INSERT INTO items (
    emby_id, server_id, library_id, library_name, type, name, sort_name,
    overview, tmdb_id, imdb_id, tvdb_id, production_year, premiere_date,
    community_rating, official_rating, runtime_ticks, genres, studios,
    image_tags, backdrop_tags, series_id, series_name, season_id,
    season_number, episode_number, media_sources, played, play_count,
    is_favorite, playback_position_ticks, played_percentage,
    date_created, date_modified, last_played_date, cached_at
  ) VALUES (
    @emby_id, @server_id, @library_id, @library_name, @type, @name, @sort_name,
    @overview, @tmdb_id, @imdb_id, @tvdb_id, @production_year, @premiere_date,
    @community_rating, @official_rating, @runtime_ticks, @genres, @studios,
    @image_tags, @backdrop_tags, @series_id, @series_name, @season_id,
    @season_number, @episode_number, @media_sources, @played, @play_count,
    @is_favorite, @playback_position_ticks, @played_percentage,
    @date_created, @date_modified, @last_played_date, datetime('now')
  ) ON CONFLICT(emby_id) DO UPDATE SET
    server_id=excluded.server_id, library_id=excluded.library_id,
    library_name=excluded.library_name, type=excluded.type, name=excluded.name,
    sort_name=excluded.sort_name, overview=excluded.overview,
    tmdb_id=excluded.tmdb_id, imdb_id=excluded.imdb_id, tvdb_id=excluded.tvdb_id,
    production_year=excluded.production_year, premiere_date=excluded.premiere_date,
    community_rating=excluded.community_rating, official_rating=excluded.official_rating,
    runtime_ticks=excluded.runtime_ticks, genres=excluded.genres, studios=excluded.studios,
    image_tags=excluded.image_tags, backdrop_tags=excluded.backdrop_tags,
    series_id=excluded.series_id, series_name=excluded.series_name,
    season_id=excluded.season_id, season_number=excluded.season_number,
    episode_number=excluded.episode_number, media_sources=excluded.media_sources,
    played=excluded.played, play_count=excluded.play_count,
    is_favorite=excluded.is_favorite, playback_position_ticks=excluded.playback_position_ticks,
    played_percentage=excluded.played_percentage,
    date_created=excluded.date_created, date_modified=excluded.date_modified,
    last_played_date=COALESCE(excluded.last_played_date, items.last_played_date),
    cached_at=datetime('now')
`;

/* eslint-disable @typescript-eslint/no-explicit-any */
function mapEmbyItem(item: any, serverId: string, libraryId: string, libraryName?: string): Record<string, unknown> {
  const providerIds = item.ProviderIds || {};
  const userData = item.UserData || {};
  return {
    emby_id: item.Id,
    server_id: serverId,
    library_id: libraryId,
    library_name: libraryName || null,
    type: item.Type,
    name: item.Name,
    sort_name: item.SortName || null,
    overview: item.Overview || null,
    tmdb_id: providerIds.Tmdb || null,
    imdb_id: providerIds.Imdb || null,
    tvdb_id: providerIds.Tvdb || null,
    production_year: item.ProductionYear || null,
    premiere_date: item.PremiereDate || null,
    community_rating: item.CommunityRating || null,
    official_rating: item.OfficialRating || null,
    runtime_ticks: item.RunTimeTicks || null,
    genres: item.Genres ? JSON.stringify(item.Genres) : null,
    studios: item.Studios ? JSON.stringify(item.Studios) : null,
    image_tags: item.ImageTags ? JSON.stringify(item.ImageTags) : null,
    backdrop_tags: item.BackdropImageTags ? JSON.stringify(item.BackdropImageTags) : null,
    series_id: item.SeriesId || null,
    series_name: item.SeriesName || null,
    season_id: item.SeasonId || null,
    season_number: item.ParentIndexNumber ?? null,
    episode_number: item.IndexNumber ?? null,
    media_sources: item.MediaSources ? JSON.stringify(
      item.MediaSources.map((ms: any) => ({
        Id: ms.Id,
        Container: ms.Container,
        Size: ms.Size,
        Bitrate: ms.Bitrate,
        VideoCodec: ms.MediaStreams?.find((s: any) => s.Type === 'Video')?.Codec,
        AudioCodec: ms.MediaStreams?.find((s: any) => s.Type === 'Audio')?.Codec,
        Width: ms.MediaStreams?.find((s: any) => s.Type === 'Video')?.Width,
        Height: ms.MediaStreams?.find((s: any) => s.Type === 'Video')?.Height,
      }))
    ) : null,
    played: userData.Played ? 1 : 0,
    play_count: userData.PlayCount || 0,
    is_favorite: userData.IsFavorite ? 1 : 0,
    playback_position_ticks: userData.PlaybackPositionTicks || 0,
    played_percentage: userData.PlayedPercentage || 0,
    date_created: item.DateCreated || null,
    date_modified: item.DateLastSaved || item.DateModified || null,
    last_played_date: userData.LastPlayedDate || null,
  };
}
/* eslint-enable @typescript-eslint/no-explicit-any */

export function upsertItem(item: Record<string, unknown>, serverId: string, libraryId: string, libraryName?: string): void {
  const row = mapEmbyItem(item, serverId, libraryId, libraryName);
  getDb().prepare(UPSERT_SQL).run(row);
}

export function upsertItems(items: Record<string, unknown>[], serverId: string, libraryId: string, libraryName?: string): void {
  const d = getDb();
  const stmt = d.prepare(UPSERT_SQL);
  const transaction = d.transaction((rows: Record<string, unknown>[]) => {
    for (const item of rows) {
      stmt.run(item);
    }
  });
  const rows = items.map((item) => mapEmbyItem(item, serverId, libraryId, libraryName));
  transaction(rows);
}

export function getItem(embyId: string): ItemRow | undefined {
  return getDb().prepare('SELECT * FROM items WHERE emby_id = ?').get(embyId) as ItemRow | undefined;
}

/**
 * Upsert items whose true library is unknown to the caller (the sync engine's
 * /Items/Resume refresh has no library context). For rows already cached, the
 * existing library_id/library_name is preserved — blindly upserting under a
 * fallback library reassigned every resumable item to the server's first
 * library, corrupting library views and dedup's per-library duplicate scan.
 */
export function upsertItemsPreservingLibrary(
  items: Record<string, unknown>[],
  serverId: string,
  fallbackLibraryId: string,
  fallbackLibraryName?: string,
): void {
  const d = getDb();
  const lookup = d.prepare('SELECT library_id, library_name FROM items WHERE emby_id = ?');
  const stmt = d.prepare(UPSERT_SQL);
  const tx = d.transaction((batch: Record<string, unknown>[]) => {
    for (const item of batch) {
      const existing = lookup.get((item as { Id?: string }).Id) as
        | { library_id: string; library_name: string | null }
        | undefined;
      const row = mapEmbyItem(
        item,
        serverId,
        existing?.library_id ?? fallbackLibraryId,
        existing?.library_name ?? fallbackLibraryName ?? undefined,
      );
      stmt.run(row);
    }
  });
  tx(items);
}

export function getItems(filters: ItemFilters = {}): { items: ItemRow[]; total: number } {
  const conditions: string[] = [];
  const params: unknown[] = [];

  if (filters.type) {
    conditions.push('type = ?');
    params.push(filters.type);
  }
  if (filters.libraryId) {
    conditions.push('library_id = ?');
    params.push(filters.libraryId);
  }
  if (filters.search) {
    conditions.push("name LIKE ? ESCAPE '\\'");
    params.push(`%${escapeLike(filters.search)}%`);
  }
  if (filters.isFavorite !== undefined) {
    conditions.push('is_favorite = ?');
    params.push(filters.isFavorite ? 1 : 0);
  }
  if (filters.isPlayed !== undefined) {
    conditions.push('played = ?');
    params.push(filters.isPlayed ? 1 : 0);
  }

  const where = conditions.length > 0 ? `WHERE ${conditions.join(' AND ')}` : '';
  const { safeSortBy: sortCol, safeSortOrder: sortDir } = sanitizeSort(filters.sortBy || 'name', filters.sortOrder || 'ASC', 'name', 'ASC');
  const limit = filters.limit || 50;
  const offset = filters.offset || 0;

  const d = getDb();
  const total = (d.prepare(`SELECT COUNT(*) as count FROM items ${where}`).get(...params) as { count: number }).count;
  const items = d.prepare(`SELECT * FROM items ${where} ORDER BY ${sortCol} ${sortDir} LIMIT ? OFFSET ?`).all(...params, limit, offset) as ItemRow[];

  return { items, total };
}

export function getResumeItems(limit = 12): ItemRow[] {
  return getDb()
    .prepare('SELECT * FROM items WHERE playback_position_ticks > 0 ORDER BY cached_at DESC LIMIT ?')
    .all(limit) as ItemRow[];
}

export function getResumeItemsDeduped(limit = 12): ItemRow[] {
  // For each dedup group that contains a watched item, pick the member with the
  // highest playback_position_ticks (the version the user is actually resuming) —
  // NOT the dedup group's "primary" (which may be an untouched 4K copy).
  // Items without a dedup group are returned directly if they have playback progress.
  // played = 0 everywhere: a played row with stale ticks is a finished item, not
  // a resumable one, and must neither appear nor win the group MAX.
  // Ordered by when the user last played — cached_at is sync recency, which
  // reshuffled the row after every sync.
  return getDb()
    .prepare(`
      SELECT i.*
      FROM items i
      WHERE i.playback_position_ticks > 0
        AND i.played = 0
        AND (
          i.dedup_group_id IS NULL
          OR i.dedup_group_id = ''
          OR i.playback_position_ticks = (
            SELECT MAX(i2.playback_position_ticks)
            FROM items i2
            WHERE i2.dedup_group_id = i.dedup_group_id
              AND i2.played = 0
          )
        )
      GROUP BY COALESCE(NULLIF(i.dedup_group_id, ''), i.emby_id)
      ORDER BY COALESCE(i.last_played_date, i.cached_at) DESC
      LIMIT ?
    `)
    .all(limit) as ItemRow[];
}

const ALLOWED_USER_DATA_COLUMNS = new Set([
  'played', 'play_count', 'is_favorite', 'playback_position_ticks', 'played_percentage',
  'last_played_date',
]);

export function updateItemUserData(
  embyId: string,
  data: Partial<{
    played: number;
    play_count: number;
    is_favorite: number;
    playback_position_ticks: number;
    played_percentage: number;
    last_played_date: string;
  }>,
): void {
  const fields: string[] = [];
  const values: unknown[] = [];
  for (const [key, value] of Object.entries(data)) {
    if (!ALLOWED_USER_DATA_COLUMNS.has(key)) continue;
    fields.push(`${key} = ?`);
    values.push(value);
  }
  if (fields.length === 0) return;
  values.push(embyId);
  getDb().prepare(`UPDATE items SET ${fields.join(', ')} WHERE emby_id = ?`).run(...values);
}

export function getLatestItems(libraryId: string, limit = 20): ItemRow[] {
  return getDb()
    .prepare('SELECT * FROM items WHERE library_id = ? AND type IN (\'Movie\', \'Series\') ORDER BY date_created DESC LIMIT ?')
    .all(libraryId, limit) as ItemRow[];
}

export function searchItems(query: string, limit = 24): ItemRow[] {
  return getDb()
    .prepare("SELECT * FROM items WHERE name LIKE ? ESCAPE '\\' AND type IN ('Movie', 'Series', 'Episode') ORDER BY name ASC LIMIT ?")
    .all(`%${escapeLike(query)}%`, limit) as ItemRow[];
}

// ── Multi-library queries (for virtual libraries) ───────

export function getItemsMultiLibrary(
  libraryIds: string[],
  opts: {
    startIndex?: number;
    limit?: number;
    sortBy?: string;
    sortOrder?: string;
    searchTerm?: string;
    itemType?: string;
  } = {},
): { items: ItemRow[]; total: number } {
  const d = getDb();
  const conditions: string[] = [];
  const params: unknown[] = [];

  if (libraryIds.length === 1) {
    conditions.push('library_id = ?');
    params.push(libraryIds[0]);
  } else if (libraryIds.length > 1) {
    conditions.push(`library_id IN (${libraryIds.map(() => '?').join(',')})`);
    params.push(...libraryIds);
  }

  // Default: only show top-level items (Movie, Series), not episodes/seasons
  if (opts.itemType) {
    conditions.push('type = ?');
    params.push(opts.itemType);
  } else {
    conditions.push("type IN ('Movie', 'Series')");
  }

  if (opts.searchTerm) {
    conditions.push("name LIKE ? ESCAPE '\\'");
    params.push(`%${escapeLike(opts.searchTerm)}%`);
  }

  const where = conditions.length > 0 ? `WHERE ${conditions.join(' AND ')}` : '';

  const mappedCol = EMBY_SORT_MAP[opts.sortBy || 'DateCreated'] || 'date_created';
  const { safeSortBy: sortCol, safeSortOrder: sortDir } = sanitizeSort(mappedCol, opts.sortOrder || 'DESC');
  const limit = opts.limit || 40;
  const offset = opts.startIndex || 0;

  const total = (d.prepare(`SELECT COUNT(*) as count FROM items ${where}`).get(...params) as { count: number }).count;
  const items = d.prepare(
    `SELECT * FROM items ${where} ORDER BY ${sortCol} ${sortDir} LIMIT ? OFFSET ?`
  ).all(...params, limit, offset) as ItemRow[];

  return { items, total };
}

export function getLatestMultiLibrary(libraryIds: string[], limit = 20): ItemRow[] {
  const d = getDb();
  if (libraryIds.length === 0) return [];

  const placeholders = libraryIds.map(() => '?').join(',');
  return d.prepare(
    `SELECT * FROM items WHERE library_id IN (${placeholders}) AND type IN ('Movie', 'Series') ORDER BY date_created DESC LIMIT ?`
  ).all(...libraryIds, limit) as ItemRow[];
}

export function getHeroCandidates(libraryIds: string[], limit = 20): ItemRow[] {
  const d = getDb();
  if (libraryIds.length === 0) {
    return d.prepare(
      "SELECT * FROM items WHERE type IN ('Movie', 'Series') AND backdrop_tags IS NOT NULL AND backdrop_tags != '[]' ORDER BY RANDOM() LIMIT ?"
    ).all(limit) as ItemRow[];
  }
  const placeholders = libraryIds.map(() => '?').join(',');
  return d.prepare(
    `SELECT * FROM items WHERE library_id IN (${placeholders}) AND type IN ('Movie', 'Series') AND backdrop_tags IS NOT NULL AND backdrop_tags != '[]' ORDER BY RANDOM() LIMIT ?`
  ).all(...libraryIds, limit) as ItemRow[];
}

export function countItemsInLibraries(libraryIds: string[]): number {
  const d = getDb();
  if (libraryIds.length === 0) return 0;
  const placeholders = libraryIds.map(() => '?').join(',');
  const row = d.prepare(
    `SELECT COUNT(*) as count FROM items WHERE library_id IN (${placeholders}) AND type IN ('Movie', 'Series')`
  ).get(...libraryIds) as { count: number };
  return row.count;
}

// ── Sync state ──────────────────────────────────────────

export function getSyncState(key: string): string | null {
  const row = getDb().prepare('SELECT value FROM sync_state WHERE key = ?').get(key) as { value: string } | undefined;
  return row?.value ?? null;
}

export function setSyncState(key: string, value: string): void {
  getDb().prepare('INSERT INTO sync_state (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value').run(key, value);
}

export function deleteSyncState(key: string): void {
  getDb().prepare('DELETE FROM sync_state WHERE key = ?').run(key);
}

export function getDistinctLibrariesFromItems(): {
  serverId: string;
  libraryId: string;
  libraryName: string;
}[] {
  const rows = getDb()
    .prepare(
      `SELECT DISTINCT server_id AS serverId, library_id AS libraryId,
              library_name AS libraryName
       FROM items
       WHERE library_name IS NOT NULL AND library_name != ''`,
    )
    .all() as { serverId: string; libraryId: string; libraryName: string }[];
  return rows;
}

// ── Image cache metadata ────────────────────────────────

export function getImageCacheEntry(url: string): { local_path: string; cached_at: string } | undefined {
  return getDb().prepare('SELECT local_path, cached_at FROM image_cache WHERE url = ?').get(url) as { local_path: string; cached_at: string } | undefined;
}

export function setImageCacheEntry(url: string, localPath: string, sizeBytes: number): void {
  getDb().prepare(
    'INSERT INTO image_cache (url, local_path, size_bytes) VALUES (?, ?, ?) ON CONFLICT(url) DO UPDATE SET local_path=excluded.local_path, size_bytes=excluded.size_bytes, cached_at=datetime(\'now\')'
  ).run(url, localPath, sizeBytes);
}

export function getImageCacheTotalSize(): number {
  const row = getDb().prepare('SELECT COALESCE(SUM(size_bytes), 0) as total FROM image_cache').get() as { total: number };
  return row.total;
}

export function getOldestImageCacheEntries(limit: number): { url: string; local_path: string; size_bytes: number }[] {
  return getDb()
    .prepare('SELECT url, local_path, size_bytes FROM image_cache ORDER BY cached_at ASC LIMIT ?')
    .all(limit) as { url: string; local_path: string; size_bytes: number }[];
}

export function deleteImageCacheEntry(url: string): void {
  getDb().prepare('DELETE FROM image_cache WHERE url = ?').run(url);
}

// ── Stats ───────────────────────────────────────────────

export function getStats(): DbStats {
  const d = getDb();
  const totalItems = (d.prepare('SELECT COUNT(*) as count FROM items').get() as { count: number }).count;
  const itemsByLibrary = d.prepare(
    'SELECT library_id, library_name, COUNT(*) as count FROM items GROUP BY library_id'
  ).all() as { library_id: string; library_name: string | null; count: number }[];
  const lastSyncTime = getSyncState('lastFullSync');
  return { totalItems, itemsByLibrary, lastSyncTime };
}

// ── Dedup queries ──────────────────────────────────────

export function clearDedupGroups(): void {
  const d = getDb();
  d.exec('DELETE FROM dedup_groups');
  d.exec('DELETE FROM dedup_episode_groups');
  d.exec("UPDATE items SET dedup_group_id = NULL WHERE dedup_group_id IS NOT NULL AND dedup_group_id != ''");
}

export function insertDedupGroup(group: {
  group_id: string;
  tmdb_id: string | null;
  imdb_id: string | null;
  type: string;
  name: string;
  year: number | null;
  primary_item_id: string;
}): void {
  getDb().prepare(`
    INSERT OR REPLACE INTO dedup_groups (group_id, tmdb_id, imdb_id, type, name, year, primary_item_id)
    VALUES (@group_id, @tmdb_id, @imdb_id, @type, @name, @year, @primary_item_id)
  `).run(group);
}

export function setItemDedupGroup(embyIds: string[], groupId: string): void {
  const d = getDb();
  const stmt = d.prepare('UPDATE items SET dedup_group_id = ? WHERE emby_id = ?');
  const tx = d.transaction((ids: string[]) => {
    for (const id of ids) stmt.run(groupId, id);
  });
  tx(embyIds);
}

/**
 * Run a block of writes inside a single explicit transaction.
 * Nested better-sqlite3 transactions auto-promote to SAVEPOINTs, so this
 * composes safely with helpers like `setItemDedupGroup` that manage their own tx.
 */
export function withWriteTx<T>(fn: () => T): T {
  const d = getDb();
  const tx = d.transaction(fn);
  return tx();
}

export function insertDedupEpisodeGroup(group: {
  group_id: string;
  series_group_id: string;
  season_number: number | null;
  episode_number: number | null;
}): void {
  getDb().prepare(`
    INSERT OR REPLACE INTO dedup_episode_groups (group_id, series_group_id, season_number, episode_number)
    VALUES (@group_id, @series_group_id, @season_number, @episode_number)
  `).run(group);
}

/**
 * Return members of a dedup group relevant to the source item.
 * Because Phase A backfill puts episodes under their series' group, a raw
 * "WHERE dedup_group_id = ?" mixes Series + Episode rows. Filter by type, and
 * for Episodes narrow further to the same (season_number, episode_number).
 */
export function getGroupVersions(groupId: string, sourceItem: ItemRow): ItemRow[] {
  const d = getDb();
  if (sourceItem.type === 'Episode') {
    return d.prepare(
      `SELECT * FROM items
       WHERE dedup_group_id = ?
         AND type = 'Episode'
         AND season_number = ?
         AND episode_number = ?
       ORDER BY library_name`
    ).all(groupId, sourceItem.season_number, sourceItem.episode_number) as ItemRow[];
  }
  return d.prepare(
    `SELECT * FROM items
     WHERE dedup_group_id = ? AND type = ?
     ORDER BY library_name`
  ).all(groupId, sourceItem.type) as ItemRow[];
}

/**
 * All rows that represent the same "viewing" as itemId, for clearing resume
 * state across the dedup group. Movie → all Movie members; Episode → Episode
 * members matching (season_number, episode_number). Lone items (no group)
 * return just themselves.
 */
export function getResumeClearTargets(itemId: string): { emby_id: string; server_id: string }[] {
  const d = getDb();
  const item = d.prepare('SELECT * FROM items WHERE emby_id = ?').get(itemId) as ItemRow | undefined;
  if (!item) return [];
  if (!item.dedup_group_id) {
    return [{ emby_id: item.emby_id, server_id: item.server_id }];
  }
  if (item.type === 'Episode') {
    return d.prepare(
      `SELECT emby_id, server_id FROM items
       WHERE dedup_group_id = ?
         AND type = 'Episode'
         AND season_number = ?
         AND episode_number = ?`
    ).all(item.dedup_group_id, item.season_number, item.episode_number) as { emby_id: string; server_id: string }[];
  }
  return d.prepare(
    `SELECT emby_id, server_id FROM items
     WHERE dedup_group_id = ? AND type = ?`
  ).all(item.dedup_group_id, item.type) as { emby_id: string; server_id: string }[];
}

/**
 * Resolve dedup_group_id for a batch of emby ids. Unknown ids and items
 * without a group are omitted. Used by search to drop API results that are
 * already represented by a cache primary.
 */
export function getDedupGroupsForItemIds(ids: string[]): Record<string, string> {
  if (ids.length === 0) return {};
  const d = getDb();
  const placeholders = ids.map(() => '?').join(',');
  const rows = d.prepare(
    `SELECT emby_id, dedup_group_id FROM items
     WHERE emby_id IN (${placeholders})
       AND dedup_group_id IS NOT NULL AND dedup_group_id != ''`
  ).all(...ids) as { emby_id: string; dedup_group_id: string }[];
  const out: Record<string, string> = {};
  for (const r of rows) out[r.emby_id] = r.dedup_group_id;
  return out;
}

export function getEpisodeVersions(seriesGroupId: string, seasonNumber: number): {
  season_number: number;
  episode_number: number;
  items: ItemRow[];
}[] {
  const d = getDb();
  // Get all series emby_ids in this dedup group
  const seriesItems = d.prepare(
    "SELECT emby_id FROM items WHERE dedup_group_id = ? AND type = 'Series'"
  ).all(seriesGroupId) as { emby_id: string }[];

  if (seriesItems.length === 0) return [];

  const seriesIds = seriesItems.map((s) => s.emby_id);
  const placeholders = seriesIds.map(() => '?').join(',');

  const episodes = d.prepare(`
    SELECT * FROM items
    WHERE type = 'Episode'
    AND series_id IN (${placeholders})
    AND season_number = ?
    ORDER BY episode_number, library_name
  `).all(...seriesIds, seasonNumber) as ItemRow[];

  // Group by episode number
  const byEp = new Map<number, ItemRow[]>();
  for (const ep of episodes) {
    if (ep.episode_number == null) continue;
    const list = byEp.get(ep.episode_number) || [];
    list.push(ep);
    byEp.set(ep.episode_number, list);
  }

  return Array.from(byEp.entries())
    .sort(([a], [b]) => a - b)
    .map(([epNum, items]) => ({
      season_number: seasonNumber,
      episode_number: epNum,
      items,
    }));
}

/**
 * Return the previous and next episodes relative to `episodeId`, walking by
 * (season_number, episode_number) within the scope of the episode's dedup
 * group (so navigation crosses series versions). Falls back to same-series
 * scope when the episode has no dedup group. Prefers siblings on the same
 * series as the current episode when multiple versions share a slot.
 */
export function getAdjacentEpisodes(episodeId: string): { prev: ItemRow | null; next: ItemRow | null } {
  const d = getDb();
  const cur = d.prepare(`SELECT * FROM items WHERE emby_id = ? AND type = 'Episode'`).get(episodeId) as ItemRow | undefined;
  if (!cur || cur.season_number == null || cur.episode_number == null) {
    return { prev: null, next: null };
  }

  const useDedup = !!cur.dedup_group_id && cur.dedup_group_id !== '';
  const scopeCol = useDedup ? 'dedup_group_id' : 'series_id';
  const scopeVal = useDedup ? cur.dedup_group_id : cur.series_id;
  if (!scopeVal) return { prev: null, next: null };

  const nextSql = `
    SELECT * FROM items
    WHERE type = 'Episode' AND ${scopeCol} = ?
      AND season_number IS NOT NULL AND episode_number IS NOT NULL
      AND (season_number > ? OR (season_number = ? AND episode_number > ?))
    ORDER BY season_number ASC, episode_number ASC,
             CASE WHEN series_id = ? THEN 0 ELSE 1 END ASC
    LIMIT 1
  `;
  const prevSql = `
    SELECT * FROM items
    WHERE type = 'Episode' AND ${scopeCol} = ?
      AND season_number IS NOT NULL AND episode_number IS NOT NULL
      AND (season_number < ? OR (season_number = ? AND episode_number < ?))
    ORDER BY season_number DESC, episode_number DESC,
             CASE WHEN series_id = ? THEN 0 ELSE 1 END ASC
    LIMIT 1
  `;

  const next = d.prepare(nextSql).get(scopeVal, cur.season_number, cur.season_number, cur.episode_number, cur.series_id) as ItemRow | undefined;
  const prev = d.prepare(prevSql).get(scopeVal, cur.season_number, cur.season_number, cur.episode_number, cur.series_id) as ItemRow | undefined;

  return { prev: prev ?? null, next: next ?? null };
}

export function getDedupStats(): { groupCount: number; mergedItems: number } {
  const d = getDb();
  const row = d.prepare(`
    SELECT
      (SELECT COUNT(*) FROM dedup_groups) as groupCount,
      (SELECT COUNT(*) FROM items WHERE dedup_group_id IS NOT NULL AND dedup_group_id != '' AND type IN ('Movie', 'Series')) as mergedItems
  `).get() as { groupCount: number; mergedItems: number };
  return row;
}

/** Get movies/series matching by tmdb_id across multiple libraries */
export function findTmdbDuplicates(type: string): { tmdb_id: string; item_ids: string[]; names: string[]; years: (number | null)[] }[] {
  const rows = getDb().prepare(`
    SELECT tmdb_id,
           GROUP_CONCAT(COALESCE(emby_id, ''), '|') as item_ids,
           GROUP_CONCAT(COALESCE(name, ''), '|') as names,
           GROUP_CONCAT(COALESCE(CAST(production_year AS TEXT), ''), '|') as years,
           COUNT(DISTINCT library_id) as lib_count
    FROM items
    WHERE type = ? AND tmdb_id IS NOT NULL AND tmdb_id != ''
    GROUP BY tmdb_id
    HAVING lib_count > 1
  `).all(type) as { tmdb_id: string; item_ids: string | null; names: string | null; years: string | null; lib_count: number }[];

  return rows
    .map((r) => {
      const ids = (r.item_ids || '').split('|').filter(Boolean);
      if (ids.length < 2) return null;
      return {
        tmdb_id: r.tmdb_id,
        item_ids: ids,
        names: (r.names || '').split('|'),
        years: (r.years || '').split('|').map((y) => (y ? Number(y) : null)),
      };
    })
    .filter((r): r is NonNullable<typeof r> => r !== null);
}

/** Get movies/series matching by imdb_id across multiple libraries, excluding already-grouped items */
export function findImdbDuplicates(type: string, excludeIds: Set<string>): { imdb_id: string; item_ids: string[] }[] {
  const rows = getDb().prepare(`
    SELECT imdb_id,
           GROUP_CONCAT(COALESCE(emby_id, ''), '|') as item_ids,
           COUNT(DISTINCT library_id) as lib_count
    FROM items
    WHERE type = ? AND imdb_id IS NOT NULL AND imdb_id != ''
      AND (dedup_group_id IS NULL OR dedup_group_id = '')
    GROUP BY imdb_id
    HAVING lib_count > 1
  `).all(type) as { imdb_id: string; item_ids: string | null; lib_count: number }[];

  return rows
    .map((r) => ({
      imdb_id: r.imdb_id,
      item_ids: (r.item_ids || '').split('|').filter((id) => id && !excludeIds.has(id)),
    }))
    .filter((r) => r.item_ids.length > 1);
}

/** Get movies/series matching by normalized name + year, excluding already-grouped items */
export function findNameYearDuplicates(type: string): { name: string; year: number; item_ids: string[] }[] {
  const rows = getDb().prepare(`
    SELECT LOWER(TRIM(name)) as norm_name, production_year,
           GROUP_CONCAT(COALESCE(emby_id, ''), '|') as item_ids,
           COUNT(DISTINCT library_id) as lib_count
    FROM items
    WHERE type = ? AND production_year IS NOT NULL
      AND (dedup_group_id IS NULL OR dedup_group_id = '')
      AND (tmdb_id IS NULL OR tmdb_id = '')
      AND (imdb_id IS NULL OR imdb_id = '')
    GROUP BY norm_name, production_year
    HAVING lib_count > 1
  `).all(type) as { norm_name: string; production_year: number; item_ids: string | null }[];

  return rows
    .map((r) => {
      const ids = (r.item_ids || '').split('|').filter(Boolean);
      if (ids.length < 2) return null;
      return {
        name: r.norm_name,
        year: r.production_year,
        item_ids: ids,
      };
    })
    .filter((r): r is NonNullable<typeof r> => r !== null);
}

/** Deduped items query: one item per dedup group (primary) + ungrouped items */
export function getItemsMultiLibraryDeduped(
  libraryIds: string[],
  opts: {
    startIndex?: number;
    limit?: number;
    sortBy?: string;
    sortOrder?: string;
    itemType?: string;
  } = {},
): { items: ItemRow[]; total: number } {
  const d = getDb();
  const placeholders = libraryIds.map(() => '?').join(',');

  // Parameterize itemType to prevent SQL injection
  const useSpecificType = !!opts.itemType;
  const typeFilter = useSpecificType ? 'AND i.type = ?' : "AND i.type IN ('Movie', 'Series')";
  const typeParams = useSpecificType ? [opts.itemType] : [];

  const mappedCol = EMBY_SORT_MAP[opts.sortBy || 'DateCreated'] || 'date_created';
  const { safeSortBy: sortCol, safeSortOrder: sortDir } = sanitizeSort(mappedCol, opts.sortOrder || 'DESC');
  const limit = opts.limit || 40;
  const offset = opts.startIndex || 0;

  // Count: primary items from dedup groups + ungrouped items
  const countSql = `
    SELECT COUNT(*) as count FROM (
      SELECT i.emby_id FROM items i
      INNER JOIN dedup_groups dg ON i.dedup_group_id = dg.group_id AND i.emby_id = dg.primary_item_id
      WHERE i.library_id IN (${placeholders}) ${typeFilter}
      UNION ALL
      SELECT i.emby_id FROM items i
      WHERE i.library_id IN (${placeholders}) ${typeFilter}
      AND (i.dedup_group_id IS NULL OR i.dedup_group_id = '')
    )
  `;
  const total = (d.prepare(countSql).get(...libraryIds, ...typeParams, ...libraryIds, ...typeParams) as { count: number }).count;

  // Items: same union with sort + pagination
  const itemsSql = `
    SELECT * FROM (
      SELECT i.* FROM items i
      INNER JOIN dedup_groups dg ON i.dedup_group_id = dg.group_id AND i.emby_id = dg.primary_item_id
      WHERE i.library_id IN (${placeholders}) ${typeFilter}
      UNION ALL
      SELECT i.* FROM items i
      WHERE i.library_id IN (${placeholders}) ${typeFilter}
      AND (i.dedup_group_id IS NULL OR i.dedup_group_id = '')
    ) ORDER BY ${sortCol} ${sortDir} LIMIT ? OFFSET ?
  `;
  const items = d.prepare(itemsSql).all(...libraryIds, ...typeParams, ...libraryIds, ...typeParams, limit, offset) as ItemRow[];

  return { items, total };
}

/** Deduped latest items for home page rows */
export function getLatestMultiLibraryDeduped(libraryIds: string[], limit = 20): ItemRow[] {
  const d = getDb();
  const placeholders = libraryIds.map(() => '?').join(',');

  return d.prepare(`
    SELECT * FROM (
      SELECT i.* FROM items i
      INNER JOIN dedup_groups dg ON i.dedup_group_id = dg.group_id AND i.emby_id = dg.primary_item_id
      WHERE i.library_id IN (${placeholders}) AND i.type IN ('Movie', 'Series')
      UNION ALL
      SELECT i.* FROM items i
      WHERE i.library_id IN (${placeholders}) AND i.type IN ('Movie', 'Series')
      AND (i.dedup_group_id IS NULL OR i.dedup_group_id = '')
    ) ORDER BY date_created DESC LIMIT ?
  `).all(...libraryIds, ...libraryIds, limit) as ItemRow[];
}

/** Deduped search: returns items with version count */
export function searchItemsDeduped(query: string, limit = 24): (ItemRow & { version_count: number })[] {
  const d = getDb();
  // Get matching items (bounded to prevent memory exhaustion on broad queries)
  const MAX_SEARCH_ROWS = 500;
  const all = d.prepare(`
    SELECT * FROM items
    WHERE name LIKE ? ESCAPE '\\' AND type IN ('Movie', 'Series', 'Episode')
    ORDER BY name ASC LIMIT ?
  `).all(`%${escapeLike(query)}%`, MAX_SEARCH_ROWS) as ItemRow[];

  // Deduplicate: group by dedup_group_id, keep primary
  const seen = new Set<string>();
  const results: (ItemRow & { version_count: number })[] = [];

  // First pass: count versions per group. Only Movie/Series contribute — after
  // Phase A backfill Episodes inherit their parent Series' dedup_group_id, so
  // counting them would inflate version_count (e.g., 40 episodes + 2 series = 42).
  const groupCounts = new Map<string, number>();
  for (const item of all) {
    if (item.dedup_group_id && (item.type === 'Movie' || item.type === 'Series')) {
      groupCounts.set(item.dedup_group_id, (groupCounts.get(item.dedup_group_id) || 0) + 1);
    }
  }

  // Get primary items for dedup groups
  const primaryMap = new Map<string, string>();
  if (groupCounts.size > 0) {
    const groupIds = Array.from(groupCounts.keys());
    const ph = groupIds.map(() => '?').join(',');
    const primaries = d.prepare(`SELECT group_id, primary_item_id FROM dedup_groups WHERE group_id IN (${ph})`).all(...groupIds) as { group_id: string; primary_item_id: string }[];
    for (const p of primaries) primaryMap.set(p.group_id, p.primary_item_id);
  }

  for (const item of all) {
    if (item.dedup_group_id) {
      if (seen.has(item.dedup_group_id)) continue;
      seen.add(item.dedup_group_id);
      // Use primary item if available and it's in our results
      const primaryId = primaryMap.get(item.dedup_group_id);
      const primaryItem = primaryId ? all.find((i) => i.emby_id === primaryId) : null;
      const display = primaryItem || item;
      results.push({ ...display, version_count: groupCounts.get(item.dedup_group_id) || 1 });
    } else {
      results.push({ ...item, version_count: 1 });
    }
    if (results.length >= limit) break;
  }

  return results;
}

/**
 * Backfill episodes' dedup_group_id from their parent series' group. Chunks
 * the UPDATE by series_id (default 500 per chunk) and yields to the event loop
 * between chunks so the main thread stays responsive on large libraries.
 * Each chunk is atomic on its own; partial progress survives a crash.
 */
export async function backfillEpisodeDedupGroups(chunkSize = 500): Promise<number> {
  const d = getDb();
  const seriesRows = d.prepare(
    "SELECT emby_id FROM items WHERE type = 'Series' AND dedup_group_id IS NOT NULL AND dedup_group_id != ''"
  ).all() as { emby_id: string }[];
  if (seriesRows.length === 0) return 0;

  let totalChanges = 0;
  for (let i = 0; i < seriesRows.length; i += chunkSize) {
    const chunk = seriesRows.slice(i, i + chunkSize).map((s) => s.emby_id);
    const placeholders = chunk.map(() => '?').join(',');
    const res = d.prepare(`
      UPDATE items
      SET dedup_group_id = (
        SELECT s.dedup_group_id FROM items AS s
        WHERE s.emby_id = items.series_id AND s.type = 'Series'
      )
      WHERE items.type = 'Episode' AND items.series_id IN (${placeholders})
    `).run(...chunk);
    totalChanges += res.changes;
    await new Promise((r) => setImmediate(r));
  }
  return totalChanges;
}

/** Dissolve dedup groups that have shrunk to 1 item */
export function dissolveSingletonGroups(): void {
  const d = getDb();
  const singletons = d.prepare(`
    SELECT dg.group_id FROM dedup_groups dg
    WHERE (SELECT COUNT(*) FROM items WHERE dedup_group_id = dg.group_id) <= 1
  `).all() as { group_id: string }[];

  if (singletons.length === 0) return;

  const tx = d.transaction(() => {
    for (const s of singletons) {
      d.prepare("UPDATE items SET dedup_group_id = NULL WHERE dedup_group_id = ?").run(s.group_id);
      d.prepare("DELETE FROM dedup_groups WHERE group_id = ?").run(s.group_id);
    }
  });
  tx();
}

// ── Cleanup ─────────────────────────────────────────────

export function clearLibrary(libraryId: string): void {
  getDb().prepare('DELETE FROM items WHERE library_id = ?').run(libraryId);
}

/** Clear items and dedup data but preserve sync_state (lastFullSync, libraries, etc.) */
export function clearItemsAndDedup(): void {
  const d = getDb();
  d.exec('DELETE FROM items');
  d.exec('DELETE FROM dedup_groups');
  d.exec('DELETE FROM dedup_episode_groups');
}

/** Delete all `episodes_synced_{seriesId}` markers so a fresh sync re-fetches episodes for every series. */
export function clearEpisodeSyncMarkers(): number {
  const d = getDb();
  const res = d.prepare("DELETE FROM sync_state WHERE key LIKE 'episodes_synced_%'").run();
  return res.changes;
}

/** Full reset: clear everything including sync_state */
export function clearAll(): void {
  const d = getDb();
  d.exec('DELETE FROM items');
  d.exec('DELETE FROM sync_state');
  d.exec('DELETE FROM dedup_groups');
  d.exec('DELETE FROM dedup_episode_groups');
}

export function clearServerData(serverId: string): void {
  const d = getDb();
  d.prepare('DELETE FROM items WHERE server_id = ?').run(serverId);
  // Clean up orphaned dedup groups
  d.exec(`DELETE FROM dedup_groups WHERE group_id NOT IN (SELECT DISTINCT dedup_group_id FROM items WHERE dedup_group_id IS NOT NULL)`);
}

export function hasAnyCachedItems(): boolean {
  const row = getDb().prepare('SELECT COUNT(*) as count FROM items').get() as { count: number };
  return row.count > 0;
}

export function checkpoint(): void {
  getDb().pragma('wal_checkpoint(PASSIVE)');
}

/** Hand the shared db handle to the analytics module. Lives here so the
 *  analytics module can avoid a top-level circular import on `db`. */
export function getDbForAnalytics(): Database.Database {
  return getDb();
}

// ── Trakt scrobble queue ────────────────────────────

export interface TraktQueueRow {
  id: number;
  action: string;
  payload: string;
  emby_id: string | null;
  attempts: number;
  next_retry_at: string | null;
  last_error: string | null;
  created_at: string;
}

export function enqueueTraktScrobble(
  action: string,
  payload: string,
  embyId: string | null,
  lastError: string | null,
): void {
  getDb()
    .prepare(
      `INSERT INTO trakt_scrobble_queue (action, payload, emby_id, attempts, last_error)
       VALUES (?, ?, ?, 0, ?)`,
    )
    .run(action, payload, embyId, lastError);
}

/** Get the next queue entry by FIFO (lowest id), regardless of retry timing. */
export function getNextTraktScrobble(): TraktQueueRow | undefined {
  return getDb()
    .prepare(`SELECT * FROM trakt_scrobble_queue ORDER BY id ASC LIMIT 1`)
    .get() as TraktQueueRow | undefined;
}

export function markTraktScrobbleAttempt(
  id: number,
  nextRetryAt: string | null,
  lastError: string | null,
): void {
  getDb()
    .prepare(
      `UPDATE trakt_scrobble_queue
       SET attempts = attempts + 1, next_retry_at = ?, last_error = ?
       WHERE id = ?`,
    )
    .run(nextRetryAt, lastError, id);
}

export function deleteTraktScrobble(id: number): void {
  getDb().prepare(`DELETE FROM trakt_scrobble_queue WHERE id = ?`).run(id);
}

export function clearTraktQueue(): void {
  getDb().exec(`DELETE FROM trakt_scrobble_queue`);
}

/**
 * Drop queued scrobble events that will never succeed in their stored form:
 *   - stop/pause with progress < 1.0   → 422 "progress >= 1.0% required"
 *   - pause with progress >= 80        → 422 "Use stop to scrobble"
 * Both classes retry until MAX_ATTEMPTS otherwise. One-shot boot cleanup.
 * Returns counts per bucket so the caller can log a useful summary.
 */
export function pruneStaleTraktQueue(): { lowProgress: number; pauseHighProgress: number } {
  const d = getDb();
  const rows = d
    .prepare(`SELECT id, action, payload FROM trakt_scrobble_queue WHERE action IN ('stop', 'pause')`)
    .all() as Array<{ id: number; action: string; payload: string }>;
  if (rows.length === 0) return { lowProgress: 0, pauseHighProgress: 0 };
  const del = d.prepare(`DELETE FROM trakt_scrobble_queue WHERE id = ?`);
  let lowProgress = 0;
  let pauseHighProgress = 0;
  const tx = d.transaction(() => {
    for (const row of rows) {
      let progress = 0;
      try {
        const p = JSON.parse(row.payload) as { progress?: unknown };
        if (typeof p.progress === 'number') progress = p.progress;
      } catch {
        // malformed entries are dropped by drainQueue on next pass — leave alone here
        continue;
      }
      if (progress < 1) {
        del.run(row.id);
        lowProgress++;
      } else if (row.action === 'pause' && progress >= 80) {
        del.run(row.id);
        pauseHighProgress++;
      }
    }
  });
  tx();
  return { lowProgress, pauseHighProgress };
}

export function countTraktQueue(): number {
  const row = getDb()
    .prepare(`SELECT COUNT(*) as c FROM trakt_scrobble_queue`)
    .get() as { c: number };
  return row.c;
}

// ── Trakt watched history (mirror of /sync/watched) ───

export interface TraktWatchedRow {
  key: string;
  trakt_type: 'movie' | 'episode';
  tmdb_id: string | null;
  imdb_id: string | null;
  show_tmdb_id: string | null;
  season_number: number | null;
  episode_number: number | null;
  watched_at: string;
}

export function bulkUpsertTraktWatched(rows: TraktWatchedRow[]): void {
  if (rows.length === 0) return;
  const d = getDb();
  const stmt = d.prepare(`
    INSERT INTO trakt_watched_history
      (key, trakt_type, tmdb_id, imdb_id, show_tmdb_id, season_number, episode_number, watched_at, synced_at)
    VALUES
      (@key, @trakt_type, @tmdb_id, @imdb_id, @show_tmdb_id, @season_number, @episode_number, @watched_at, datetime('now'))
    ON CONFLICT(key) DO UPDATE SET
      watched_at = excluded.watched_at,
      synced_at  = datetime('now')
  `);
  const tx = d.transaction((batch: TraktWatchedRow[]) => {
    for (const r of batch) stmt.run(r);
  });
  tx(rows);
}

export function clearTraktWatched(): void {
  getDb().exec(`DELETE FROM trakt_watched_history`);
}

export function countTraktWatched(): { movies: number; episodes: number } {
  const d = getDb();
  const m = d.prepare(`SELECT COUNT(*) as c FROM trakt_watched_history WHERE trakt_type = 'movie'`).get() as { c: number };
  const e = d.prepare(`SELECT COUNT(*) as c FROM trakt_watched_history WHERE trakt_type = 'episode'`).get() as { c: number };
  return { movies: m.c, episodes: e.c };
}

export function isMovieWatchedOnTrakt(tmdbId: string): boolean {
  const row = getDb()
    .prepare(`SELECT 1 FROM trakt_watched_history WHERE trakt_type = 'movie' AND tmdb_id = ? LIMIT 1`)
    .get(tmdbId);
  return Boolean(row);
}

export function isEpisodeWatchedOnTrakt(showTmdbId: string, season: number, episode: number): boolean {
  const row = getDb()
    .prepare(
      `SELECT 1 FROM trakt_watched_history
       WHERE trakt_type = 'episode' AND show_tmdb_id = ? AND season_number = ? AND episode_number = ?
       LIMIT 1`,
    )
    .get(showTmdbId, season, episode);
  return Boolean(row);
}

// ── Trakt watchlist (mirror of /users/me/watchlist) ───

export interface TraktWatchlistRow {
  key: string;
  trakt_type: 'movie' | 'show';
  tmdb_id: string | null;
  imdb_id: string | null;
  trakt_id: number | null;
  title: string | null;
  year: number | null;
  overview: string | null;
  added_at: string | null;
}

export function bulkUpsertTraktWatchlist(rows: TraktWatchlistRow[]): void {
  if (rows.length === 0) return;
  const d = getDb();
  const stmt = d.prepare(`
    INSERT INTO trakt_watchlist
      (key, trakt_type, tmdb_id, imdb_id, trakt_id, title, year, overview, added_at, synced_at)
    VALUES
      (@key, @trakt_type, @tmdb_id, @imdb_id, @trakt_id, @title, @year, @overview, @added_at, datetime('now'))
    ON CONFLICT(key) DO UPDATE SET
      title = excluded.title,
      year = excluded.year,
      overview = excluded.overview,
      added_at = excluded.added_at,
      synced_at = datetime('now')
  `);
  const tx = d.transaction((batch: TraktWatchlistRow[]) => {
    for (const r of batch) stmt.run(r);
  });
  tx(rows);
}

export function replaceTraktWatchlist(rows: TraktWatchlistRow[]): void {
  const d = getDb();
  const tx = d.transaction(() => {
    d.exec(`DELETE FROM trakt_watchlist`);
    if (rows.length === 0) return;
    const stmt = d.prepare(`
      INSERT INTO trakt_watchlist
        (key, trakt_type, tmdb_id, imdb_id, trakt_id, title, year, overview, added_at, synced_at)
      VALUES
        (@key, @trakt_type, @tmdb_id, @imdb_id, @trakt_id, @title, @year, @overview, @added_at, datetime('now'))
    `);
    for (const r of rows) stmt.run(r);
  });
  tx();
  onWatchlistMutated?.();
}

export function clearTraktWatchlist(): void {
  getDb().exec(`DELETE FROM trakt_watchlist`);
}

export function countTraktWatchlist(): number {
  const row = getDb().prepare(`SELECT COUNT(*) as c FROM trakt_watchlist`).get() as { c: number };
  return row.c;
}

export function getTraktWatchlistEntries(): TraktWatchlistRow[] {
  return getDb()
    .prepare(`SELECT key, trakt_type, tmdb_id, imdb_id, trakt_id, title, year, overview, added_at FROM trakt_watchlist ORDER BY added_at DESC`)
    .all() as TraktWatchlistRow[];
}

export function isInTraktWatchlist(traktType: 'movie' | 'show', tmdbId: string): boolean {
  const row = getDb()
    .prepare(`SELECT 1 FROM trakt_watchlist WHERE trakt_type = ? AND tmdb_id = ? LIMIT 1`)
    .get(traktType, tmdbId);
  return Boolean(row);
}

export function deleteTraktWatchlistEntry(key: string): void {
  getDb().prepare(`DELETE FROM trakt_watchlist WHERE key = ?`).run(key);
  onWatchlistMutated?.();
}

// ── Trakt rating cache (24h TTL) ─────────────────────

export interface TraktRatingRow {
  tmdb_id: string;
  trakt_type: 'movie' | 'show';
  rating: number | null;
  votes: number | null;
  fetched_at: string;
}

export function getCachedTraktRating(tmdbId: string, type: 'movie' | 'show', ttlMs: number): TraktRatingRow | undefined {
  const row = getDb()
    .prepare(`SELECT * FROM trakt_ratings WHERE tmdb_id = ? AND trakt_type = ?`)
    .get(tmdbId, type) as TraktRatingRow | undefined;
  if (!row) return undefined;
  const ageMs = Date.now() - new Date(row.fetched_at).getTime();
  if (ageMs > ttlMs) return undefined;
  return row;
}

export function setCachedTraktRating(tmdbId: string, type: 'movie' | 'show', rating: number | null, votes: number | null): void {
  getDb()
    .prepare(`
      INSERT INTO trakt_ratings (tmdb_id, trakt_type, rating, votes, fetched_at)
      VALUES (?, ?, ?, ?, datetime('now'))
      ON CONFLICT(tmdb_id, trakt_type) DO UPDATE SET
        rating = excluded.rating,
        votes = excluded.votes,
        fetched_at = excluded.fetched_at
    `)
    .run(tmdbId, type, rating, votes);
}

export function clearTraktRatings(): void {
  getDb().exec(`DELETE FROM trakt_ratings`);
}

// ── Lookup helpers used by Trakt sync engine ─────────

/**
 * Resolve a dedup_group_id to its primary item row. Used by the Trakt
 * watchlist match path so the rendered card matches the same primary the
 * library grid shows (preserving image_tags + the right server_id), instead
 * of picking an arbitrary group member that may have empty image metadata.
 */
export function getDedupPrimaryItem(groupId: string): ItemRow | undefined {
  const d = getDb();
  const row = d.prepare(
    `SELECT i.* FROM items i
     INNER JOIN dedup_groups dg ON i.emby_id = dg.primary_item_id
     WHERE dg.group_id = ?`,
  ).get(groupId) as ItemRow | undefined;
  return row;
}

/** All cached items matching a TMDB id (across servers). */
export function findItemsByTmdbId(tmdbId: string, type?: 'Movie' | 'Series'): ItemRow[] {
  const d = getDb();
  if (type) {
    return d.prepare(`SELECT * FROM items WHERE tmdb_id = ? AND type = ?`).all(tmdbId, type) as ItemRow[];
  }
  return d.prepare(`SELECT * FROM items WHERE tmdb_id = ?`).all(tmdbId) as ItemRow[];
}

export function findItemsByImdbId(imdbId: string, type?: 'Movie' | 'Series'): ItemRow[] {
  const d = getDb();
  if (type) {
    return d.prepare(`SELECT * FROM items WHERE imdb_id = ? AND type = ?`).all(imdbId, type) as ItemRow[];
  }
  return d.prepare(`SELECT * FROM items WHERE imdb_id = ?`).all(imdbId) as ItemRow[];
}

/** All cached episode rows for a given (showTmdbId, season, episode), across servers/series copies. */
export function findEpisodesByShowTmdb(showTmdbId: string, season: number, episode: number): ItemRow[] {
  return getDb()
    .prepare(
      `SELECT e.* FROM items e
       INNER JOIN items s ON e.series_id = s.emby_id
       WHERE s.type = 'Series' AND s.tmdb_id = ?
         AND e.type = 'Episode'
         AND e.season_number = ?
         AND e.episode_number = ?`,
    )
    .all(showTmdbId, season, episode) as ItemRow[];
}

/**
 * Bulk lookup for items by external IDs. Returns the union of rows matching
 * any tmdb_id or imdb_id in the input lists, deduplicated by emby_id. SQLite
 * has a default 999-parameter limit, so we chunk. Used by Trakt sync to
 * collapse thousands of per-item lookups into a few flat IN queries.
 */
export function bulkFindItemsByExternalIds(
  tmdbIds: string[],
  imdbIds: string[],
  type: 'Movie' | 'Series',
): ItemRow[] {
  const d = getDb();
  const CHUNK = 500;
  const out: ItemRow[] = [];
  const seen = new Set<string>();

  const append = (rows: ItemRow[]) => {
    for (const r of rows) {
      if (seen.has(r.emby_id)) continue;
      seen.add(r.emby_id);
      out.push(r);
    }
  };

  for (let i = 0; i < tmdbIds.length; i += CHUNK) {
    const chunk = tmdbIds.slice(i, i + CHUNK);
    const placeholders = chunk.map(() => '?').join(',');
    append(
      d.prepare(
        `SELECT * FROM items WHERE type = ? AND tmdb_id IN (${placeholders})`,
      ).all(type, ...chunk) as ItemRow[],
    );
  }
  for (let i = 0; i < imdbIds.length; i += CHUNK) {
    const chunk = imdbIds.slice(i, i + CHUNK);
    const placeholders = chunk.map(() => '?').join(',');
    append(
      d.prepare(
        `SELECT * FROM items WHERE type = ? AND imdb_id IN (${placeholders})`,
      ).all(type, ...chunk) as ItemRow[],
    );
  }
  return out;
}

/** Bulk lookup: every Episode row whose series_id is in the given list. Chunked for SQLite param limit. */
export function bulkFindEpisodesBySeriesIds(seriesIds: string[]): ItemRow[] {
  if (seriesIds.length === 0) return [];
  const d = getDb();
  const CHUNK = 500;
  const out: ItemRow[] = [];
  for (let i = 0; i < seriesIds.length; i += CHUNK) {
    const chunk = seriesIds.slice(i, i + CHUNK);
    const placeholders = chunk.map(() => '?').join(',');
    const rows = d.prepare(
      `SELECT * FROM items WHERE type = 'Episode' AND series_id IN (${placeholders})`,
    ).all(...chunk) as ItemRow[];
    out.push(...rows);
  }
  return out;
}

/** Bulk fetch items by emby_id list. Used by Trakt apply-watched to load all
 *  selected rows in 1-N flat queries instead of N getItem() round trips. */
export function bulkGetItemsByEmbyIds(embyIds: string[]): ItemRow[] {
  if (embyIds.length === 0) return [];
  const d = getDb();
  const CHUNK = 500;
  const out: ItemRow[] = [];
  for (let i = 0; i < embyIds.length; i += CHUNK) {
    const chunk = embyIds.slice(i, i + CHUNK);
    const placeholders = chunk.map(() => '?').join(',');
    const rows = d.prepare(
      `SELECT * FROM items WHERE emby_id IN (${placeholders})`,
    ).all(...chunk) as ItemRow[];
    out.push(...rows);
  }
  return out;
}

/**
 * Last-resort matcher for Trakt → local lookups when an item lacks both
 * tmdb_id and imdb_id (Emby metadata gap), or the catalog id mismatches.
 *
 * Two-pass strategy:
 *   1. Strict — exact lowercased+trimmed name with production_year ±1
 *      (Trakt and Emby occasionally disagree by one on release year).
 *   2. Normalized — strip leading articles, punctuation, and collapse
 *      whitespace, then compare. This catches title variants like
 *      "The Hoppers" vs "Hoppers" or "Spider-Man" vs "Spider Man".
 * Used by the watchlist sidebar; not by primary mark-played pathways.
 */
export function findItemsByNameAndYear(
  name: string,
  year: number,
  type: 'Movie' | 'Series',
): ItemRow[] {
  const d = getDb();
  const lowered = name.trim().toLowerCase();
  // Pass 1: exact match with ±1 year tolerance.
  const strict = d.prepare(
    `SELECT * FROM items
     WHERE type = ?
       AND production_year BETWEEN ? AND ?
       AND LOWER(TRIM(name)) = ?`,
  ).all(type, year - 1, year + 1, lowered) as ItemRow[];
  if (strict.length > 0) return strict;

  // Pass 2: normalized match (strip articles + punctuation) on year ±1.
  const target = normalizeTitleForMatch(name);
  if (!target) return [];
  const candidates = d.prepare(
    `SELECT * FROM items
     WHERE type = ? AND production_year BETWEEN ? AND ?`,
  ).all(type, year - 1, year + 1) as ItemRow[];
  return candidates.filter((row) => normalizeTitleForMatch(row.name) === target);
}

/** Lowercase, drop leading article ("the"/"a"/"an"), strip non-alphanumeric. */
function normalizeTitleForMatch(s: string | null | undefined): string {
  if (!s) return '';
  return s
    .toLowerCase()
    .replace(/^\s*(the|a|an)\s+/, '')
    .replace(/[^a-z0-9]+/g, '');
}

/** Mark a list of items played in one transaction (clears resume position).
 *  Used by Trakt apply-watched and background history sync. Does NOT touch
 *  Emby — caller pushes those separately, optionally with concurrency. */
export function bulkMarkItemsPlayed(embyIds: string[]): void {
  if (embyIds.length === 0) return;
  const d = getDb();
  const stmt = d.prepare(`
    UPDATE items
    SET played = 1, playback_position_ticks = 0, played_percentage = 0
    WHERE emby_id = ?
  `);
  const tx = d.transaction((ids: string[]) => {
    for (const id of ids) stmt.run(id);
  });
  tx(embyIds);
}
