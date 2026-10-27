using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class DatabaseService : IDisposable
{
    public const string DatabaseFileName = "nocturne-v4.db";
    private const string SharedLegacyFileName = "nocturne.db";

    // E2: every literal statement the methods below execute is registered at type initialization (each
    // "private static readonly string Sql... = Checked(...)" field), and SelfCheckPreparedStatements prepares
    // exactly this list. The earlier hand-kept list had drifted: 11 of its 23 strings were not what the code ran.
    private static readonly List<string> CheckedStatements = new();
    private static string Checked(string sql) { CheckedStatements.Add(sql); return sql; }
    internal static IReadOnlyList<string> RegisteredStatements => CheckedStatements;

    // E5: used as an exception filter on the read paths that fall back to null / empty. The failure is logged
    // with the statement; in Debug builds the filter returns false so the exception propagates (tests and
    // development see it instead of an empty result), in Release the fallback still applies.
    private static bool QueryFailed(Exception ex, string what, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        NocturneLogger.Error("DatabaseService", $"{method} failed: {string.Join(' ', what.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}", ex);
#if DEBUG
        return false;
#else
        return true;
#endif
    }

    private readonly object _dbLock = new();
    private readonly string _connectionString;
    private readonly string _dbPath;
    public string DbPath => _dbPath; // for readers on their own connection (PartitionOracle)
    public bool IsClosed { get { lock (_dbLock) return _connection == null; } } // after Dispose (app exit)
    private SqliteConnection? _connection;

    public DatabaseService() : this(ResolveDefaultPath(AppPaths.DataDirectory))
    {
    }

    // v3.5 (Electron) and v4 shared %APPDATA%\Nocturne\nocturne.db, and v4 dropped v3.5's tables on open. v4 now
    // has its own file. A shared file holding v4's schema (dedup_keys) is v4's: its WAL is checkpointed so the
    // main file is complete, then it is renamed. A v3.5 file is left alone. If the move can't be done safely
    // (another connection holds it, the checkpoint is blocked, the rename fails) v4 uses the shared file in place.
    public static string ResolveDefaultPath(string dir)
    {
        var path = Path.Combine(dir, DatabaseFileName);
        var legacy = Path.Combine(dir, SharedLegacyFileName);
        if (File.Exists(path) || !File.Exists(legacy)) return path;
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = legacy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
            using (var conn = new SqliteConnection(cs))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'dedup_keys';";
                if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return path;
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var r = cmd.ExecuteReader();
                if (!r.Read() || r.GetInt64(0) != 0) throw new IOException("the WAL checkpoint was blocked by another connection");
            }
            if (File.Exists(legacy + "-wal") && new FileInfo(legacy + "-wal").Length > 0)
                throw new IOException("the WAL is still in use");
            File.Move(legacy, path);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                try { File.Delete(legacy + suffix); } catch { /* empty side file; SQLite recreates it */ }
            }
            NocturneLogger.Info("DatabaseService", $"Moved the v4 database from {legacy} to {path}.");
            return path;
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("DatabaseService", $"Could not move the v4 database out of the shared {legacy} ({ex.Message}); using it in place this run.");
            return legacy;
        }
    }

    public DatabaseService(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // Private cache (the default): with WAL and one writer, a shared cache bought nothing, and its table locks made
        // the rebuild's swap block every reader on the cache (#21 item 3).
        // Unpooled (#22 item 5): _connection stays open for the app's life and the other connections are rare long scans,
        // so a pool bought nothing, and a pooled Close() leaves the handle open: SQLite deletes the WAL only at the last
        // real close, so it outlived every run at its high-water mark.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();

        Initialize();
    }

    private void Initialize()
    {
        try
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA cache_size = -262144;
                PRAGMA journal_size_limit = 67108864; -- #22 item 5: the WAL file shrinks back to 64 MB when it resets

                CREATE TABLE IF NOT EXISTS cached_items (
                    id TEXT NOT NULL,
                    server_id TEXT NOT NULL,
                    parent_id TEXT,
                    group_id INTEGER,
                    name TEXT NOT NULL,
                    type TEXT NOT NULL,
                    run_time_ticks INTEGER,
                    production_year INTEGER,
                    community_rating REAL,
                    index_number INTEGER,
                    parent_index_number INTEGER,
                    series_id TEXT,
                    series_name TEXT,
                    season_id TEXT,
                    season_name TEXT,
                    genres TEXT,
                    tmdb_id TEXT,
                    imdb_id TEXT,
                    dedup_group_id TEXT,
                    height INTEGER,
                    width INTEGER,
                    video_codec TEXT,
                    audio_codec TEXT,
                    bitrate INTEGER,
                    size_bytes INTEGER,
                    runtime_seconds INTEGER,
                    hdr_type TEXT,
                    edition TEXT,
                    json_data TEXT NOT NULL,
                    last_synced INTEGER NOT NULL,
                    PRIMARY KEY (id, server_id)
                );

                CREATE TABLE IF NOT EXISTS sync_checkpoints (
                    server_id TEXT NOT NULL,
                    library_id TEXT NOT NULL,
                    start_index INTEGER NOT NULL,
                    total_items INTEGER NOT NULL,
                    is_completed INTEGER NOT NULL DEFAULT 0,
                    last_synced INTEGER NOT NULL,
                    PRIMARY KEY (server_id, library_id)
                );

                CREATE TABLE IF NOT EXISTS watch_events (
                    id TEXT PRIMARY KEY,
                    item_id TEXT NOT NULL,
                    server_id TEXT NOT NULL,
                    item_name TEXT NOT NULL,
                    series_name TEXT,
                    item_type TEXT NOT NULL,
                    duration_seconds INTEGER NOT NULL,
                    watched_at INTEGER NOT NULL,
                    source TEXT NOT NULL,
                    genres TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_watch_events_time ON watch_events(watched_at);
                CREATE INDEX IF NOT EXISTS idx_watch_events_source ON watch_events(source);

                CREATE TABLE IF NOT EXISTS trakt_watchlist (
                    id TEXT PRIMARY KEY,
                    trakt_id INTEGER,
                    tmdb_id TEXT,
                    imdb_id TEXT,
                    title TEXT NOT NULL,
                    year INTEGER,
                    type TEXT NOT NULL,
                    added_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS trakt_watched_history (
                    key TEXT PRIMARY KEY,
                    trakt_type TEXT NOT NULL,
                    tmdb_id TEXT,
                    imdb_id TEXT,
                    show_tmdb_id TEXT,
                    season_number INTEGER,
                    episode_number INTEGER,
                    watched_at INTEGER NOT NULL,
                    synced_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS continue_watching_cache (
                    id TEXT NOT NULL,
                    server_id TEXT NOT NULL,
                    sort_order INTEGER NOT NULL,
                    json_data TEXT NOT NULL,
                    updated_at INTEGER NOT NULL,
                    PRIMARY KEY (id, server_id)
                );

                CREATE TABLE IF NOT EXISTS media_stream_preferences (
                    media_key TEXT PRIMARY KEY,
                    audio_track_id INTEGER,
                    audio_language TEXT,
                    subtitle_track_id INTEGER,
                    subtitle_language TEXT,
                    updated_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS migration_state (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();

            EnsureSchema(_connection);
            SelfCheckPreparedStatements();

            NocturneLogger.Info("DatabaseService", "Database initialized in WAL mode successfully.");
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("DatabaseService", "Database initialization error", ex);
            throw; // Fail fast per architecture review V5
        }
    }

    private void EnsureSchema(SqliteConnection conn)
    {
        // Check if dedup_groups is legacy TEXT group_id schema
        bool isLegacyDedupGroups = false;
        try
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info(dedup_groups);";
            using var reader = checkCmd.ExecuteReader();
            while (reader.Read())
            {
                var colName = reader.GetString(1);
                var colType = reader.GetString(2);
                if (string.Equals(colName, "group_id", StringComparison.OrdinalIgnoreCase) &&
                    colType.Contains("TEXT", StringComparison.OrdinalIgnoreCase))
                {
                    isLegacyDedupGroups = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (QueryFailed(ex, "PRAGMA table_info(dedup_groups);")) { }

        if (isLegacyDedupGroups)
        {
            using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = "DROP TABLE IF EXISTS dedup_groups; DROP TABLE IF EXISTS dedup_keys;";
            dropCmd.ExecuteNonQuery();
        }

        using var initCmd = conn.CreateCommand();
        initCmd.CommandText = @"
            -- 1. Denormalized groups table (the display entity for shelves/search)
            CREATE TABLE IF NOT EXISTS dedup_groups (
                group_id         INTEGER PRIMARY KEY,
                item_type        TEXT NOT NULL,
                primary_item_id  TEXT NOT NULL,
                primary_server   TEXT NOT NULL,
                is_provisional   INTEGER NOT NULL DEFAULT 0,
                version_count    INTEGER NOT NULL DEFAULT 1,
                name             TEXT NOT NULL,
                sort_name        TEXT,
                year             INTEGER,
                overview         TEXT,
                image_tag        TEXT,
                backdrop_tag     TEXT,
                community_rating REAL,
                date_created     INTEGER,
                series_group_id  INTEGER,
                season_number    INTEGER,
                episode_number   INTEGER,
                max_height       INTEGER,
                has_4k           INTEGER NOT NULL DEFAULT 0,
                max_bitrate      INTEGER,
                audio_formats    TEXT,
                image_item_id    TEXT,
                image_server_id  TEXT,
                backdrop_item_id TEXT,
                backdrop_server_id TEXT,
                tmdb_key         TEXT,
                primary_score    INTEGER NOT NULL DEFAULT 0
            );

            -- 2. Union-Find Alias Table (WITHOUT ROWID)
            CREATE TABLE IF NOT EXISTS dedup_keys (
                key              TEXT PRIMARY KEY,
                group_id         INTEGER NOT NULL,
                is_authoritative INTEGER NOT NULL DEFAULT 1
            ) WITHOUT ROWID;

            -- 3. User Data State (Native Source of Truth, Immune to Group Rebuilds)
            CREATE TABLE IF NOT EXISTS user_data (
                server_id       TEXT NOT NULL,
                item_id         TEXT NOT NULL,
                position_ticks  INTEGER NOT NULL DEFAULT 0,
                runtime_ticks   INTEGER,
                played          INTEGER NOT NULL DEFAULT 0,
                last_played_utc INTEGER,
                origin          TEXT NOT NULL DEFAULT 'local',
                updated_locally INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (server_id, item_id)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_user_data_continue
                ON user_data(last_played_utc DESC)
                WHERE position_ticks > 0 AND played = 0;

            -- 4. Persistent Sibling Write Queue with Unique Upsert
            CREATE TABLE IF NOT EXISTS sibling_write_queue (
                queue_id       INTEGER PRIMARY KEY AUTOINCREMENT,
                server_id      TEXT NOT NULL,
                item_id        TEXT NOT NULL,
                position_ticks INTEGER,
                mark_played    INTEGER NOT NULL DEFAULT 0,
                queued_utc     INTEGER NOT NULL,
                attempts       INTEGER NOT NULL DEFAULT 0,
                UNIQUE(server_id, item_id)
            );

            -- 5. Merged Watchlist (Emby Favorites + Trakt Watchlist, D10)
            CREATE TABLE IF NOT EXISTS watchlist_entries (
                source     TEXT NOT NULL,
                ref        TEXT NOT NULL,
                item_type  TEXT NOT NULL,
                added_utc  INTEGER NOT NULL,
                title      TEXT,
                year       INTEGER,
                tmdb_key   TEXT,
                pending_op TEXT,
                PRIMARY KEY (source, ref)
            ) WITHOUT ROWID;

            -- 6. TMDB Fallback Metadata Cache (D9, D11)
            CREATE TABLE IF NOT EXISTS tmdb_metadata (
                provider_key  TEXT PRIMARY KEY,
                poster_path   TEXT,
                backdrop_path TEXT,
                still_path    TEXT,
                overview      TEXT,
                vote_average  REAL,
                fetched_utc   INTEGER NOT NULL,
                not_found     INTEGER NOT NULL DEFAULT 0
            ) WITHOUT ROWID;

            -- 7. Trakt Scrobble & Sync Queue (Ordered Event Stream, D6, §4.4)
            CREATE TABLE IF NOT EXISTS trakt_queue (
                queue_id   INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id   INTEGER NOT NULL,
                action     TEXT NOT NULL,
                progress   REAL NOT NULL DEFAULT 0,
                payload    TEXT,
                queued_utc INTEGER NOT NULL,
                attempts   INTEGER NOT NULL DEFAULT 0
            );

            -- 8. Integration State (Trakt & TMDB Sync Markers, §4.5)
            CREATE TABLE IF NOT EXISTS integration_state (
                name       TEXT NOT NULL,
                key        TEXT NOT NULL,
                value      TEXT NOT NULL,
                PRIMARY KEY (name, key)
            ) WITHOUT ROWID;
        ";
        initCmd.ExecuteNonQuery();

        // 5. cached_items column migration
        MigrateTableColumns(conn, "cached_items", new Dictionary<string, string>
        {
            { "genres", "TEXT" },
            { "tmdb_id", "TEXT" },
            { "imdb_id", "TEXT" },
            { "dedup_group_id", "TEXT" },
            { "group_id", "INTEGER" },
            { "height", "INTEGER" },
            { "width", "INTEGER" },
            { "video_codec", "TEXT" },
            { "audio_codec", "TEXT" },
            { "bitrate", "INTEGER" },
            { "size_bytes", "INTEGER" },
            { "runtime_seconds", "INTEGER" },
            { "hdr_type", "TEXT" },
            { "edition", "TEXT" }
        });

        // 6. dedup_groups column migration (§3.1)
        MigrateTableColumns(conn, "dedup_groups", new Dictionary<string, string>
        {
            { "image_item_id", "TEXT" },
            { "image_server_id", "TEXT" },
            { "backdrop_item_id", "TEXT" },
            { "backdrop_server_id", "TEXT" },
            { "tmdb_key", "TEXT" },
            { "primary_score", "INTEGER NOT NULL DEFAULT 0" }
        });

        // 7. user_data column migration (D7, §4.1)
        MigrateTableColumns(conn, "user_data", new Dictionary<string, string>
        {
            { "origin", "TEXT NOT NULL DEFAULT 'local'" },
            { "updated_locally", "INTEGER NOT NULL DEFAULT 0" }
        });

        // 8. watch_events migration
        MigrateTableColumns(conn, "watch_events", new Dictionary<string, string>
        {
            { "series_name", "TEXT" },
            { "genres", "TEXT" }
        });

        // 9. Indexes
        using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = @"
            CREATE INDEX IF NOT EXISTS idx_cached_items_type ON cached_items(type);
            CREATE INDEX IF NOT EXISTS idx_cached_items_parent ON cached_items(parent_id);
            CREATE INDEX IF NOT EXISTS idx_cached_items_series ON cached_items(series_id);
            CREATE INDEX IF NOT EXISTS idx_cached_items_dedup ON cached_items(dedup_group_id);
            CREATE INDEX IF NOT EXISTS idx_cached_items_tmdb ON cached_items(tmdb_id);
            CREATE INDEX IF NOT EXISTS idx_cached_items_imdb ON cached_items(imdb_id);
            CREATE INDEX IF NOT EXISTS ix_items_group ON cached_items(group_id);
            CREATE INDEX IF NOT EXISTS ix_items_server_sync ON cached_items(server_id, last_synced);
            CREATE INDEX IF NOT EXISTS ix_dedup_keys_group ON dedup_keys(group_id);
            CREATE INDEX IF NOT EXISTS ix_dedup_groups_shelf ON dedup_groups(item_type, date_created DESC);
            CREATE INDEX IF NOT EXISTS ix_dedup_groups_primary ON dedup_groups(primary_item_id, primary_server);
            CREATE INDEX IF NOT EXISTS ix_dedup_groups_series ON dedup_groups(series_group_id, season_number, episode_number);
            CREATE INDEX IF NOT EXISTS ix_dedup_groups_sort ON dedup_groups(item_type, sort_name);
            CREATE INDEX IF NOT EXISTS ix_groups_needs_art ON dedup_groups(group_id) WHERE image_tag IS NULL AND tmdb_key IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_trakt_queue_order ON trakt_queue(queued_utc ASC);
            CREATE INDEX IF NOT EXISTS ix_watchlist_entries_added ON watchlist_entries(added_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_watch_events_time ON watch_events(watched_at);
            CREATE INDEX IF NOT EXISTS idx_watch_events_source ON watch_events(source);
        ";
        idxCmd.ExecuteNonQuery();

        // 8. FTS5 Search & triggers
        try
        {
            using var ftsCmd = conn.CreateCommand();
            ftsCmd.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS dedup_groups_fts USING fts5(
                    name,
                    sort_name,
                    content='dedup_groups',
                    content_rowid='group_id'
                );

                CREATE TRIGGER IF NOT EXISTS dg_fts_ai AFTER INSERT ON dedup_groups BEGIN
                  INSERT INTO dedup_groups_fts(rowid, name, sort_name)
                    VALUES (new.group_id, new.name, new.sort_name);
                END;

                CREATE TRIGGER IF NOT EXISTS dg_fts_ad AFTER DELETE ON dedup_groups BEGIN
                  INSERT INTO dedup_groups_fts(dedup_groups_fts, rowid, name, sort_name)
                    VALUES ('delete', old.group_id, old.name, old.sort_name);
                END;

                CREATE TRIGGER IF NOT EXISTS dg_fts_au AFTER UPDATE ON dedup_groups BEGIN
                  INSERT INTO dedup_groups_fts(dedup_groups_fts, rowid, name, sort_name)
                    VALUES ('delete', old.group_id, old.name, old.sort_name);
                  INSERT INTO dedup_groups_fts(rowid, name, sort_name)
                    VALUES (new.group_id, new.name, new.sort_name);
                END;
            ";
            ftsCmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("DatabaseService", $"FTS5 setup note: {ex.Message}");
        }
    }

    private void MigrateTableColumns(SqliteConnection conn, string tableName, Dictionary<string, string> requiredColumns)
    {
        try
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({tableName});";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            foreach (var (col, colType) in requiredColumns)
            {
                if (!existingColumns.Contains(col))
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {col} {colType};";
                    alterCmd.ExecuteNonQuery();
                    NocturneLogger.Info("DatabaseService", $"Migrated schema: added column '{col}' to table '{tableName}'.");
                }
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("DatabaseService", $"Schema migration warning for table {tableName}: {ex.Message}");
        }
    }

    private static readonly string SqlUpsertItem = Checked(@"
                INSERT INTO cached_items (
                    id, server_id, parent_id, group_id, name, type, run_time_ticks, production_year,
                    community_rating, index_number, parent_index_number, series_id, series_name,
                    season_id, season_name, genres, tmdb_id, imdb_id, dedup_group_id,
                    height, width, video_codec, audio_codec, bitrate, size_bytes, runtime_seconds, hdr_type, edition,
                    json_data, last_synced
                ) VALUES (
                    $id, $server_id, $parent_id, $group_id, $name, $type, $run_time_ticks, $production_year,
                    $community_rating, $index_number, $parent_index_number, $series_id, $series_name,
                    $season_id, $season_name, $genres, $tmdb_id, $imdb_id, $dedup_group_id,
                    $height, $width, $video_codec, $audio_codec, $bitrate, $size_bytes, $runtime_seconds, $hdr_type, $edition,
                    $json_data, $last_synced
                )
                ON CONFLICT(id, server_id) DO UPDATE SET
                    name = excluded.name,
                    parent_id = COALESCE(excluded.parent_id, cached_items.parent_id),
                    group_id = COALESCE(excluded.group_id, cached_items.group_id),
                    type = excluded.type,
                    run_time_ticks = excluded.run_time_ticks,
                    production_year = excluded.production_year,
                    community_rating = excluded.community_rating,
                    index_number = excluded.index_number,
                    parent_index_number = excluded.parent_index_number,
                    series_id = excluded.series_id,
                    series_name = excluded.series_name,
                    season_id = excluded.season_id,
                    season_name = excluded.season_name,
                    genres = excluded.genres,
                    tmdb_id = excluded.tmdb_id,
                    imdb_id = excluded.imdb_id,
                    dedup_group_id = COALESCE(excluded.dedup_group_id, cached_items.dedup_group_id),
                    height = COALESCE(excluded.height, cached_items.height),
                    width = COALESCE(excluded.width, cached_items.width),
                    video_codec = COALESCE(excluded.video_codec, cached_items.video_codec),
                    audio_codec = COALESCE(excluded.audio_codec, cached_items.audio_codec),
                    bitrate = COALESCE(excluded.bitrate, cached_items.bitrate),
                    size_bytes = COALESCE(excluded.size_bytes, cached_items.size_bytes),
                    runtime_seconds = COALESCE(excluded.runtime_seconds, cached_items.runtime_seconds),
                    hdr_type = COALESCE(excluded.hdr_type, cached_items.hdr_type),
                    edition = COALESCE(excluded.edition, cached_items.edition),
                    json_data = excluded.json_data,
                    last_synced = excluded.last_synced;
            ");
    private static readonly string SqlUpsertItem2 = Checked(@"
                    INSERT INTO user_data (server_id, item_id, position_ticks, runtime_ticks, played, last_played_utc, origin, updated_locally)
                    VALUES ($u_srv, $u_item, $u_pos, $u_run, $u_played, $u_last, 'emby:' || $u_srv, 0)
                    ON CONFLICT(server_id, item_id) DO UPDATE SET
                        position_ticks = CASE WHEN user_data.updated_locally = 1 AND user_data.last_played_utc > excluded.last_played_utc THEN user_data.position_ticks ELSE excluded.position_ticks END,
                        played = CASE WHEN user_data.updated_locally = 1 AND user_data.last_played_utc > excluded.last_played_utc THEN user_data.played ELSE excluded.played END,
                        last_played_utc = CASE WHEN user_data.updated_locally = 1 AND user_data.last_played_utc > excluded.last_played_utc THEN user_data.last_played_utc ELSE excluded.last_played_utc END,
                        origin = CASE WHEN user_data.updated_locally = 1 AND user_data.last_played_utc > excluded.last_played_utc THEN user_data.origin ELSE excluded.origin END;
                ");

    public void UpsertItem(EmbyItem item, string serverId, string? parentId = null)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => UpsertItem(item, serverId, parentId))) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlUpsertItem;

            int? gid = null;
            if (int.TryParse(item.DedupGroupId, out int parsedGid)) gid = parsedGid;

            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$parent_id", (object?)parentId ?? (object?)item.SeasonId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$group_id", (object?)gid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", item.Name);
            cmd.Parameters.AddWithValue("$type", item.Type);
            cmd.Parameters.AddWithValue("$run_time_ticks", (object?)item.RunTimeTicks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$production_year", (object?)item.ProductionYear ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$community_rating", (object?)item.CommunityRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$index_number", (object?)item.IndexNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$parent_index_number", (object?)item.ParentIndexNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$series_id", (object?)item.SeriesId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$series_name", (object?)item.SeriesName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$season_id", (object?)item.SeasonId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$season_name", (object?)item.SeasonName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$genres", item.Genres != null ? string.Join(",", item.Genres) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$tmdb_id", (object?)item.TmdbId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$imdb_id", (object?)item.ImdbId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dedup_group_id", (object?)item.DedupGroupId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$height", (object?)item.Height ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$width", (object?)item.Width ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$video_codec", (object?)item.VideoCodec ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$audio_codec", (object?)item.AudioCodec ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bitrate", (object?)item.MeasuredBitrate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size_bytes", (object?)item.SizeBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$runtime_seconds", (object?)item.RuntimeSeconds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hdr_type", (object?)item.HdrType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$edition", (object?)item.Edition ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$json_data", JsonSerializer.Serialize(item));
            cmd.Parameters.AddWithValue("$last_synced", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            cmd.ExecuteNonQuery();

            if (item.UserData != null)
            {
                using var userCmd = _connection.CreateCommand();
                userCmd.CommandText = SqlUpsertItem2;
                userCmd.Parameters.AddWithValue("$u_srv", serverId);
                userCmd.Parameters.AddWithValue("$u_item", item.Id);
                userCmd.Parameters.AddWithValue("$u_pos", item.UserData.PlaybackPositionTicks);
                userCmd.Parameters.AddWithValue("$u_run", (object?)item.RunTimeTicks ?? DBNull.Value);
                userCmd.Parameters.AddWithValue("$u_played", item.UserData.Played ? 1 : 0);
                userCmd.Parameters.AddWithValue("$u_last", item.UserData.LastPlayedDate.HasValue
                    ? ((DateTimeOffset)item.UserData.LastPlayedDate.Value).ToUnixTimeSeconds()
                    : DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                userCmd.ExecuteNonQuery();
            }
        }
    }

    private static readonly string SqlUpsertItems = Checked(@"
                    INSERT INTO cached_items (
                        id, server_id, parent_id, group_id, name, type, run_time_ticks, production_year,
                        community_rating, index_number, parent_index_number, series_id, series_name,
                        season_id, season_name, genres, tmdb_id, imdb_id, dedup_group_id,
                        height, width, video_codec, audio_codec, bitrate, size_bytes, runtime_seconds, hdr_type, edition,
                        json_data, last_synced
                    ) VALUES (
                        $id, $server_id, $parent_id, $group_id, $name, $type, $run_time_ticks, $production_year,
                        $community_rating, $index_number, $parent_index_number, $series_id, $series_name,
                        $season_id, $season_name, $genres, $tmdb_id, $imdb_id, $dedup_group_id,
                        $height, $width, $video_codec, $audio_codec, $bitrate, $size_bytes, $runtime_seconds, $hdr_type, $edition,
                        $json_data, $last_synced
                    )
                    ON CONFLICT(id, server_id) DO UPDATE SET
                        name = excluded.name,
                        parent_id = COALESCE(excluded.parent_id, cached_items.parent_id),
                        group_id = COALESCE(excluded.group_id, cached_items.group_id),
                        type = excluded.type,
                        run_time_ticks = excluded.run_time_ticks,
                        production_year = excluded.production_year,
                        community_rating = excluded.community_rating,
                        index_number = excluded.index_number,
                        parent_index_number = excluded.parent_index_number,
                        series_id = excluded.series_id,
                        series_name = excluded.series_name,
                        season_id = excluded.season_id,
                        season_name = excluded.season_name,
                        genres = excluded.genres,
                        tmdb_id = excluded.tmdb_id,
                        imdb_id = excluded.imdb_id,
                        dedup_group_id = COALESCE(excluded.dedup_group_id, cached_items.dedup_group_id),
                        height = COALESCE(excluded.height, cached_items.height),
                        width = COALESCE(excluded.width, cached_items.width),
                        video_codec = COALESCE(excluded.video_codec, cached_items.video_codec),
                        audio_codec = COALESCE(excluded.audio_codec, cached_items.audio_codec),
                        bitrate = COALESCE(excluded.bitrate, cached_items.bitrate),
                        size_bytes = COALESCE(excluded.size_bytes, cached_items.size_bytes),
                        runtime_seconds = COALESCE(excluded.runtime_seconds, cached_items.runtime_seconds),
                        hdr_type = COALESCE(excluded.hdr_type, cached_items.hdr_type),
                        edition = COALESCE(excluded.edition, cached_items.edition),
                        json_data = excluded.json_data,
                        last_synced = excluded.last_synced;
                ");
    private static readonly string SqlUpsertItems2 = Checked(@"
                    INSERT INTO user_data (server_id, item_id, position_ticks, runtime_ticks, played, last_played_utc, origin, updated_locally)
                    VALUES ($u_srv, $u_item, $u_pos, $u_run, $u_played, $u_last, 'emby:' || $u_srv, 0)
                    ON CONFLICT(server_id, item_id) DO UPDATE SET
                        position_ticks = CASE WHEN user_data.updated_locally = 1 AND user_data.last_played_utc > excluded.last_played_utc THEN user_data.position_ticks ELSE excluded.position_ticks END,
                        played = CASE WHEN user_data.updated_locally = 1 AND user_data.last_played_utc > excluded.last_played_utc THEN user_data.played ELSE excluded.played END,
                        last_played_utc = CASE WHEN user_data.updated_locally = 1 AND user_data.last_played_utc > excluded.last_played_utc THEN user_data.last_played_utc ELSE excluded.last_played_utc END,
                        origin = CASE WHEN user_data.updated_locally = 1 AND user_data.last_played_utc > excluded.last_played_utc THEN user_data.origin ELSE excluded.origin END;
                ");
    private static readonly string SqlUpsertItems3 = Checked(@"
                    INSERT INTO watchlist_entries (source, ref, item_type, added_utc, title, year, tmdb_key)
                    VALUES ($w_src, $w_ref, $w_type, $w_added, $w_title, $w_year, $w_tmdb)
                    ON CONFLICT(source, ref) DO UPDATE SET
                        item_type = excluded.item_type,
                        title = COALESCE(excluded.title, watchlist_entries.title),
                        year = COALESCE(excluded.year, watchlist_entries.year),
                        tmdb_key = COALESCE(excluded.tmdb_key, watchlist_entries.tmdb_key);
                ");
    private static readonly string SqlUpsertItems4 = Checked(@"
                        INSERT INTO sync_checkpoints (server_id, library_id, start_index, total_items, is_completed, last_synced)
                        VALUES ($cs_server_id, $cs_library_id, $cs_start_index, $cs_total_items, $cs_is_completed, $cs_last_synced)
                        ON CONFLICT(server_id, library_id) DO UPDATE SET
                            start_index = excluded.start_index,
                            total_items = excluded.total_items,
                            is_completed = excluded.is_completed,
                            last_synced = excluded.last_synced;
                    ");

    public void UpsertItems(
        IEnumerable<EmbyItem> items,
        string serverId,
        string? parentId = null,
        int? checkpointStartIndex = null,
        int? checkpointTotalItems = null,
        bool checkpointIsCompleted = false)
    {
        var list = items.ToList();
        if (list.Count == 0 && !checkpointStartIndex.HasValue) return;

        lock (_dbLock)
        {
            if (_connection == null) return;

            const int chunkSize = 2000;
            int totalChunks = list.Count > 0 ? (int)Math.Ceiling((double)list.Count / chunkSize) : 1;
            int currentChunk = 0;

            for (int offset = 0; offset < Math.Max(1, list.Count); offset += chunkSize)
            {
                currentChunk++;
                bool isFinalChunk = currentChunk >= totalChunks;
                var chunk = list.Skip(offset).Take(chunkSize).ToList();

                using var tx = _connection.BeginTransaction();
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = SqlUpsertItems;

                var pId = cmd.Parameters.Add("$id", SqliteType.Text);
                var pServerId = cmd.Parameters.Add("$server_id", SqliteType.Text);
                var pParentId = cmd.Parameters.Add("$parent_id", SqliteType.Text);
                var pGroupId = cmd.Parameters.Add("$group_id", SqliteType.Integer);
                var pName = cmd.Parameters.Add("$name", SqliteType.Text);
                var pType = cmd.Parameters.Add("$type", SqliteType.Text);
                var pRunTimeTicks = cmd.Parameters.Add("$run_time_ticks", SqliteType.Integer);
                var pProductionYear = cmd.Parameters.Add("$production_year", SqliteType.Integer);
                var pCommunityRating = cmd.Parameters.Add("$community_rating", SqliteType.Real);
                var pIndexNumber = cmd.Parameters.Add("$index_number", SqliteType.Integer);
                var pParentIndexNumber = cmd.Parameters.Add("$parent_index_number", SqliteType.Integer);
                var pSeriesId = cmd.Parameters.Add("$series_id", SqliteType.Text);
                var pSeriesName = cmd.Parameters.Add("$series_name", SqliteType.Text);
                var pSeasonId = cmd.Parameters.Add("$season_id", SqliteType.Text);
                var pSeasonName = cmd.Parameters.Add("$season_name", SqliteType.Text);
                var pGenres = cmd.Parameters.Add("$genres", SqliteType.Text);
                var pTmdbId = cmd.Parameters.Add("$tmdb_id", SqliteType.Text);
                var pImdbId = cmd.Parameters.Add("$imdb_id", SqliteType.Text);
                var pDedupGroupId = cmd.Parameters.Add("$dedup_group_id", SqliteType.Text);
                var pHeight = cmd.Parameters.Add("$height", SqliteType.Integer);
                var pWidth = cmd.Parameters.Add("$width", SqliteType.Integer);
                var pVideoCodec = cmd.Parameters.Add("$video_codec", SqliteType.Text);
                var pAudioCodec = cmd.Parameters.Add("$audio_codec", SqliteType.Text);
                var pBitrate = cmd.Parameters.Add("$bitrate", SqliteType.Integer);
                var pSizeBytes = cmd.Parameters.Add("$size_bytes", SqliteType.Integer);
                var pRuntimeSeconds = cmd.Parameters.Add("$runtime_seconds", SqliteType.Integer);
                var pHdrType = cmd.Parameters.Add("$hdr_type", SqliteType.Text);
                var pEdition = cmd.Parameters.Add("$edition", SqliteType.Text);
                var pJsonData = cmd.Parameters.Add("$json_data", SqliteType.Text);
                var pLastSynced = cmd.Parameters.Add("$last_synced", SqliteType.Integer);

                using var userCmd = _connection.CreateCommand();
                userCmd.Transaction = tx;
                userCmd.CommandText = SqlUpsertItems2;
                var puSrv = userCmd.Parameters.Add("$u_srv", SqliteType.Text);
                var puItem = userCmd.Parameters.Add("$u_item", SqliteType.Text);
                var puPos = userCmd.Parameters.Add("$u_pos", SqliteType.Integer);
                var puRun = userCmd.Parameters.Add("$u_run", SqliteType.Integer);
                var puPlayed = userCmd.Parameters.Add("$u_played", SqliteType.Integer);
                var puLast = userCmd.Parameters.Add("$u_last", SqliteType.Integer);

                using var wlCmd = _connection.CreateCommand();
                wlCmd.Transaction = tx;
                wlCmd.CommandText = SqlUpsertItems3;
                var pwSrc = wlCmd.Parameters.Add("$w_src", SqliteType.Text);
                var pwRef = wlCmd.Parameters.Add("$w_ref", SqliteType.Text);
                var pwType = wlCmd.Parameters.Add("$w_type", SqliteType.Text);
                var pwAdded = wlCmd.Parameters.Add("$w_added", SqliteType.Integer);
                var pwTitle = wlCmd.Parameters.Add("$w_title", SqliteType.Text);
                var pwYear = wlCmd.Parameters.Add("$w_year", SqliteType.Integer);
                var pwTmdb = wlCmd.Parameters.Add("$w_tmdb", SqliteType.Text);

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var item in chunk)
                {
                    int? itemGid = null;
                    if (int.TryParse(item.DedupGroupId, out int parsed)) itemGid = parsed;

                    pId.Value = item.Id;
                    pServerId.Value = serverId;
                    pParentId.Value = (object?)parentId ?? (object?)item.SeasonId ?? DBNull.Value;
                    pGroupId.Value = (object?)itemGid ?? DBNull.Value;
                    pName.Value = item.Name;
                    pType.Value = item.Type;
                    pRunTimeTicks.Value = (object?)item.RunTimeTicks ?? DBNull.Value;
                    pProductionYear.Value = (object?)item.ProductionYear ?? DBNull.Value;
                    pCommunityRating.Value = (object?)item.CommunityRating ?? DBNull.Value;
                    pIndexNumber.Value = (object?)item.IndexNumber ?? DBNull.Value;
                    pParentIndexNumber.Value = (object?)item.ParentIndexNumber ?? DBNull.Value;
                    pSeriesId.Value = (object?)item.SeriesId ?? DBNull.Value;
                    pSeriesName.Value = (object?)item.SeriesName ?? DBNull.Value;
                    pSeasonId.Value = (object?)item.SeasonId ?? DBNull.Value;
                    pSeasonName.Value = (object?)item.SeasonName ?? DBNull.Value;
                    pGenres.Value = item.Genres != null ? string.Join(",", item.Genres) : (object)DBNull.Value;
                    pTmdbId.Value = (object?)item.TmdbId ?? DBNull.Value;
                    pImdbId.Value = (object?)item.ImdbId ?? DBNull.Value;
                    pDedupGroupId.Value = (object?)item.DedupGroupId ?? DBNull.Value;
                    pHeight.Value = (object?)item.Height ?? DBNull.Value;
                    pWidth.Value = (object?)item.Width ?? DBNull.Value;
                    pVideoCodec.Value = (object?)item.VideoCodec ?? DBNull.Value;
                    pAudioCodec.Value = (object?)item.AudioCodec ?? DBNull.Value;
                    pBitrate.Value = (object?)item.MeasuredBitrate ?? DBNull.Value;
                    pSizeBytes.Value = (object?)item.SizeBytes ?? DBNull.Value;
                    pRuntimeSeconds.Value = (object?)item.RuntimeSeconds ?? DBNull.Value;
                    pHdrType.Value = (object?)item.HdrType ?? DBNull.Value;
                    pEdition.Value = (object?)item.Edition ?? DBNull.Value;
                    pJsonData.Value = JsonSerializer.Serialize(item);
                    pLastSynced.Value = now;

                    cmd.ExecuteNonQuery();

                    if (item.UserData != null && (item.UserData.PlaybackPositionTicks > 0 || item.UserData.Played))
                    {
                        long lastPlayed = item.UserData.LastPlayedDate.HasValue
                            ? new DateTimeOffset(item.UserData.LastPlayedDate.Value).ToUnixTimeSeconds()
                            : now;
                        puSrv.Value = serverId;
                        puItem.Value = item.Id;
                        puPos.Value = item.UserData.PlaybackPositionTicks;
                        puRun.Value = (object?)item.RunTimeTicks ?? DBNull.Value;
                        puPlayed.Value = item.UserData.Played ? 1 : 0;
                        puLast.Value = lastPlayed;
                        userCmd.ExecuteNonQuery();
                    }

                    if (item.UserData != null && item.UserData.IsFavorite)
                    {
                        string? tmdbKey = !string.IsNullOrEmpty(item.TmdbId)
                            ? $"tmdb:{(string.Equals(item.Type, "Series", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie")}:{item.TmdbId}"
                            : null;
                        pwSrc.Value = serverId;
                        pwRef.Value = item.Id;
                        pwType.Value = item.Type ?? "Movie";
                        pwAdded.Value = now;
                        pwTitle.Value = item.Name;
                        pwYear.Value = (object?)item.ProductionYear ?? DBNull.Value;
                        pwTmdb.Value = (object?)tmdbKey ?? DBNull.Value;
                        wlCmd.ExecuteNonQuery();
                    }
                }

                // Crash safety: write the checkpoint in the exact same transaction as the data it covers
                if (isFinalChunk && checkpointStartIndex.HasValue && !string.IsNullOrEmpty(parentId))
                {
                    using var cpCmd = _connection.CreateCommand();
                    cpCmd.Transaction = tx;
                    cpCmd.CommandText = SqlUpsertItems4;
                    cpCmd.Parameters.AddWithValue("$cs_server_id", serverId);
                    cpCmd.Parameters.AddWithValue("$cs_library_id", parentId);
                    cpCmd.Parameters.AddWithValue("$cs_start_index", checkpointStartIndex.Value);
                    cpCmd.Parameters.AddWithValue("$cs_total_items", checkpointTotalItems ?? checkpointStartIndex.Value);
                    cpCmd.Parameters.AddWithValue("$cs_is_completed", checkpointIsCompleted ? 1 : 0);
                    cpCmd.Parameters.AddWithValue("$cs_last_synced", now);
                    cpCmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
        }
    }

    public List<EmbyItem> GetCachedItems(string? type = null, string? serverId = null, int limit = 50, string? parentId = null)
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            var sql = @"
                SELECT c.json_data, c.server_id, c.dedup_group_id,
                       COALESCE((SELECT COUNT(*) FROM cached_items sub WHERE sub.dedup_group_id = c.dedup_group_id AND sub.dedup_group_id IS NOT NULL AND sub.dedup_group_id != ''), 1) AS v_count
                FROM cached_items c WHERE 1=1 ";

            if (!string.IsNullOrEmpty(type))
            {
                sql += "AND c.type = $type ";
                cmd.Parameters.AddWithValue("$type", type);
            }
            if (!string.IsNullOrEmpty(serverId))
            {
                sql += "AND c.server_id = $server_id ";
                cmd.Parameters.AddWithValue("$server_id", serverId);
            }
            if (!string.IsNullOrEmpty(parentId))
            {
                sql += "AND c.parent_id = $parent_id ";
                cmd.Parameters.AddWithValue("$parent_id", parentId);
            }
            sql += "ORDER BY c.name ASC LIMIT $limit;";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var dedupId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var vCount = reader.IsDBNull(3) ? 1 : Convert.ToInt32(reader.GetInt64(3));

                var item = JsonSerializer.Deserialize<EmbyItem>(json);
                if (item != null)
                {
                    item.ServerId = sid;
                    item.DedupGroupId = dedupId;
                    item.VersionCount = Math.Max(1, vCount);
                    items.Add(item);
                }
            }
        }
        AttachGroupArtwork(items);
        return items;
    }

    private static readonly string SqlGroupOfItem = Checked("SELECT group_id FROM cached_items WHERE id = $id AND server_id = $srv;");

    // Spec §3 (A0): a card shows its dedup group's elected artwork: the poster and backdrop of whichever version has
    // them, on that version's own server, then TMDB's. Every display query ends here, as do items fetched live from a
    // server (looked up by (server, id)). Items without a group keep their own images.
    public void AttachGroupArtwork(IList<EmbyItem> items)
    {
        if (items.Count == 0) return;
        lock (_dbLock)
        {
            if (_connection == null) return;
            var groupOf = new Dictionary<EmbyItem, int>();
            using (var lookup = _connection.CreateCommand())
            {
                lookup.CommandText = SqlGroupOfItem;
                var pId = lookup.Parameters.Add("$id", SqliteType.Text);
                var pSrv = lookup.Parameters.Add("$srv", SqliteType.Text);
                foreach (var item in items)
                {
                    if (item.ArtworkAttached) continue;
                    if (int.TryParse(item.DedupGroupId, out var gid) && gid > 0) { groupOf[item] = gid; continue; }
                    if (string.IsNullOrEmpty(item.ServerId) || string.IsNullOrEmpty(item.Id)) continue;
                    pId.Value = item.Id;
                    pSrv.Value = item.ServerId;
                    if (lookup.ExecuteScalar() is long found) groupOf[item] = (int)found;
                }
            }

            var art = new Dictionary<int, (string? Tag, string? Item, string? Server, string? BTag, string? BItem, string? BServer, string? Poster, string? Backdrop)>();
            foreach (var chunk in groupOf.Values.Distinct().Chunk(500))
            {
                using var cmd = _connection.CreateCommand();
                var names = chunk.Select((gid, i) => { cmd.Parameters.AddWithValue($"$g{i}", gid); return $"$g{i}"; });
                cmd.CommandText = $@"
                    SELECT g.group_id, g.image_tag, g.image_item_id, g.image_server_id, g.backdrop_tag, g.backdrop_item_id, g.backdrop_server_id,
                           COALESCE(t.poster_path, t.still_path), t.backdrop_path
                    FROM dedup_groups g
                    LEFT JOIN tmdb_metadata t ON t.provider_key = g.tmdb_key AND t.not_found = 0
                    WHERE g.group_id IN ({string.Join(",", names)});";
                using var r = cmd.ExecuteReader();
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                while (r.Read()) art[r.GetInt32(0)] = (S(1), S(2), S(3), S(4), S(5), S(6), S(7), S(8));
            }

            foreach (var (item, gid) in groupOf)
            {
                if (!art.TryGetValue(gid, out var a)) continue;
                item.ArtworkAttached = true;
                (item.ElectedImageTag, item.ImageItemId, item.ImageServerId) = (a.Tag, a.Item, a.Server);
                (item.ElectedBackdropTag, item.ArtworkBackdropItemId, item.ArtworkBackdropServerId) = (a.BTag, a.BItem, a.BServer);
                (item.TmdbPosterPath, item.TmdbBackdropPath) = (a.Poster, a.Backdrop);
            }
        }
    }

    private static readonly string SqlSaveContinueWatchingCache = Checked("DELETE FROM continue_watching_cache;");
    private static readonly string SqlSaveContinueWatchingCache2 = Checked(@"
                    INSERT OR REPLACE INTO continue_watching_cache (id, server_id, sort_order, json_data, updated_at)
                    VALUES ($id, $server_id, $sort_order, $json_data, $updated_at);");

    public void SaveContinueWatchingCache(IEnumerable<EmbyItem> items)
    {
        var list = items.ToList();
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => SaveContinueWatchingCache(list))) return;
            if (_connection == null) return;
            using var tx = _connection.BeginTransaction();
            using var delCmd = _connection.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = SqlSaveContinueWatchingCache;
            delCmd.ExecuteNonQuery();

            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = SqlSaveContinueWatchingCache2;
                cmd.Parameters.AddWithValue("$id", item.Id);
                cmd.Parameters.AddWithValue("$server_id", item.ServerId ?? string.Empty);
                cmd.Parameters.AddWithValue("$sort_order", i);
                cmd.Parameters.AddWithValue("$json_data", JsonSerializer.Serialize(item));
                cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static readonly string SqlGetCachedContinueWatching = Checked("SELECT json_data, server_id FROM continue_watching_cache ORDER BY sort_order ASC;");

    public List<EmbyItem> GetCachedContinueWatching()
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            if (_connection == null) return items;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetCachedContinueWatching;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var item = JsonSerializer.Deserialize<EmbyItem>(json);
                if (item != null)
                {
                    item.ServerId = sid;
                    items.Add(item);
                }
            }
        }
        AttachGroupArtwork(items);
        return items;
    }

    private static readonly string SqlRemoveFromContinueWatchingCache = Checked("DELETE FROM continue_watching_cache WHERE id = $id;");

    public void RemoveFromContinueWatchingCache(string itemId)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => RemoveFromContinueWatchingCache(itemId))) return;
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlRemoveFromContinueWatchingCache;
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.ExecuteNonQuery();
        }
    }

    private static readonly string SqlGetSeriesProviderIds = Checked("SELECT json_extract(json_data, '$.ProviderIds') FROM cached_items WHERE id = $id AND server_id = $srv AND type = 'Series';");

    // ProviderIds of a cached Series row (same server), without deserializing the whole item.
    public Dictionary<string, string>? GetSeriesProviderIds(string serverId, string seriesId)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetSeriesProviderIds;
            cmd.Parameters.AddWithValue("$id", seriesId);
            cmd.Parameters.AddWithValue("$srv", serverId);
            return cmd.ExecuteScalar() is string json && json.Length > 2
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                : null;
        }
    }

    private static readonly string SqlGetCachedItem = Checked("SELECT json_data FROM cached_items WHERE id = $id AND server_id = $serverId LIMIT 1;");
    private static readonly string SqlGetCachedItem2 = Checked("SELECT json_data FROM cached_items WHERE id = $id LIMIT 1;");

    public EmbyItem? GetCachedItem(string itemId, string? serverId = null)
    {
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            if (!string.IsNullOrEmpty(serverId))
            {
                cmd.CommandText = SqlGetCachedItem;
                cmd.Parameters.AddWithValue("$id", itemId);
                cmd.Parameters.AddWithValue("$serverId", serverId);
            }
            else
            {
                cmd.CommandText = SqlGetCachedItem2;
                cmd.Parameters.AddWithValue("$id", itemId);
            }

            var result = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(result)) return null;

            try
            {
                var item = JsonSerializer.Deserialize<EmbyItem>(result);
                if (item != null && !string.IsNullOrEmpty(serverId))
                {
                    item.ServerId = serverId;
                }
                if (item != null) AttachGroupArtwork(new[] { item });
                return item;
            }
            catch (Exception ex) when (QueryFailed(ex, $"json_data of cached item {itemId}"))
            {
                return null;
            }
        }
    }

    public List<EmbyItem> GetCachedChildren(string parentId, string? type = null)
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            var sql = @"
                SELECT c.json_data, c.server_id, c.dedup_group_id
                FROM cached_items c
                WHERE c.parent_id = $parent_id ";
            if (!string.IsNullOrEmpty(type))
            {
                sql += "AND c.type = $type ";
                cmd.Parameters.AddWithValue("$type", type);
            }
            sql += "ORDER BY c.index_number ASC, c.name ASC;";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$parent_id", parentId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var dedupId = reader.IsDBNull(2) ? null : reader.GetString(2);

                try
                {
                    var item = JsonSerializer.Deserialize<EmbyItem>(json);
                    if (item != null)
                    {
                        item.ServerId = sid;
                        item.DedupGroupId = dedupId;
                        items.Add(item);
                    }
                }
                catch (Exception ex) when (QueryFailed(ex, sql)) { }
            }
        }
        AttachGroupArtwork(items);
        return items;
    }

    public List<EmbyItem> GetCombinedLibraryItems(
        IEnumerable<string> libraryIds,
        string? type = null,
        string? sortBy = "name",
        string? sortOrder = "asc",
        int limit = 100)
    {
        var items = new List<EmbyItem>();
        var libList = libraryIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();

        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            var sql = @"
                SELECT c.json_data, c.server_id, c.dedup_group_id,
                       COALESCE(dg.version_count, 1) AS v_count
                FROM cached_items c
                LEFT JOIN dedup_groups dg ON c.group_id = dg.group_id
                WHERE (dg.group_id IS NULL OR (c.id = dg.primary_item_id AND c.server_id = dg.primary_server)) ";

            if (libList.Count > 0)
            {
                var pNames = new List<string>();
                for (int i = 0; i < libList.Count; i++)
                {
                    var pName = $"$lib{i}";
                    pNames.Add(pName);
                    cmd.Parameters.AddWithValue(pName, libList[i]);
                }
                sql += $"AND c.parent_id IN ({string.Join(",", pNames)}) ";
            }

            if (!string.IsNullOrEmpty(type))
            {
                sql += "AND c.type = $type ";
                cmd.Parameters.AddWithValue("$type", type);
            }

            var orderCol = (sortBy?.ToLowerInvariant()) switch
            {
                "communityrating" => "c.community_rating",
                "productionyear" => "c.production_year",
                "datecreated" => "c.last_synced",
                _ => "c.name"
            };
            var orderDir = (sortOrder?.ToLowerInvariant() == "desc") ? "DESC" : "ASC";

            sql += $"ORDER BY {orderCol} {orderDir} LIMIT $limit;";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var dedupId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var vCount = reader.IsDBNull(3) ? 1 : Convert.ToInt32(reader.GetInt64(3));

                var item = JsonSerializer.Deserialize<EmbyItem>(json);
                if (item != null)
                {
                    item.ServerId = sid;
                    item.DedupGroupId = dedupId;
                    item.VersionCount = Math.Max(1, vCount);
                    items.Add(item);
                }
            }
        }
        AttachGroupArtwork(items);
        return items;
    }

    private static readonly string SqlGetGroupVersions = Checked(@"
                    SELECT json_data, server_id, dedup_group_id,
                           height, width, video_codec, audio_codec, bitrate, size_bytes, hdr_type, edition
                    FROM cached_items
                    WHERE group_id = $gid OR dedup_group_id = $group_id;");
    private static readonly string SqlGetGroupVersions2 = Checked(@"
                    SELECT json_data, server_id, dedup_group_id,
                           height, width, video_codec, audio_codec, bitrate, size_bytes, hdr_type, edition
                    FROM cached_items
                    WHERE dedup_group_id = $group_id;");

    public List<EmbyItem> GetGroupVersions(string groupId)
    {
        var list = new List<EmbyItem>();
        if (string.IsNullOrEmpty(groupId)) return list;

        lock (_dbLock)
        {
            if (_connection == null) return list;
            using var cmd = _connection.CreateCommand();
            if (long.TryParse(groupId, out var gid))
            {
                cmd.CommandText = SqlGetGroupVersions;
                cmd.Parameters.AddWithValue("$gid", gid);
                cmd.Parameters.AddWithValue("$group_id", groupId);
            }
            else
            {
                cmd.CommandText = SqlGetGroupVersions2;
                cmd.Parameters.AddWithValue("$group_id", groupId);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var g = reader.IsDBNull(2) ? groupId : reader.GetString(2);
                var item = JsonSerializer.Deserialize<EmbyItem>(json);
                if (item != null)
                {
                    item.ServerId = sid;
                    item.DedupGroupId = g;
                    if (!reader.IsDBNull(3)) item.Height = reader.GetInt32(3);
                    if (!reader.IsDBNull(4)) item.Width = reader.GetInt32(4);
                    if (!reader.IsDBNull(5)) item.VideoCodec = reader.GetString(5);
                    if (!reader.IsDBNull(6)) item.AudioCodec = reader.GetString(6);
                    if (!reader.IsDBNull(7)) item.Bitrate = reader.GetInt64(7);
                    if (!reader.IsDBNull(8)) item.SizeBytes = reader.GetInt64(8);
                    list.Add(item);
                }
            }
        }
        return list;
    }

    private static readonly string SqlGetSiblingVersions = Checked("SELECT COALESCE(CAST(group_id AS TEXT), dedup_group_id) FROM cached_items WHERE id = $id LIMIT 1;");
    private static readonly string SqlGetSiblingVersions2 = Checked("SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE tmdb_id = $val AND id != $id;");
    private static readonly string SqlGetSiblingVersions3 = Checked("SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE imdb_id = $val AND id != $id;");
    private static readonly string SqlGetSiblingVersions4 = Checked("SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE LOWER(name) = $name AND production_year = $year AND id != $id;");

    public List<EmbyItem> GetSiblingVersions(EmbyItem item)
    {
        string? groupId = item.DedupGroupId;
        if (string.IsNullOrEmpty(groupId))
        {
            lock (_dbLock)
            {
                if (_connection != null)
                {
                    using var lookupCmd = _connection.CreateCommand();
                    lookupCmd.CommandText = SqlGetSiblingVersions;
                    lookupCmd.Parameters.AddWithValue("$id", item.Id);
                    var res = lookupCmd.ExecuteScalar();
                    if (res != null && res != DBNull.Value) groupId = res.ToString();
                }
            }
        }

        if (!string.IsNullOrEmpty(groupId))
        {
            var byGroup = GetGroupVersions(groupId);
            if (byGroup.Count > 0) return byGroup;
        }

        var list = new List<EmbyItem> { item };
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            if (!string.IsNullOrEmpty(item.TmdbId))
            {
                cmd.CommandText = SqlGetSiblingVersions2;
                cmd.Parameters.AddWithValue("$val", item.TmdbId);
                cmd.Parameters.AddWithValue("$id", item.Id);
            }
            else if (!string.IsNullOrEmpty(item.ImdbId))
            {
                cmd.CommandText = SqlGetSiblingVersions3;
                cmd.Parameters.AddWithValue("$val", item.ImdbId);
                cmd.Parameters.AddWithValue("$id", item.Id);
            }
            else if (!string.IsNullOrEmpty(item.Name) && item.ProductionYear.HasValue)
            {
                cmd.CommandText = SqlGetSiblingVersions4;
                cmd.Parameters.AddWithValue("$name", item.Name.ToLowerInvariant());
                cmd.Parameters.AddWithValue("$year", item.ProductionYear.Value);
                cmd.Parameters.AddWithValue("$id", item.Id);
            }
            else
            {
                return list;
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var gid = reader.IsDBNull(2) ? null : reader.GetString(2);
                var match = JsonSerializer.Deserialize<EmbyItem>(json);
                if (match != null && !list.Any(x => x.Id == match.Id && x.ServerId == sid))
                {
                    match.ServerId = sid;
                    match.DedupGroupId = gid;
                    list.Add(match);
                }
            }
        }
        return list;
    }

    private static readonly string SqlRecordWatchEvent = Checked(@"
                INSERT INTO watch_events (id, item_id, server_id, item_name, series_name, item_type, duration_seconds, watched_at, source, genres)
                VALUES ($id, $item_id, $server_id, $item_name, $series_name, $item_type, $duration_seconds, $watched_at, $source, $genres);
            ");

    public void RecordWatchEvent(
        string itemId,
        string serverId,
        string itemName,
        string itemType,
        int durationSeconds,
        string? seriesName = null,
        List<string>? genres = null,
        string source = "local")
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => RecordWatchEvent(itemId, serverId, itemName, itemType, durationSeconds, seriesName, genres, source))) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlRecordWatchEvent;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$item_id", itemId);
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$item_name", itemName);
            cmd.Parameters.AddWithValue("$series_name", (object?)seriesName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$item_type", itemType);
            cmd.Parameters.AddWithValue("$duration_seconds", Math.Max(0, durationSeconds));
            cmd.Parameters.AddWithValue("$watched_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$genres", genres != null ? string.Join(",", genres) : (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public AnalyticsStats GetAnalytics(int days = 30, string source = "local")
    {
        var stats = new AnalyticsStats();
        lock (_dbLock)
        {
            long since = days > 0
                ? DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds()
                : 0; // 0 = all time

            using var cmd = _connection.CreateCommand();
            var srcFilter = source switch
            {
                "trakt" => "AND source = 'trakt'",
                "local" => "AND source = 'local'",
                _ => "" // combined
            };

            cmd.CommandText = $@"
                SELECT
                    COALESCE(SUM(duration_seconds), 0) AS total_sec,
                    COUNT(CASE WHEN item_type = 'Movie' THEN 1 END) AS movie_count,
                    COUNT(CASE WHEN item_type = 'Episode' THEN 1 END) AS episode_count,
                    COUNT(DISTINCT CASE WHEN item_type = 'Episode' AND series_name IS NOT NULL THEN series_name END) AS distinct_series
                FROM watch_events
                WHERE watched_at >= $since {srcFilter};
            ";
            cmd.Parameters.AddWithValue("$since", since);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                stats.TotalWatchTimeSeconds = reader.GetInt64(0);
                stats.MoviesWatched = reader.GetInt32(1);
                stats.EpisodesWatched = reader.GetInt32(2);
                stats.DistinctSeriesCount = reader.GetInt32(3);

                var weeks = Math.Max(1.0, (days > 0 ? days : 90) / 7.0);
                stats.AverageHoursPerWeek = (stats.TotalWatchTimeSeconds / 3600.0) / weeks;
            }

            // Top Series
            stats.TopSeries = GetTopSeries(days, source);

            // Top Movies
            stats.TopMovies = GetTopMovies(days, source);

            // Top Genres
            stats.TopGenres = GetGenreBreakdown(days, source);

            // Activity Heatmap & Frequency Bars
            stats.ActivityHeatmap = GetDailyWatchActivity(days > 0 ? Math.Min(days, 365) : 365, source);
            stats.FrequencyBars = GenerateFrequencyBars(stats.ActivityHeatmap);
        }
        return stats;
    }

    private List<TopMediaItem> GetTopSeries(int days, string source, int limit = 5)
    {
        var result = new List<TopMediaItem>();
        long since = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds() : 0;
        using var cmd = _connection!.CreateCommand();
        var srcFilter = source switch { "trakt" => "AND source = 'trakt'", "local" => "AND source = 'local'", _ => "" };

        cmd.CommandText = $@"
            SELECT COALESCE(series_name, item_name) AS title, COUNT(*) AS ep_count, SUM(duration_seconds) AS total_sec
            FROM watch_events
            WHERE item_type = 'Episode' AND watched_at >= $since {srcFilter}
            GROUP BY title
            ORDER BY ep_count DESC, total_sec DESC
            LIMIT {limit};
        ";
        cmd.Parameters.AddWithValue("$since", since);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TopMediaItem
            {
                Title = reader.GetString(0),
                PlayCount = reader.GetInt32(1),
                TotalHours = reader.GetInt64(2) / 3600.0,
                Type = "Series"
            });
        }
        return result;
    }

    private List<TopMediaItem> GetTopMovies(int days, string source, int limit = 5)
    {
        var result = new List<TopMediaItem>();
        long since = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds() : 0;
        using var cmd = _connection!.CreateCommand();
        var srcFilter = source switch { "trakt" => "AND source = 'trakt'", "local" => "AND source = 'local'", _ => "" };

        cmd.CommandText = $@"
            SELECT item_name AS title, COUNT(*) AS plays, SUM(duration_seconds) AS total_sec
            FROM watch_events
            WHERE item_type = 'Movie' AND watched_at >= $since {srcFilter}
            GROUP BY title
            ORDER BY plays DESC, total_sec DESC
            LIMIT {limit};
        ";
        cmd.Parameters.AddWithValue("$since", since);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TopMediaItem
            {
                Title = reader.GetString(0),
                PlayCount = reader.GetInt32(1),
                TotalHours = reader.GetInt64(2) / 3600.0,
                Type = "Movie"
            });
        }
        return result;
    }

    private List<GenreCount> GetGenreBreakdown(int days, string source)
    {
        var map = new Dictionary<string, (int count, long sec)>(StringComparer.OrdinalIgnoreCase);
        long since = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds() : 0;
        using var cmd = _connection!.CreateCommand();
        var srcFilter = source switch { "trakt" => "AND source = 'trakt'", "local" => "AND source = 'local'", _ => "" };

        cmd.CommandText = $@"
            SELECT genres, duration_seconds
            FROM watch_events
            WHERE genres IS NOT NULL AND genres != '' AND watched_at >= $since {srcFilter};
        ";
        cmd.Parameters.AddWithValue("$since", since);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var gStr = reader.GetString(0);
            var sec = reader.GetInt64(1);
            var genres = gStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var g in genres)
            {
                if (map.TryGetValue(g, out var val))
                {
                    map[g] = (val.count + 1, val.sec + sec);
                }
                else
                {
                    map[g] = (1, sec);
                }
            }
        }

        var totalCount = map.Values.Sum(v => v.count);
        return map.OrderByDescending(kv => kv.Value.count)
                  .Take(8)
                  .Select(kv => new GenreCount
                  {
                      Genre = kv.Key,
                      Count = kv.Value.count,
                      Hours = kv.Value.sec / 3600.0,
                      Percentage = totalCount > 0 ? (double)kv.Value.count / totalCount * 100.0 : 0.0
                  }).ToList();
    }

    private List<DayActivity> GetDailyWatchActivity(int days, string source)
    {
        var result = new List<DayActivity>();
        var dayMap = new Dictionary<string, (int count, long sec)>();
        long since = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();

        using var cmd = _connection!.CreateCommand();
        var srcFilter = source switch { "trakt" => "AND source = 'trakt'", "local" => "AND source = 'local'", _ => "" };

        cmd.CommandText = $@"
            SELECT date(watched_at, 'unixepoch') AS day, COUNT(*), SUM(duration_seconds)
            FROM watch_events
            WHERE watched_at >= $since {srcFilter}
            GROUP BY day;
        ";
        cmd.Parameters.AddWithValue("$since", since);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var day = reader.GetString(0);
            dayMap[day] = (reader.GetInt32(1), reader.GetInt64(2));
        }

        var maxSec = dayMap.Values.Count > 0 ? Math.Max(1L, dayMap.Values.Max(v => v.sec)) : 1L;

        // Generate full daily series for past N days
        var now = DateTime.UtcNow.Date;
        for (int i = days; i >= 0; i--)
        {
            var dateStr = now.AddDays(-i).ToString("yyyy-MM-dd");
            if (dayMap.TryGetValue(dateStr, out var val))
            {
                var ratio = (double)val.sec / maxSec;
                int level = ratio switch
                {
                    > 0.75 => 4,
                    > 0.50 => 3,
                    > 0.25 => 2,
                    > 0.0 => 1,
                    _ => 0
                };
                result.Add(new DayActivity
                {
                    Date = dateStr,
                    PlaysCount = val.count,
                    DurationSeconds = val.sec,
                    Level = level
                });
            }
            else
            {
                result.Add(new DayActivity
                {
                    Date = dateStr,
                    PlaysCount = 0,
                    DurationSeconds = 0,
                    Level = 0
                });
            }
        }
        return result;
    }

    private static List<FrequencyBarItem> GenerateFrequencyBars(List<DayActivity> activities)
    {
        var bars = new List<FrequencyBarItem>();
        if (activities.Count == 0) return bars;

        // Group into recent 14 buckets or past 14 days
        var recent = activities.TakeLast(14).ToList();
        var maxHours = Math.Max(0.5, recent.Max(a => a.DurationSeconds / 3600.0));

        foreach (var act in recent)
        {
            var h = act.DurationSeconds / 3600.0;
            bars.Add(new FrequencyBarItem
            {
                Label = act.Date.Length >= 5 ? act.Date[5..] : act.Date,
                Hours = h,
                HeightRatio = Math.Clamp(h / maxHours, 0.04, 1.0)
            });
        }
        return bars;
    }

    private static readonly string SqlSaveTraktWatchlist = Checked("DELETE FROM trakt_watchlist;");
    private static readonly string SqlSaveTraktWatchlist2 = Checked(@"
                    INSERT INTO trakt_watchlist (id, trakt_id, tmdb_id, imdb_id, title, year, type, added_at)
                    VALUES ($id, $trakt_id, $tmdb_id, $imdb_id, $title, $year, $type, $added_at);
                ");

    public void SaveTraktWatchlist(List<TraktWatchlistItem> items)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => SaveTraktWatchlist(items))) return;
            using var tx = _connection.BeginTransaction();
            using var delCmd = _connection.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = SqlSaveTraktWatchlist;
            delCmd.ExecuteNonQuery();

            foreach (var item in items)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = SqlSaveTraktWatchlist2;
                cmd.Parameters.AddWithValue("$id", item.Id);
                cmd.Parameters.AddWithValue("$trakt_id", (object?)null ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tmdb_id", (object?)item.TmdbId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$imdb_id", (object?)item.ImdbId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$title", item.Title);
                cmd.Parameters.AddWithValue("$year", (object?)item.Year ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$type", item.Type);
                cmd.Parameters.AddWithValue("$added_at", new DateTimeOffset(item.AddedAt).ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static readonly string SqlGetTraktWatchlist = Checked("SELECT id, title, year, type, tmdb_id, imdb_id, added_at FROM trakt_watchlist ORDER BY added_at DESC;");

    public List<TraktWatchlistItem> GetTraktWatchlist()
    {
        var list = new List<TraktWatchlistItem>();
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetTraktWatchlist;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TraktWatchlistItem
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    Year = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    Type = reader.GetString(3),
                    TmdbId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ImdbId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    AddedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)).UtcDateTime
                });
            }
        }
        return list;
    }

    private static readonly string SqlGetCachedItemsCount = Checked("SELECT COUNT(*) FROM cached_items;");

    public int GetCachedItemsCount()
    {
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetCachedItemsCount;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private static readonly string SqlGetDedupGroupsCount = Checked("SELECT COUNT(*) FROM dedup_groups WHERE version_count > 1;");

    public int GetDedupGroupsCount()
    {
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetDedupGroupsCount;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private static readonly string SqlGetTotalDedupGroupsCount = Checked("SELECT COUNT(*) FROM dedup_groups;");

    public int GetTotalDedupGroupsCount()
    {
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetTotalDedupGroupsCount;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public long GetDatabaseSize()
    {
        try
        {
            long total = 0;
            var dbPath = _dbPath;
            var walPath = _dbPath + "-wal";
            var shmPath = _dbPath + "-shm";

            if (File.Exists(dbPath)) total += new FileInfo(dbPath).Length;
            if (File.Exists(walPath)) total += new FileInfo(walPath).Length;
            if (File.Exists(shmPath)) total += new FileInfo(shmPath).Length;
            return total;
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("DatabaseService", $"Could not read the database size: {ex.Message}");
        }
        return 0;
    }

    private record PreservedWatchEvent(
        string Id, string ItemId, string ServerId, string ItemName,
        string? SeriesName, string ItemType, int DurationSeconds,
        long WatchedAt, string Source, string? Genres);

    private record PreservedTraktHistory(
        string Key, string TraktType, string? TmdbId, string? ImdbId,
        string? ShowTmdbId, int? SeasonNumber, int? EpisodeNumber,
        long WatchedAt, long SyncedAt);

    private static readonly string SqlReadAllWatchEvents = Checked("SELECT id, item_id, server_id, item_name, series_name, item_type, duration_seconds, watched_at, source, genres FROM watch_events;");

    private List<PreservedWatchEvent> ReadAllWatchEvents()
    {
        var list = new List<PreservedWatchEvent>();
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open) return list;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlReadAllWatchEvents;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new PreservedWatchEvent(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetInt64(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)
                ));
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("DatabaseService", $"Failed to read watch_events for preservation: {ex.Message}");
        }
        return list;
    }

    private static readonly string SqlReadAllTraktHistory = Checked("SELECT key, trakt_type, tmdb_id, imdb_id, show_tmdb_id, season_number, episode_number, watched_at, synced_at FROM trakt_watched_history;");

    private List<PreservedTraktHistory> ReadAllTraktHistory()
    {
        var list = new List<PreservedTraktHistory>();
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open) return list;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlReadAllTraktHistory;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new PreservedTraktHistory(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8)
                ));
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("DatabaseService", $"Failed to read trakt_watched_history for preservation: {ex.Message}");
        }
        return list;
    }

    private static readonly string SqlRestoreWatchEvents = Checked(@"
                INSERT OR IGNORE INTO watch_events (id, item_id, server_id, item_name, series_name, item_type, duration_seconds, watched_at, source, genres)
                VALUES ($id, $item_id, $server_id, $item_name, $series_name, $item_type, $duration_seconds, $watched_at, $source, $genres);
            ");

    private void RestoreWatchEvents(List<PreservedWatchEvent> events)
    {
        if (events.Count == 0 || _connection == null) return;
        try
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = SqlRestoreWatchEvents;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pItemId = cmd.Parameters.Add("$item_id", SqliteType.Text);
            var pServerId = cmd.Parameters.Add("$server_id", SqliteType.Text);
            var pItemName = cmd.Parameters.Add("$item_name", SqliteType.Text);
            var pSeriesName = cmd.Parameters.Add("$series_name", SqliteType.Text);
            var pItemType = cmd.Parameters.Add("$item_type", SqliteType.Text);
            var pDuration = cmd.Parameters.Add("$duration_seconds", SqliteType.Integer);
            var pWatchedAt = cmd.Parameters.Add("$watched_at", SqliteType.Integer);
            var pSource = cmd.Parameters.Add("$source", SqliteType.Text);
            var pGenres = cmd.Parameters.Add("$genres", SqliteType.Text);

            foreach (var ev in events)
            {
                pId.Value = ev.Id;
                pItemId.Value = ev.ItemId;
                pServerId.Value = ev.ServerId;
                pItemName.Value = ev.ItemName;
                pSeriesName.Value = (object?)ev.SeriesName ?? DBNull.Value;
                pItemType.Value = ev.ItemType;
                pDuration.Value = ev.DurationSeconds;
                pWatchedAt.Value = ev.WatchedAt;
                pSource.Value = ev.Source;
                pGenres.Value = (object?)ev.Genres ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("DatabaseService", "Failed to restore watch_events", ex);
        }
    }

    private static readonly string SqlRestoreTraktHistory = Checked(@"
                INSERT OR IGNORE INTO trakt_watched_history (key, trakt_type, tmdb_id, imdb_id, show_tmdb_id, season_number, episode_number, watched_at, synced_at)
                VALUES ($key, $trakt_type, $tmdb_id, $imdb_id, $show_tmdb_id, $season_number, $episode_number, $watched_at, $synced_at);
            ");

    private void RestoreTraktHistory(List<PreservedTraktHistory> items)
    {
        if (items.Count == 0 || _connection == null) return;
        try
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = SqlRestoreTraktHistory;
            var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
            var pType = cmd.Parameters.Add("$trakt_type", SqliteType.Text);
            var pTmdb = cmd.Parameters.Add("$tmdb_id", SqliteType.Text);
            var pImdb = cmd.Parameters.Add("$imdb_id", SqliteType.Text);
            var pShowTmdb = cmd.Parameters.Add("$show_tmdb_id", SqliteType.Text);
            var pSeason = cmd.Parameters.Add("$season_number", SqliteType.Integer);
            var pEpisode = cmd.Parameters.Add("$episode_number", SqliteType.Integer);
            var pWatchedAt = cmd.Parameters.Add("$watched_at", SqliteType.Integer);
            var pSyncedAt = cmd.Parameters.Add("$synced_at", SqliteType.Integer);

            foreach (var th in items)
            {
                pKey.Value = th.Key;
                pType.Value = th.TraktType;
                pTmdb.Value = (object?)th.TmdbId ?? DBNull.Value;
                pImdb.Value = (object?)th.ImdbId ?? DBNull.Value;
                pShowTmdb.Value = (object?)th.ShowTmdbId ?? DBNull.Value;
                pSeason.Value = (object?)th.SeasonNumber ?? DBNull.Value;
                pEpisode.Value = (object?)th.EpisodeNumber ?? DBNull.Value;
                pWatchedAt.Value = th.WatchedAt;
                pSyncedAt.Value = th.SyncedAt;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("DatabaseService", "Failed to restore trakt_watched_history", ex);
        }
    }

    public void ClearAllCache()
    {
        lock (_dbLock)
        {
            var preservedEvents = ReadAllWatchEvents();
            var preservedHistory = ReadAllTraktHistory();

            bool fileDeleteSucceeded = false;
            try
            {
                _connection?.Close();
                _connection?.Dispose();
                _connection = null;
                SqliteConnection.ClearAllPools();

                // The constructor's path: a hard-coded %APPDATA% path deleted the real database from any other
                // instance (an isolated NOCTURNE_DATA_DIR profile) and left that instance's own data in place.
                var dbPath = _dbPath;
                var walPath = _dbPath + "-wal";
                var shmPath = _dbPath + "-shm";

                if (File.Exists(dbPath)) File.Delete(dbPath);
                if (File.Exists(walPath)) File.Delete(walPath);
                if (File.Exists(shmPath)) File.Delete(shmPath);
                // image-cache\ in this directory is v3.5's (src/main/image-cache.ts); v4 never writes it.

                fileDeleteSucceeded = true;
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("DatabaseService", $"File deletion failed during ClearCache: {ex.Message}. Falling back to SQL wipe.");
            }

            Initialize();

            if (fileDeleteSucceeded)
            {
                RestoreWatchEvents(preservedEvents);
                RestoreTraktHistory(preservedHistory);
            }
            else
            {
                try
                {
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = @"
                        DELETE FROM cached_items;
                        DELETE FROM dedup_groups;
                        DELETE FROM dedup_keys;
                        DELETE FROM user_data;
                        DELETE FROM sibling_write_queue;
                        DELETE FROM trakt_watchlist;
                        DELETE FROM sync_checkpoints;
                        VACUUM;
                        PRAGMA wal_checkpoint(TRUNCATE);
                    ";
                    cmd.ExecuteNonQuery();
                }
                catch (Exception sqlEx)
                {
                    NocturneLogger.Error("DatabaseService", "SQL fallback wipe error", sqlEx);
                }
            }

            NocturneLogger.Info("DatabaseService", $"ClearAllCache completed. Preserved {preservedEvents.Count} watch events, {preservedHistory.Count} trakt history.");
        }
    }

    private static readonly string SqlClearServerData = Checked(@"
                    DELETE FROM cached_items WHERE server_id = $server_id;
                    DELETE FROM sync_checkpoints WHERE server_id = $server_id;
                    DELETE FROM sibling_write_queue WHERE server_id = $server_id;
                    DELETE FROM user_data WHERE server_id = $server_id;

                    -- Clean up empty dedup groups and keys
                    DELETE FROM dedup_groups WHERE group_id NOT IN (SELECT DISTINCT group_id FROM cached_items WHERE group_id IS NOT NULL);
                    DELETE FROM dedup_keys WHERE group_id NOT IN (SELECT group_id FROM dedup_groups);

                    -- Update remaining version counts
                    UPDATE dedup_groups
                       SET version_count = (SELECT COUNT(*) FROM cached_items c WHERE c.group_id = dedup_groups.group_id);
                ");

    public void ClearServerData(string serverId)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var tx = _connection.BeginTransaction();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = SqlClearServerData;
                cmd.Parameters.AddWithValue("$server_id", serverId);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                NocturneLogger.Error("DatabaseService", $"Failed to clear server data for {serverId}", ex);
                throw;
            }
        }
    }

    private static readonly string SqlGetItemsForDeduplication = Checked("SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE type IN ('Movie', 'Series', 'Episode');");

    public List<EmbyItem> GetItemsForDeduplication()
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = SqlGetItemsForDeduplication;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var srvId = reader.IsDBNull(1) ? null : reader.GetString(1);
                var dedupId = reader.IsDBNull(2) ? null : reader.GetString(2);
                try
                {
                    var item = JsonSerializer.Deserialize<EmbyItem>(json);
                    if (item != null)
                    {
                        if (!string.IsNullOrEmpty(srvId)) item.ServerId = srvId;
                        item.DedupGroupId = dedupId;
                        items.Add(item);
                    }
                }
                catch (Exception ex) when (QueryFailed(ex, SqlGetItemsForDeduplication)) { }
            }
        }
        return items;
    }

    private static readonly string SqlSaveDedupBatch = Checked("UPDATE cached_items SET group_id = $toGid, dedup_group_id = $toGidStr WHERE group_id = $fromGid;");
    private static readonly string SqlSaveDedupBatch2 = Checked("DELETE FROM dedup_groups WHERE group_id = $gid; DELETE FROM dedup_keys WHERE group_id = $gid;");
    private static readonly string SqlSaveDedupBatch3 = Checked(@"
                        INSERT INTO dedup_keys (key, group_id, is_authoritative)
                        VALUES ($key, $group_id, $is_auth)
                        ON CONFLICT(key) DO UPDATE SET
                            group_id = excluded.group_id,
                            is_authoritative = excluded.is_authoritative;
                    ");
    private static readonly string SqlSaveDedupBatch4 = Checked(@"
                        INSERT INTO dedup_groups (
                            group_id, item_type, primary_item_id, primary_server, is_provisional, version_count,
                            name, sort_name, year, overview, image_tag, backdrop_tag, community_rating,
                            date_created, series_group_id, season_number, episode_number,
                            max_height, has_4k, max_bitrate, audio_formats,
                            image_item_id, image_server_id, backdrop_item_id, backdrop_server_id, tmdb_key, primary_score
                        ) VALUES (
                            $group_id, $item_type, $primary_item_id, $primary_server, $is_provisional, $version_count,
                            $name, $sort_name, $year, $overview, $image_tag, $backdrop_tag, $community_rating,
                            $date_created, $series_group_id, $season_number, $episode_number,
                            $max_height, $has_4k, $max_bitrate, $audio_formats,
                            $image_item_id, $image_server_id, $backdrop_item_id, $backdrop_server_id, $tmdb_key, $primary_score
                        )
                        ON CONFLICT(group_id) DO UPDATE SET
                            item_type = excluded.item_type,
                            primary_item_id = excluded.primary_item_id,
                            primary_server = excluded.primary_server,
                            is_provisional = excluded.is_provisional,
                            version_count = excluded.version_count,
                            name = excluded.name,
                            sort_name = excluded.sort_name,
                            year = excluded.year,
                            overview = excluded.overview,
                            image_tag = excluded.image_tag,
                            backdrop_tag = excluded.backdrop_tag,
                            community_rating = excluded.community_rating,
                            date_created = excluded.date_created,
                            series_group_id = excluded.series_group_id,
                            season_number = excluded.season_number,
                            episode_number = excluded.episode_number,
                            max_height = excluded.max_height,
                            has_4k = excluded.has_4k,
                            max_bitrate = excluded.max_bitrate,
                            audio_formats = excluded.audio_formats,
                            image_item_id = excluded.image_item_id,
                            image_server_id = excluded.image_server_id,
                            backdrop_item_id = excluded.backdrop_item_id,
                            backdrop_server_id = excluded.backdrop_server_id,
                            tmdb_key = excluded.tmdb_key,
                            primary_score = excluded.primary_score;
                    ");
    private static readonly string SqlSaveDedupBatch5 = Checked("UPDATE cached_items SET group_id = $gid, dedup_group_id = $gidStr WHERE id = $id AND server_id = $srv;");

    public void SaveDedupBatch(
        List<DedupGroupRecord> dirtyGroups,
        List<int> deletedGroupIds,
        List<(int FromGid, int ToGid)> remappedGroups,
        List<(string Key, int GroupId, bool IsAuthoritative)> keysToSave,
        List<(string ServerId, string ItemId, int GroupId)>? itemAssignments = null)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var tx = _connection.BeginTransaction();
            try
            {
                ApplyDedupBatch(tx, dirtyGroups, deletedGroupIds, remappedGroups, keysToSave, itemAssignments);
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                NocturneLogger.Error("DatabaseService", "Failed to save dedup batch", ex);
                throw;
            }
        }
    }

    private void ApplyDedupBatch(
        SqliteTransaction tx,
        List<DedupGroupRecord> dirtyGroups,
        List<int> deletedGroupIds,
        List<(int FromGid, int ToGid)> remappedGroups,
        List<(string Key, int GroupId, bool IsAuthoritative)> keysToSave,
        List<(string ServerId, string ItemId, int GroupId)>? itemAssignments)
    {
        // 1. Remap cached_items if groups were merged
        if (remappedGroups.Count > 0)
        {
            using var remapCmd = tx.Connection!.CreateCommand();
            remapCmd.Transaction = tx;
            remapCmd.CommandText = SqlSaveDedupBatch;
            var pTo = remapCmd.Parameters.Add("$toGid", SqliteType.Integer);
            var pToStr = remapCmd.Parameters.Add("$toGidStr", SqliteType.Text);
            var pFrom = remapCmd.Parameters.Add("$fromGid", SqliteType.Integer);

            foreach (var (fromGid, toGid) in remappedGroups)
            {
                pFrom.Value = fromGid;
                pTo.Value = toGid;
                pToStr.Value = toGid.ToString();
                remapCmd.ExecuteNonQuery();
            }
        }

        // 2. Delete loser groups
        if (deletedGroupIds.Count > 0)
        {
            using var delCmd = tx.Connection!.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = SqlSaveDedupBatch2;
            var pDel = delCmd.Parameters.Add("$gid", SqliteType.Integer);
            foreach (var id in deletedGroupIds)
            {
                pDel.Value = id;
                delCmd.ExecuteNonQuery();
            }
        }

        // 3. Upsert keys
        if (keysToSave.Count > 0)
        {
            using var keyCmd = tx.Connection!.CreateCommand();
            keyCmd.Transaction = tx;
            keyCmd.CommandText = SqlSaveDedupBatch3;
            var pK = keyCmd.Parameters.Add("$key", SqliteType.Text);
            var pG = keyCmd.Parameters.Add("$group_id", SqliteType.Integer);
            var pA = keyCmd.Parameters.Add("$is_auth", SqliteType.Integer);

            foreach (var (k, gid, isAuth) in keysToSave)
            {
                pK.Value = k;
                pG.Value = gid;
                pA.Value = isAuth ? 1 : 0;
                keyCmd.ExecuteNonQuery();
            }
        }

        // 4. Upsert dirty groups
        if (dirtyGroups.Count > 0)
        {
            using var grpCmd = tx.Connection!.CreateCommand();
            grpCmd.Transaction = tx;
            grpCmd.CommandText = SqlSaveDedupBatch4;

            var pGroupId = grpCmd.Parameters.Add("$group_id", SqliteType.Integer);
            var pItemType = grpCmd.Parameters.Add("$item_type", SqliteType.Text);
            var pPrimaryItemId = grpCmd.Parameters.Add("$primary_item_id", SqliteType.Text);
            var pPrimaryServer = grpCmd.Parameters.Add("$primary_server", SqliteType.Text);
            var pIsProvisional = grpCmd.Parameters.Add("$is_provisional", SqliteType.Integer);
            var pVersionCount = grpCmd.Parameters.Add("$version_count", SqliteType.Integer);
            var pName = grpCmd.Parameters.Add("$name", SqliteType.Text);
            var pSortName = grpCmd.Parameters.Add("$sort_name", SqliteType.Text);
            var pYear = grpCmd.Parameters.Add("$year", SqliteType.Integer);
            var pOverview = grpCmd.Parameters.Add("$overview", SqliteType.Text);
            var pImageTag = grpCmd.Parameters.Add("$image_tag", SqliteType.Text);
            var pBackdropTag = grpCmd.Parameters.Add("$backdrop_tag", SqliteType.Text);
            var pCommunityRating = grpCmd.Parameters.Add("$community_rating", SqliteType.Real);
            var pDateCreated = grpCmd.Parameters.Add("$date_created", SqliteType.Integer);
            var pSeriesGroupId = grpCmd.Parameters.Add("$series_group_id", SqliteType.Integer);
            var pSeasonNumber = grpCmd.Parameters.Add("$season_number", SqliteType.Integer);
            var pEpisodeNumber = grpCmd.Parameters.Add("$episode_number", SqliteType.Integer);
            var pMaxHeight = grpCmd.Parameters.Add("$max_height", SqliteType.Integer);
            var pHas4k = grpCmd.Parameters.Add("$has_4k", SqliteType.Integer);
            var pMaxBitrate = grpCmd.Parameters.Add("$max_bitrate", SqliteType.Integer);
            var pAudioFormats = grpCmd.Parameters.Add("$audio_formats", SqliteType.Text);
            var pImageItemId = grpCmd.Parameters.Add("$image_item_id", SqliteType.Text);
            var pImageServerId = grpCmd.Parameters.Add("$image_server_id", SqliteType.Text);
            var pBackdropItemId = grpCmd.Parameters.Add("$backdrop_item_id", SqliteType.Text);
            var pBackdropServerId = grpCmd.Parameters.Add("$backdrop_server_id", SqliteType.Text);
            var pTmdbKey = grpCmd.Parameters.Add("$tmdb_key", SqliteType.Text);
            var pPrimaryScore = grpCmd.Parameters.Add("$primary_score", SqliteType.Integer);

            foreach (var g in dirtyGroups)
            {
                pGroupId.Value = g.GroupId;
                pItemType.Value = g.ItemType;
                pPrimaryItemId.Value = g.PrimaryItemId;
                pPrimaryServer.Value = g.PrimaryServerId;
                pIsProvisional.Value = g.IsProvisional;
                pVersionCount.Value = g.VersionCount;
                pName.Value = g.Name;
                pSortName.Value = (object?)g.SortName ?? DBNull.Value;
                pYear.Value = (object?)g.Year ?? DBNull.Value;
                pOverview.Value = (object?)g.Overview ?? DBNull.Value;
                pImageTag.Value = (object?)g.ImageTag ?? DBNull.Value;
                pBackdropTag.Value = (object?)g.BackdropTag ?? DBNull.Value;
                pCommunityRating.Value = (object?)g.CommunityRating ?? DBNull.Value;
                pDateCreated.Value = (object?)g.DateCreated ?? DBNull.Value;
                pSeriesGroupId.Value = (object?)g.SeriesGroupId ?? DBNull.Value;
                pSeasonNumber.Value = (object?)g.SeasonNumber ?? DBNull.Value;
                pEpisodeNumber.Value = (object?)g.EpisodeNumber ?? DBNull.Value;
                pMaxHeight.Value = (object?)g.MaxHeight ?? DBNull.Value;
                pHas4k.Value = g.Has4k;
                pMaxBitrate.Value = (object?)g.MaxBitrate ?? DBNull.Value;
                pAudioFormats.Value = (object?)g.AudioFormats ?? DBNull.Value;
                pImageItemId.Value = (object?)g.ImageItemId ?? DBNull.Value;
                pImageServerId.Value = (object?)g.ImageServerId ?? DBNull.Value;
                pBackdropItemId.Value = (object?)g.BackdropItemId ?? DBNull.Value;
                pBackdropServerId.Value = (object?)g.BackdropServerId ?? DBNull.Value;
                pTmdbKey.Value = (object?)g.TmdbKey ?? DBNull.Value;
                pPrimaryScore.Value = g.PrimaryScore;

                grpCmd.ExecuteNonQuery();
                g.IsDirty = false;
            }
        }

        // 5. Update cached_items group assignment
        if (itemAssignments != null && itemAssignments.Count > 0)
        {
            using var assignCmd = tx.Connection!.CreateCommand();
            assignCmd.Transaction = tx;
            assignCmd.CommandText = SqlSaveDedupBatch5;
            var pGid = assignCmd.Parameters.Add("$gid", SqliteType.Integer);
            var pGidStr = assignCmd.Parameters.Add("$gidStr", SqliteType.Text);
            var pId = assignCmd.Parameters.Add("$id", SqliteType.Text);
            var pSrv = assignCmd.Parameters.Add("$srv", SqliteType.Text);

            foreach (var (srv, id, gid) in itemAssignments)
            {
                pGid.Value = gid;
                pGidStr.Value = gid.ToString();
                pId.Value = id;
                pSrv.Value = srv;
                assignCmd.ExecuteNonQuery();
            }
        }
    }

    // The dedup working set of one sync batch (ConcurrentDedupIndex.LoadWorkingSet). These throw rather than
    // return empty: a batch deduplicated against nothing would mint duplicate groups.
    private static readonly string SqlMaxGroupId = Checked("SELECT COALESCE(MAX(group_id), 0) FROM dedup_groups;");
    private static readonly string SqlLookupDedupKey = Checked("SELECT group_id, is_authoritative FROM dedup_keys WHERE key = $key;");
    private static readonly string SqlWorkingSetGroup = Checked(@"
                    SELECT group_id, item_type, primary_item_id, primary_server, is_provisional, version_count,
                           name, sort_name, year, overview, image_tag, backdrop_tag, community_rating,
                           date_created, series_group_id, season_number, episode_number,
                           max_height, has_4k, max_bitrate, audio_formats,
                           image_item_id, image_server_id, backdrop_item_id, backdrop_server_id, tmdb_key, primary_score
                    FROM dedup_groups WHERE group_id = $gid;
                ");
    private static readonly string SqlWorkingSetKeys = Checked("SELECT key, is_authoritative FROM dedup_keys WHERE group_id = $gid;");
    private static readonly string SqlWorkingSetMembers = Checked("SELECT server_id, id, parent_index_number, index_number, type FROM cached_items WHERE group_id = $gid;");
    private static readonly string SqlCountMultiVersionGroups = Checked("SELECT COUNT(*) FROM dedup_groups WHERE version_count > 1;");

    public int GetMaxGroupId()
    {
        lock (_dbLock)
        {
            using var cmd = (_connection ?? throw new InvalidOperationException("Database is closed.")).CreateCommand();
            cmd.CommandText = SqlMaxGroupId;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // One indexed lookup per key (dedup_keys is keyed by key), then each reached group's row by primary key, its
    // keys (ix_dedup_keys_group) and its members (ix_items_group). Keys whose group row is gone are still returned.
    public (Dictionary<string, (int GroupId, bool IsAuthoritative)> Found, List<DedupGroupRecord> Groups, List<(string Key, int GroupId, bool IsAuthoritative)> Keys, List<(string ServerId, string ItemId, int GroupId, (int Season, int Episode)? Position)> Members)
        LoadDedupWorkingSet(IEnumerable<string> keys)
    {
        var found = new Dictionary<string, (int GroupId, bool IsAuthoritative)>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<DedupGroupRecord>();
        var groupKeys = new List<(string Key, int GroupId, bool IsAuthoritative)>();
        var members = new List<(string ServerId, string ItemId, int GroupId, (int Season, int Episode)? Position)>();
        lock (_dbLock)
        {
            var conn = _connection ?? throw new InvalidOperationException("Database is closed.");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SqlLookupDedupKey;
                var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
                foreach (var k in keys)
                {
                    pKey.Value = k;
                    using var r = cmd.ExecuteReader();
                    if (r.Read()) found[k] = (r.GetInt32(0), r.GetInt32(1) == 1);
                }
            }

            using var gCmd = conn.CreateCommand();
            gCmd.CommandText = SqlWorkingSetGroup;
            var pG = gCmd.Parameters.Add("$gid", SqliteType.Integer);
            using var kCmd = conn.CreateCommand();
            kCmd.CommandText = SqlWorkingSetKeys;
            var pK = kCmd.Parameters.Add("$gid", SqliteType.Integer);
            using var mCmd = conn.CreateCommand();
            mCmd.CommandText = SqlWorkingSetMembers;
            var pM = mCmd.Parameters.Add("$gid", SqliteType.Integer);
            foreach (var gid in found.Values.Select(v => v.GroupId).Distinct())
            {
                pG.Value = gid;
                using (var reader = gCmd.ExecuteReader())
                {
                    if (!reader.Read()) continue;
                    groups.Add(new DedupGroupRecord
                    {
                        GroupId = reader.GetInt32(0),
                        ItemType = reader.GetString(1),
                        PrimaryItemId = reader.GetString(2),
                        PrimaryServerId = reader.GetString(3),
                        IsProvisional = reader.GetInt32(4),
                        VersionCount = reader.GetInt32(5),
                        Name = reader.GetString(6),
                        SortName = reader.IsDBNull(7) ? null : reader.GetString(7),
                        Year = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        Overview = reader.IsDBNull(9) ? null : reader.GetString(9),
                        ImageTag = reader.IsDBNull(10) ? null : reader.GetString(10),
                        BackdropTag = reader.IsDBNull(11) ? null : reader.GetString(11),
                        CommunityRating = reader.IsDBNull(12) ? null : reader.GetFloat(12),
                        DateCreated = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                        SeriesGroupId = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                        SeasonNumber = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                        EpisodeNumber = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                        MaxHeight = reader.IsDBNull(17) ? null : reader.GetInt32(17),
                        Has4k = reader.GetInt32(18),
                        MaxBitrate = reader.IsDBNull(19) ? null : reader.GetInt64(19),
                        AudioFormats = reader.IsDBNull(20) ? null : reader.GetString(20),
                        ImageItemId = reader.IsDBNull(21) ? null : reader.GetString(21),
                        ImageServerId = reader.IsDBNull(22) ? null : reader.GetString(22),
                        BackdropItemId = reader.IsDBNull(23) ? null : reader.GetString(23),
                        BackdropServerId = reader.IsDBNull(24) ? null : reader.GetString(24),
                        TmdbKey = reader.IsDBNull(25) ? null : reader.GetString(25),
                        PrimaryScore = reader.GetInt32(26),
                        IsDirty = false
                    });
                }
                pK.Value = gid;
                using (var reader = kCmd.ExecuteReader())
                    while (reader.Read()) groupKeys.Add((reader.GetString(0), gid, reader.GetInt32(1) == 1));
                pM.Value = gid;
                using (var reader = mCmd.ExecuteReader())
                    while (reader.Read())
                    {
                        (int, int)? position = !reader.IsDBNull(2) && !reader.IsDBNull(3) && reader.GetString(4) == "Episode"
                            ? (reader.GetInt32(2), reader.GetInt32(3))
                            : null;
                        members.Add((reader.GetString(0), reader.GetString(1), gid, position));
                    }
            }
        }
        return (found, groups, groupKeys, members);
    }

    public int CountMultiVersionGroups()
    {
        lock (_dbLock)
        {
            if (_connection == null) return 0;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlCountMultiVersionGroups;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex) when (QueryFailed(ex, SqlCountMultiVersionGroups)) { return 0; }
        }
    }

    public void UpdatePlaybackProgress(string itemId, string serverId, long positionTicks, long? runtimeTicks = null)
    {
        UpdateLocalPlaybackProgress(serverId, itemId, positionTicks, runtimeTicks);
    }

    private static readonly string SqlClearResumeProgress = Checked("UPDATE cached_items SET json_data = @json WHERE id = @id AND server_id = @srv");

    // Clears the resume point without touching played state. With a serverId only that server's copy is
    // touched; item ids are per-server and collide across servers.
    public void ClearResumeProgress(string itemId, string? serverId = null)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => ClearResumeProgress(itemId, serverId))) return;
            try
            {
                using var cmd = _connection?.CreateCommand();
                if (cmd == null) return;
                cmd.CommandText = string.IsNullOrEmpty(serverId)
                    ? "SELECT id, server_id, json_data FROM cached_items WHERE id = @id"
                    : "SELECT id, server_id, json_data FROM cached_items WHERE id = @id AND server_id = @srv";
                cmd.Parameters.AddWithValue("@id", itemId);
                if (!string.IsNullOrEmpty(serverId)) cmd.Parameters.AddWithValue("@srv", serverId);
                using var reader = cmd.ExecuteReader();
                var updates = new List<(string Id, string ServerId, string NewJson)>();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    var srv = reader.GetString(1);
                    var json = reader.GetString(2);
                    var item = JsonSerializer.Deserialize<EmbyItem>(json);
                    if (item?.UserData != null)
                    {
                        item.UserData.PlaybackPositionTicks = 0;
                        updates.Add((id, srv, JsonSerializer.Serialize(item)));
                    }
                }
                reader.Close();

                foreach (var (uId, uSrv, newJson) in updates)
                {
                    using var upCmd = _connection?.CreateCommand();
                    if (upCmd == null) continue;
                    upCmd.CommandText = SqlClearResumeProgress;
                    upCmd.Parameters.AddWithValue("@json", newJson);
                    upCmd.Parameters.AddWithValue("@id", uId);
                    upCmd.Parameters.AddWithValue("@srv", uSrv);
                    upCmd.ExecuteNonQuery();
                }

                using var udCmd = _connection!.CreateCommand();
                udCmd.CommandText = string.IsNullOrEmpty(serverId)
                    ? "UPDATE user_data SET position_ticks = 0, origin = 'local', updated_locally = 1 WHERE item_id = @id"
                    : "UPDATE user_data SET position_ticks = 0, origin = 'local', updated_locally = 1 WHERE item_id = @id AND server_id = @srv";
                udCmd.Parameters.AddWithValue("@id", itemId);
                if (!string.IsNullOrEmpty(serverId)) udCmd.Parameters.AddWithValue("@srv", serverId);
                udCmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("DatabaseService", $"ClearResumeProgress failed for {itemId}: {ex.Message}");
            }
        }
    }

    private static readonly string SqlUpdatePlaybackProgress = Checked("SELECT json_data FROM continue_watching_cache WHERE id = $id;");
    private static readonly string SqlUpdatePlaybackProgress2 = Checked("UPDATE continue_watching_cache SET json_data = $json, updated_at = $ts WHERE id = $id;");
    private static readonly string SqlUpdatePlaybackProgress3 = Checked("UPDATE cached_items SET json_data = $json WHERE id = $id AND server_id = $sid;");

    public void UpdatePlaybackProgress(string itemId, string serverId, long positionTicks)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => UpdatePlaybackProgress(itemId, serverId, positionTicks))) return;
            if (_connection == null) return;
            try
            {
                // 1. Update continue_watching_cache
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = SqlUpdatePlaybackProgress;
                    cmd.Parameters.AddWithValue("$id", itemId);
                    var existing = cmd.ExecuteScalar() as string;
                    if (!string.IsNullOrEmpty(existing))
                    {
                        var it = JsonSerializer.Deserialize<EmbyItem>(existing);
                        if (it != null)
                        {
                            it.UserData ??= new UserItemData();
                            it.UserData.PlaybackPositionTicks = positionTicks;
                            it.UserData.Played = false;
                            it.UserData.LastPlayedDate = DateTime.UtcNow;

                            using var upCmd = _connection.CreateCommand();
                            upCmd.CommandText = SqlUpdatePlaybackProgress2;
                            upCmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(it));
                            upCmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                            upCmd.Parameters.AddWithValue("$id", itemId);
                            upCmd.ExecuteNonQuery();
                        }
                    }
                }

                // 2. Update cached_items
                using (var cmd2 = _connection.CreateCommand())
                {
                    // Scoped to the owning server when known: item ids collide across servers.
                    cmd2.CommandText = string.IsNullOrEmpty(serverId)
                        ? "SELECT json_data, server_id FROM cached_items WHERE id = $id;"
                        : "SELECT json_data, server_id FROM cached_items WHERE id = $id AND server_id = $sid;";
                    cmd2.Parameters.AddWithValue("$id", itemId);
                    if (!string.IsNullOrEmpty(serverId)) cmd2.Parameters.AddWithValue("$sid", serverId);
                    using var reader = cmd2.ExecuteReader();
                    var list = new List<(string sid, string json)>();
                    while (reader.Read())
                    {
                        list.Add((reader.GetString(1), reader.GetString(0)));
                    }
                    reader.Close();

                    foreach (var (sid, json) in list)
                    {
                        var it = JsonSerializer.Deserialize<EmbyItem>(json);
                        if (it != null)
                        {
                            it.UserData ??= new UserItemData();
                            it.UserData.PlaybackPositionTicks = positionTicks;
                            it.UserData.Played = false;
                            it.UserData.LastPlayedDate = DateTime.UtcNow;

                            using var upCmd = _connection.CreateCommand();
                            upCmd.CommandText = SqlUpdatePlaybackProgress3;
                            upCmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(it));
                            upCmd.Parameters.AddWithValue("$id", itemId);
                            upCmd.Parameters.AddWithValue("$sid", sid);
                            upCmd.ExecuteNonQuery();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("DatabaseService", $"UpdatePlaybackProgress failed for {itemId}: {ex.Message}");
            }
        }
    }

    private static readonly string SqlGetSyncCheckpoint = Checked("SELECT start_index, total_items, is_completed, last_synced FROM sync_checkpoints WHERE server_id = $server_id AND library_id = $library_id;");

    public SyncCheckpoint? GetSyncCheckpoint(string serverId, string libraryId)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetSyncCheckpoint;
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$library_id", libraryId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new SyncCheckpoint
                {
                    ServerId = serverId,
                    LibraryId = libraryId,
                    StartIndex = reader.GetInt32(0),
                    TotalItems = reader.GetInt32(1),
                    IsCompleted = reader.GetInt32(2) == 1,
                    LastSynced = reader.GetInt64(3)
                };
            }
            return null;
        }
    }

    private static readonly string SqlSaveSyncCheckpoint = Checked(@"
                INSERT INTO sync_checkpoints (server_id, library_id, start_index, total_items, is_completed, last_synced)
                VALUES ($server_id, $library_id, $start_index, $total_items, $is_completed, $last_synced)
                ON CONFLICT(server_id, library_id) DO UPDATE SET
                    start_index = excluded.start_index,
                    total_items = excluded.total_items,
                    is_completed = excluded.is_completed,
                    last_synced = excluded.last_synced;
            ");

    public void SaveSyncCheckpoint(string serverId, string libraryId, int startIndex, int totalItems, bool isCompleted)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlSaveSyncCheckpoint;
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$library_id", libraryId);
            cmd.Parameters.AddWithValue("$start_index", startIndex);
            cmd.Parameters.AddWithValue("$total_items", totalItems);
            cmd.Parameters.AddWithValue("$is_completed", isCompleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_synced", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    private static readonly string SqlClearSyncCheckpoints = Checked("DELETE FROM sync_checkpoints;");
    private static readonly string SqlClearSyncCheckpoints2 = Checked("DELETE FROM sync_checkpoints WHERE server_id = $server_id;");

    public void ClearSyncCheckpoints(string? serverId = null)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            if (string.IsNullOrEmpty(serverId))
            {
                cmd.CommandText = SqlClearSyncCheckpoints;
            }
            else
            {
                cmd.CommandText = SqlClearSyncCheckpoints2;
                cmd.Parameters.AddWithValue("$server_id", serverId);
            }
            cmd.ExecuteNonQuery();
        }
    }

    private static readonly string SqlGetAllCachedItemsForParent = Checked("SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE parent_id = $parent_id AND server_id = $server_id;");

    public List<EmbyItem> GetAllCachedItemsForParent(string parentId, string serverId)
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            if (_connection == null) return items;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetAllCachedItemsForParent;
            cmd.Parameters.AddWithValue("$parent_id", parentId);
            cmd.Parameters.AddWithValue("$server_id", serverId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var dedupId = reader.IsDBNull(2) ? null : reader.GetString(2);
                try
                {
                    var item = JsonSerializer.Deserialize<EmbyItem>(json);
                    if (item != null)
                    {
                        item.ServerId = sid;
                        item.DedupGroupId = dedupId;
                        items.Add(item);
                    }
                }
                catch (Exception ex) when (QueryFailed(ex, SqlGetAllCachedItemsForParent)) { }
            }
        }
        return items;
    }

    private static readonly string SqlGetAllCachedItems = Checked("SELECT json_data, server_id, dedup_group_id FROM cached_items;");
    private static readonly string SqlGetAllCachedItems2 = Checked("SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE server_id = $server_id;");

    public List<EmbyItem> GetAllCachedItems(string? serverId = null)
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            if (_connection == null) return items;
            using var cmd = _connection.CreateCommand();
            if (string.IsNullOrEmpty(serverId))
            {
                cmd.CommandText = SqlGetAllCachedItems;
            }
            else
            {
                cmd.CommandText = SqlGetAllCachedItems2;
                cmd.Parameters.AddWithValue("$server_id", serverId);
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var dedupId = reader.IsDBNull(2) ? null : reader.GetString(2);
                try
                {
                    var item = JsonSerializer.Deserialize<EmbyItem>(json);
                    if (item != null)
                    {
                        item.ServerId = sid;
                        item.DedupGroupId = dedupId;
                        items.Add(item);
                    }
                }
                catch (Exception ex) when (QueryFailed(ex, cmd.CommandText)) { }
            }
        }
        return items;
    }

    private static readonly string SqlSaveMediaStreamPreference = Checked(@"
                    INSERT INTO media_stream_preferences (media_key, audio_track_id, audio_language, subtitle_track_id, subtitle_language, updated_at)
                    VALUES ($key, $aid, $alang, $sid, $slang, $updated)
                    ON CONFLICT(media_key) DO UPDATE SET
                        audio_track_id = COALESCE($aid, audio_track_id),
                        audio_language = COALESCE($alang, audio_language),
                        subtitle_track_id = COALESCE($sid, subtitle_track_id),
                        subtitle_language = COALESCE($slang, subtitle_language),
                        updated_at = $updated;");

    public void SaveMediaStreamPreference(string mediaKey, int? audioTrackId, string? audioLang, int? subtitleTrackId, string? subtitleLang)
    {
        if (string.IsNullOrWhiteSpace(mediaKey)) return;
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => SaveMediaStreamPreference(mediaKey, audioTrackId, audioLang, subtitleTrackId, subtitleLang))) return;
            if (_connection == null) return;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlSaveMediaStreamPreference;
                cmd.Parameters.AddWithValue("$key", mediaKey);
                cmd.Parameters.AddWithValue("$aid", (object?)audioTrackId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$alang", (object?)audioLang ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sid", (object?)subtitleTrackId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$slang", (object?)subtitleLang ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("DatabaseService", $"SaveMediaStreamPreference failed for {mediaKey}: {ex.Message}");
            }
        }
    }

    private static readonly string SqlGetMediaStreamPreference = Checked("SELECT audio_track_id, audio_language, subtitle_track_id, subtitle_language FROM media_stream_preferences WHERE media_key = $key;");

    public (int? AudioTrackId, string? AudioLanguage, int? SubtitleTrackId, string? SubtitleLanguage)? GetMediaStreamPreference(string mediaKey)
    {
        if (string.IsNullOrWhiteSpace(mediaKey)) return null;
        lock (_dbLock)
        {
            if (_connection == null) return null;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetMediaStreamPreference;
                cmd.Parameters.AddWithValue("$key", mediaKey);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    int? aid = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                    string? alang = reader.IsDBNull(1) ? null : reader.GetString(1);
                    int? sid = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                    string? slang = reader.IsDBNull(3) ? null : reader.GetString(3);
                    return (aid, alang, sid, slang);
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("DatabaseService", $"GetMediaStreamPreference failed for {mediaKey}: {ex.Message}");
            }
        }
        return null;
    }

    // Prepares every registered statement (CheckedStatements) against the open schema, so a column the code
    // names but the schema lacks fails at startup with the statement, instead of on the call path.
    public void SelfCheckPreparedStatements()
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            var failures = new List<string>();
            foreach (var sql in CheckedStatements)
            {
                try
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Prepare();
                }
                catch (SqliteException ex)
                {
                    failures.Add($"{ex.Message} in: {string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}");
                }
            }
            if (failures.Count > 0)
                throw new InvalidOperationException($"{failures.Count} of {CheckedStatements.Count} statements do not prepare against this schema:\n{string.Join("\n", failures)}");
            NocturneLogger.Info("DatabaseService", $"SelfCheckPreparedStatements prepared all {CheckedStatements.Count} registered statements.");
        }
    }

    private static readonly string SqlUpdateLocalPlaybackProgress = Checked(@"
                INSERT INTO user_data (server_id, item_id, position_ticks, runtime_ticks, played, last_played_utc, origin, updated_locally)
                VALUES ($server_id, $item_id, $position_ticks, $runtime_ticks, $played, $last_played_utc, $origin, 1)
                ON CONFLICT(server_id, item_id) DO UPDATE SET
                    position_ticks = excluded.position_ticks,
                    runtime_ticks = COALESCE(excluded.runtime_ticks, user_data.runtime_ticks),
                    played = excluded.played,
                    last_played_utc = excluded.last_played_utc,
                    origin = excluded.origin,
                    updated_locally = 1;
            ");

    public void UpdateLocalPlaybackProgress(string serverId, string itemId, long positionTicks, bool played, long? runtimeTicks = null, string origin = Origin.Local)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => UpdateLocalPlaybackProgress(serverId, itemId, positionTicks, played, runtimeTicks, origin))) return;
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlUpdateLocalPlaybackProgress;
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$item_id", itemId);
            cmd.Parameters.AddWithValue("$position_ticks", positionTicks);
            cmd.Parameters.AddWithValue("$runtime_ticks", (object?)runtimeTicks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$played", played ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_played_utc", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$origin", origin);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateLocalPlaybackProgress(string serverId, string itemId, long positionTicks, long? runtimeTicks = null)
    {
        UpdateLocalPlaybackProgress(serverId, itemId, positionTicks, false, runtimeTicks);
    }

    private static readonly string SqlMarkLocalGroupPlayed = Checked(@"
                UPDATE user_data
                   SET played = 1, position_ticks = 0, last_played_utc = $now, origin = 'local', updated_locally = 1
                 WHERE (server_id, item_id) IN (
                     SELECT server_id, id FROM cached_items WHERE group_id = $group_id
                 );
            ");

    public void MarkLocalGroupPlayed(long groupId)
    {
        lock (_dbLock)
        {
            // During a rebuild write the group id may be about to change: mark the members it has now.
            if (_queuedWrites != null)
            {
                var members = GetGroupItemIds((int)groupId);
                if (QueueDuringRebuildWrite(() => MarkLocalItemsPlayed(members))) return;
            }
            if (_connection == null) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlMarkLocalGroupPlayed;
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$group_id", groupId);
            cmd.ExecuteNonQuery();
        }
    }

    private static readonly string SqlMarkLocalItemPlayed = Checked(@"
                INSERT INTO user_data (server_id, item_id, position_ticks, runtime_ticks, played, last_played_utc, origin, updated_locally)
                VALUES ($server_id, $item_id, 0, NULL, 1, $now, 'local', 1)
                ON CONFLICT(server_id, item_id) DO UPDATE SET
                    played = 1,
                    position_ticks = 0,
                    last_played_utc = excluded.last_played_utc,
                    origin = 'local',
                    updated_locally = 1;
            ");

    public void MarkLocalItemPlayed(string serverId, string itemId)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => MarkLocalItemPlayed(serverId, itemId))) return;
            if (_connection == null) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlMarkLocalItemPlayed;
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$item_id", itemId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
    }

    private static readonly string SqlUpdateUserDataState = Checked(@"
                INSERT INTO user_data (server_id, item_id, position_ticks, runtime_ticks, played, last_played_utc, origin, updated_locally)
                VALUES ($server_id, $item_id, $position_ticks, $runtime_ticks, $played, $last_played_utc, $origin, 1)
                ON CONFLICT(server_id, item_id) DO UPDATE SET
                    position_ticks = excluded.position_ticks,
                    runtime_ticks = COALESCE(excluded.runtime_ticks, user_data.runtime_ticks),
                    played = excluded.played,
                    last_played_utc = excluded.last_played_utc,
                    origin = excluded.origin,
                    updated_locally = 1;
            ");

    public void UpdateUserDataState(string serverId, string itemId, long positionTicks, long? runtimeTicks, bool played, long lastPlayedUtc, string origin = "local")
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => UpdateUserDataState(serverId, itemId, positionTicks, runtimeTicks, played, lastPlayedUtc, origin))) return;
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlUpdateUserDataState;
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$item_id", itemId);
            cmd.Parameters.AddWithValue("$position_ticks", positionTicks);
            cmd.Parameters.AddWithValue("$runtime_ticks", (object?)runtimeTicks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$played", played ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_played_utc", lastPlayedUtc);
            cmd.Parameters.AddWithValue("$origin", origin);
            cmd.ExecuteNonQuery();
        }
    }

    public void EnqueueSiblingWrite(string serverId, string itemId, long? positionTicks, bool markPlayed, string origin)
    {
        EnqueueSiblingPlaybackWrite(serverId, itemId, positionTicks, markPlayed, origin);
    }

    private static readonly string SqlGetContinueWatchingItems = Checked(@"
                SELECT g.group_id, g.item_type, g.primary_item_id, g.primary_server, g.is_provisional, g.version_count,
                       g.name, g.sort_name, g.year, g.overview, g.image_tag, g.backdrop_tag, g.community_rating,
                       g.date_created, g.series_group_id, g.season_number, g.episode_number,
                       g.max_height, g.has_4k, g.max_bitrate, g.audio_formats,
                       MAX(u.last_played_utc) AS latest_played,
                       u.position_ticks, u.runtime_ticks, u.server_id, u.item_id,
                       c.json_data
                FROM user_data u
                JOIN cached_items c ON c.id = u.item_id AND c.server_id = u.server_id
                JOIN dedup_groups g ON g.group_id = c.group_id
                WHERE u.position_ticks > 0 AND u.played = 0
                GROUP BY g.group_id
                ORDER BY latest_played DESC
                LIMIT 20;
            ");

    public List<EmbyItem> GetContinueWatchingItems()
    {
        var list = new List<EmbyItem>();
        lock (_dbLock)
        {
            if (_connection == null) return list;
            using var cmd = _connection.CreateCommand();
            // CRITICAL SQLITE INVARIANT: Exactly ONE aggregate function MAX(u.last_played_utc)
            cmd.CommandText = SqlGetContinueWatchingItems;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var gid = reader.GetInt32(0);
                var itemType = reader.GetString(1);
                var primItemId = reader.GetString(2);
                var primServerId = reader.GetString(3);
                var isProv = reader.GetInt32(4);
                var vCount = reader.GetInt32(5);
                var name = reader.GetString(6);
                var sortName = reader.IsDBNull(7) ? null : reader.GetString(7);
                var year = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8);
                var overview = reader.IsDBNull(9) ? null : reader.GetString(9);
                var imgTag = reader.IsDBNull(10) ? null : reader.GetString(10);
                var bdTag = reader.IsDBNull(11) ? null : reader.GetString(11);
                var rating = reader.IsDBNull(12) ? (float?)null : reader.GetFloat(12);
                var posTicks = reader.GetInt64(22);
                var runTicks = reader.IsDBNull(23) ? (long?)null : reader.GetInt64(23);
                var sid = reader.GetString(24);
                var itemId = reader.GetString(25);
                var json = reader.GetString(26);

                var item = JsonSerializer.Deserialize<EmbyItem>(json);
                if (item != null)
                {
                    item.ServerId = sid;
                    item.DedupGroupId = gid.ToString();
                    item.VersionCount = vCount;
                    item.UserData ??= new UserItemData();
                    item.UserData.PlaybackPositionTicks = posTicks;
                    item.UserData.Played = false;
                    list.Add(item);
                }
            }
        }
        AttachGroupArtwork(list);
        return list;
    }

    private static readonly string SqlEnqueueSiblingPlaybackWrite = Checked(@"
                INSERT INTO sibling_write_queue(server_id, item_id, position_ticks, mark_played, queued_utc)
                VALUES ($server_id, $item_id, $position_ticks, $mark_played, $queued_utc)
                ON CONFLICT(server_id, item_id) DO UPDATE SET
                    position_ticks = excluded.position_ticks,
                    mark_played = excluded.mark_played,
                    queued_utc = excluded.queued_utc,
                    attempts = 0;
            ");

    // origin: where the change came from (Origin); D7 drops the write to the server named there.
    public void EnqueueSiblingPlaybackWrite(string serverId, string itemId, long? positionTicks, bool markPlayed, string origin)
    {
        if (!Origin.WritesTo(origin, serverId)) return;
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => EnqueueSiblingPlaybackWrite(serverId, itemId, positionTicks, markPlayed, origin))) return;
            if (_connection == null) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlEnqueueSiblingPlaybackWrite;
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$item_id", itemId);
            cmd.Parameters.AddWithValue("$position_ticks", (object?)positionTicks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mark_played", markPlayed ? 1 : 0);
            cmd.Parameters.AddWithValue("$queued_utc", now);
            cmd.ExecuteNonQuery();
        }
    }

    private static readonly string SqlGetPendingSiblingWrites = Checked("SELECT queue_id, server_id, item_id, position_ticks, mark_played FROM sibling_write_queue WHERE attempts < $max_attempts ORDER BY queued_utc ASC LIMIT $limit;");

    public List<(int QueueId, string ServerId, string ItemId, long? PositionTicks, bool MarkPlayed)> GetPendingSiblingWrites(int limit = 50, int maxAttempts = MaxSiblingWriteAttempts)
    {
        var list = new List<(int, string, string, long?, bool)>();
        lock (_dbLock)
        {
            if (_connection == null) return list;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetPendingSiblingWrites;
            cmd.Parameters.AddWithValue("$max_attempts", maxAttempts);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int qid = reader.GetInt32(0);
                string sid = reader.GetString(1);
                string iid = reader.GetString(2);
                long? pos = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                bool played = reader.GetInt32(4) == 1;
                list.Add((qid, sid, iid, pos, played));
            }
        }
        return list;
    }

    // Rows that keep failing stay in the table for diagnosis but stop blocking the head of the queue.
    public const int MaxSiblingWriteAttempts = 10;

    private static readonly string SqlRecordSiblingWriteFailure = Checked("UPDATE sibling_write_queue SET attempts = attempts + 1 WHERE queue_id = $qid;");

    public void RecordSiblingWriteFailure(int queueId)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => RecordSiblingWriteFailure(queueId))) return;
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlRecordSiblingWriteFailure;
            cmd.Parameters.AddWithValue("$qid", queueId);
            cmd.ExecuteNonQuery();
        }
    }

    private static readonly string SqlGetMigrationState = Checked("SELECT value FROM migration_state WHERE key = $key;");

    public string? GetMigrationState(string key)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetMigrationState;
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    private static readonly string SqlSetMigrationState = Checked("INSERT INTO migration_state (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;");

    public void SetMigrationState(string key, string value)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => SetMigrationState(key, value))) return;
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlSetMigrationState;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
    }

    // Episode groups in which one server holds two items under different (season, episode) that share no episode
    // id: a season/episode match the server's own numbering contradicts (an untagged S02E10 beside the S02E04 that
    // server tags with the group's id). Servers numbering one episode differently share its id and pass, and so do
    // the copies one server keeps under several numberings. Ids come from the item's JSON, provider names in any
    // case, as the dedup engine reads them (the imdb_id column is stale for rows cached before that fix). Same
    // query as the [gate] row of scripts/dedup-baseline.sql. The second count is the broader row above it (items
    // spanning more than one (season, episode) without all sharing one episode id), which item 6 alerts on.
    private static readonly string SqlCountUnsharedRenumberedGroups = Checked(@"
                    WITH spans AS (
                      SELECT group_id FROM cached_items
                      WHERE type = 'Episode' AND group_id IS NOT NULL
                      GROUP BY group_id
                      HAVING COUNT(DISTINCT COALESCE(parent_index_number, -1) || ':' || COALESCE(index_number, -1)) > 1),
                    raw AS (
                      SELECT c.group_id g, c.server_id srv, c.id, COALESCE(c.parent_index_number, -1) s, COALESCE(c.index_number, -1) e,
                        (SELECT value FROM json_each(c.json_data, '$.ProviderIds') WHERE lower(key) IN ('tvdb', 'thetvdb') AND value != '' LIMIT 1) tvdb,
                        (SELECT value FROM json_each(c.json_data, '$.ProviderIds') WHERE lower(key) IN ('tmdb', 'themoviedb') AND value != '' LIMIT 1) tmdb,
                        (SELECT value FROM json_each(c.json_data, '$.ProviderIds') WHERE lower(key) = 'imdb' AND value != '' LIMIT 1) imdb
                      FROM cached_items c JOIN spans ON spans.group_id = c.group_id),
                    ep AS (
                      SELECT g, srv, id, s, e,
                        CASE WHEN tvdb GLOB '[0-9]*' AND tvdb NOT GLOB '*[^0-9]*' THEN tvdb END tvdb,
                        CASE WHEN tmdb GLOB '[0-9]*' AND tmdb NOT GLOB '*[^0-9]*' THEN tmdb END tmdb,
                        CASE WHEN imdb GLOB 'tt[0-9]*' AND substr(imdb, 3) NOT GLOB '*[^0-9]*' THEN imdb END imdb
                      FROM raw),
                    per AS (
                      SELECT COUNT(*) n, COUNT(tvdb) n_tvdb, COUNT(DISTINCT tvdb) d_tvdb, COUNT(tmdb) n_tmdb,
                             COUNT(DISTINCT tmdb) d_tmdb, COUNT(imdb) n_imdb, COUNT(DISTINCT imdb) d_imdb
                      FROM ep GROUP BY g)
                    SELECT
                      (SELECT COUNT(DISTINCT a.g) FROM ep a JOIN ep b
                         ON b.g = a.g AND b.srv = a.srv AND a.id < b.id AND (a.s != b.s OR a.e != b.e)
                       WHERE NOT ((a.tvdb = b.tvdb OR a.tmdb = b.tmdb OR a.imdb = b.imdb) IS TRUE)),
                      (SELECT COUNT(*) FROM per
                       WHERE NOT ((n_tvdb = n AND d_tvdb = 1) OR (n_tmdb = n AND d_tmdb = 1) OR (n_imdb = n AND d_imdb = 1)));
                ");

    // Own read-only connection, like the index load: the scan takes seconds on a large library and must not hold
    // _dbLock. Null when the query fails.
    public (int Gate, int Spanning)? CountUnsharedRenumberedGroups()
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SqlCountUnsharedRenumberedGroups;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0), reader.GetInt32(1));
        }
        catch (Exception ex) when (QueryFailed(ex, SqlCountUnsharedRenumberedGroups))
        {
            return null;
        }
    }

    // A merge the conflict guard (C4) refused leaves two traces: the key the item shared with the group it conflicted
    // with is demoted there (still owned, no longer authoritative), and the item's own group holds it as evidence,
    // "{key}@{group}". Demotion happens nowhere else; a range seek on the key finds the evidence.
    private static readonly string SqlConflictSplitPairs = Checked(@"
                SELECT DISTINCT k.group_id, e.group_id FROM dedup_keys k
                JOIN dedup_keys e ON e.key >= k.key || '@' AND e.key < k.key || 'A'
                WHERE k.is_authoritative = 0 AND instr(k.key, '@') = 0 AND e.group_id != k.group_id
                  AND (k.key GLOB 'tmdb:*' OR k.key GLOB 'imdb:*' OR k.key GLOB 'tvdb:*' OR k.key GLOB 'ep:tvdb:*' OR k.key GLOB 'ep:tmdb:*' OR k.key GLOB 'ep:imdb:*');
            ");
    private static readonly string SqlConflictSplitKeys = Checked("SELECT key FROM dedup_keys WHERE group_id = $gid;");
    private static readonly string SqlConflictSplitGroup = Checked(@"
                SELECT g.item_type, g.name, g.year, group_concat(DISTINCT c.server_id), MAX(c.series_name), g.season_number, g.episode_number,
                       (SELECT MIN(s.group_id) FROM cached_items e JOIN cached_items s ON s.id = e.series_id AND s.server_id = e.server_id
                         WHERE e.group_id = g.group_id)
                FROM dedup_groups g JOIN cached_items c ON c.group_id = g.group_id WHERE g.group_id = $gid GROUP BY g.group_id;
            ");

    // One pair of groups the conflict guard split. An episode side carries its series' group (through its own Series
    // row on the same server), by which RollUpConflicts finds the root cause.
    public sealed record ConflictPair(int GroupA, int GroupB, string ItemType, string TitleA, string TitleB, int? YearA, int? YearB,
        string Provider, string IdsA, string ServersA, string IdsB, string ServersB, string? Series, int? SeriesA, int? SeriesB);

    // #21 item 2: the groups the conflict guard split, with the ids they disagree on. Own read-only connection: the key
    // scan takes seconds. Pairs whose ids no longer disagree (a member since removed) are left out. Null on failure.
    public List<ConflictPair>? GetConflictPairs()
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            conn.Open();
            var pairs = new List<(int, int)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SqlConflictSplitPairs;
                using var r = cmd.ExecuteReader();
                while (r.Read()) pairs.Add((r.GetInt32(0), r.GetInt32(1)));
            }

            using var keys = conn.CreateCommand();
            keys.CommandText = SqlConflictSplitKeys;
            var pKeys = keys.Parameters.Add("$gid", SqliteType.Integer);
            using var group = conn.CreateCommand();
            group.CommandText = SqlConflictSplitGroup;
            var pGroup = group.Parameters.Add("$gid", SqliteType.Integer);
            Dictionary<string, HashSet<string>> IdsOf(int gid)
            {
                pKeys.Value = gid;
                var list = new List<string>();
                using var r = keys.ExecuteReader();
                while (r.Read()) list.Add(r.GetString(0));
                return ConcurrentDedupIndex.IdsOfKeys(list);
            }
            (string Type, string Title, string? Series, int? Year, string Servers, int? SeriesGroup)? GroupOf(int gid)
            {
                pGroup.Value = gid;
                using var r = group.ExecuteReader();
                if (!r.Read()) return null;
                var series = r.IsDBNull(4) ? null : r.GetString(4);
                var title = r.GetString(0) == "Episode" ? $"{series} S{(r.IsDBNull(5) ? 0 : r.GetInt32(5)):D2}E{(r.IsDBNull(6) ? 0 : r.GetInt32(6)):D2} {r.GetString(1)}" : r.GetString(1);
                return (r.GetString(0), title, series, r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3), r.IsDBNull(7) ? null : r.GetInt32(7));
            }

            var result = new List<ConflictPair>();
            foreach (var (ga, gb) in pairs.Select(p => (Math.Min(p.Item1, p.Item2), Math.Max(p.Item1, p.Item2))).Distinct())
            {
                var (a, b) = (IdsOf(ga), IdsOf(gb));
                if (ConcurrentDedupIndex.FindConflict(a, b) is not { } type || GroupOf(ga) is not { } ra || GroupOf(gb) is not { } rb) continue;
                result.Add(new ConflictPair(ga, gb, ra.Type, ra.Title, rb.Title, ra.Year, rb.Year, type,
                    string.Join(", ", a[type].Order()), ra.Servers, string.Join(", ", b[type].Order()), rb.Servers,
                    ra.Series ?? rb.Series, ra.SeriesGroup, rb.SeriesGroup));
            }
            return result;
        }
        catch (Exception ex) when (QueryFailed(ex, SqlConflictSplitPairs))
        {
            return null;
        }
    }

    // A row to fix on the server. Episodes: the episode pairs it accounts for (0 for a movie). A series row shows the
    // series' own conflicting ids when it has them (its Series rows split, or its season/episode keys, whose ids are
    // the series'); otherwise one episode pair's, as an example.
    public sealed record ConflictRoot(string ItemType, string Title, int? Year, string Provider, string IdsA, string ServersA, string IdsB, string ServersB, int Episodes, bool SeriesIds)
    {
        public string? TruncatedOn { get; init; } // the servers holding the shorter id, when one is a strict prefix of the other
    }

    // #23: one id a strict prefix of the other (TMDb 43 vs 43982; doubled, 568012 vs 568012568012) reads as a damaged id
    // on the server holding the shorter one (Raptor's API returns them so). Diagnostic only: the matcher never treats a
    // prefix as a match, as short ids are valid ids and prefix collisions across 170K titles are not rare.
    public static string? TruncatedOn(string idsA, string serversA, string idsB, string serversB)
    {
        if (idsA.Contains(',') || idsB.Contains(',') || idsA.Length == idsB.Length) return null;
        var (shortId, longId, servers) = idsA.Length < idsB.Length ? (idsA, idsB, serversA) : (idsB, idsA, serversB);
        return longId.StartsWith(shortId, StringComparison.OrdinalIgnoreCase) ? servers : null;
    }

    // #22 item 1: by root cause. Most pairs are episodes split because their series carries a conflicting id; the
    // series is what gets fixed, so a series and every episode pair under it (keyed by the series' groups on each
    // side) make one row.
    public static List<ConflictRoot> RollUpConflicts(IEnumerable<ConflictPair> pairs)
    {
        var roots = new List<ConflictRoot>();
        foreach (var p in pairs.Where(p => p.ItemType == "Movie"))
            roots.Add(new ConflictRoot("Movie", p.TitleA, p.YearA ?? p.YearB, p.Provider, p.IdsA, p.ServersA, p.IdsB, p.ServersB, 0, false)
                { TruncatedOn = TruncatedOn(p.IdsA, p.ServersA, p.IdsB, p.ServersB) });

        (int, int) SeriesKey(ConflictPair p) => p.ItemType == "Series"
            ? (p.GroupA, p.GroupB)
            : (Math.Min(p.SeriesA ?? -p.GroupA, p.SeriesB ?? -p.GroupB), Math.Max(p.SeriesA ?? -p.GroupA, p.SeriesB ?? -p.GroupB));
        foreach (var series in pairs.Where(p => p.ItemType != "Movie").GroupBy(SeriesKey))
        {
            var own = series.FirstOrDefault(p => p.ItemType == "Series") ?? series.FirstOrDefault(p => p.Provider.StartsWith("ep:"));
            var shown = own ?? series.First();
            var title = shown.ItemType == "Series" ? shown.TitleA : shown.Series ?? shown.TitleA;
            roots.Add(new ConflictRoot("Series", title, shown.ItemType == "Series" ? shown.YearA ?? shown.YearB : null, shown.Provider,
                shown.IdsA, shown.ServersA, shown.IdsB, shown.ServersB, series.Count(p => p.ItemType == "Episode"), own != null)
                { TruncatedOn = TruncatedOn(shown.IdsA, shown.ServersA, shown.IdsB, shown.ServersB) });
        }
        // Damaged ids first: each is one re-identify on the server that holds it.
        return roots.OrderBy(r => r.TruncatedOn == null ? 1 : 0).ThenBy(r => r.ItemType == "Movie" ? 1 : 0)
            .ThenByDescending(r => r.Episodes).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly string SqlRemoveSiblingWrite = Checked("DELETE FROM sibling_write_queue WHERE queue_id = $qid;");

    public void RemoveSiblingWrite(int queueId)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => RemoveSiblingWrite(queueId))) return;
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlRemoveSiblingWrite;
            cmd.Parameters.AddWithValue("$qid", queueId);
            cmd.ExecuteNonQuery();
        }
    }

    public void ExecuteSql(string sql)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private static readonly string SqlGetCachedItemsChunk = Checked("SELECT rowid, json_data, server_id, group_id FROM cached_items NOT INDEXED WHERE rowid > $after AND type IN ('Movie', 'Series', 'Episode') ORDER BY rowid LIMIT $limit;");

    // Keyset paging: the caller passes the last rowid it saw. LIMIT/OFFSET re-read and re-sorted every
    // preceding row's JSON per chunk (26 s per chunk at offset 600k on the live DB; the rebuild never finished).
    // NOT INDEXED walks the rowid b-tree in order instead of sorting the type index's matches.
    public (List<EmbyItem> Items, long LastRowId) GetCachedItemsChunk(long afterRowId, int limit)
    {
        var items = new List<EmbyItem>();
        long lastRowId = afterRowId;
        lock (_dbLock)
        {
            if (_connection == null) return (items, lastRowId);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetCachedItemsChunk;
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$after", afterRowId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                lastRowId = reader.GetInt64(0);
                var item = JsonSerializer.Deserialize<EmbyItem>(reader.GetString(1));
                if (item != null)
                {
                    item.ServerId = reader.GetString(2);
                    item.DedupGroupId = reader.IsDBNull(3) ? null : reader.GetInt64(3).ToString(); // its group now: the rebuild keeps ids where it can
                    items.Add(item);
                }
            }
        }
        return (items, lastRowId);
    }

    // #22 item 5: a rebuilt group keeps the id most of its members had (unless another group took it first), so the swap
    // rewrites only the rows whose group changed. Every rebuild renumbered from 1, so one change early in the library
    // shifted every later id: a repeat rebuild of the live copy rewrote nearly all 1.67 M rows (4.5 GB of WAL, 245 s).
    private static Dictionary<int, int> StableGroupIds(List<DedupGroupRecord> groups, IReadOnlyDictionary<(string, string), int> previous)
    {
        var map = new Dictionary<int, int>(groups.Count);
        var taken = new HashSet<int>();
        int next = Math.Max(previous.Count > 0 ? previous.Values.Max() : 0, groups.Count > 0 ? groups.Max(g => g.GroupId) : 0) + 1;
        foreach (var g in groups.OrderBy(g => g.GroupId))
        {
            var id = g.ItemIds.Keys.Select(m => previous.TryGetValue(m, out var old) ? old : 0).Where(old => old > 0)
                .GroupBy(old => old).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Select(x => x.Key)
                .FirstOrDefault(old => !taken.Contains(old));
            if (id == 0) id = next++;
            taken.Add(id);
            map[g.GroupId] = id;
        }
        return map;
    }

    public void SaveRebuildDedupState(ConcurrentDedupIndex index, IReadOnlyDictionary<(string, string), int>? previous = null)
    {
        var (groups, keys, items) = index.GetRebuildState();
        var map = StableGroupIds(groups, previous ?? new Dictionary<(string, string), int>());
        // An evidence key names its group: "{key}@{group}".
        string Key(string key)
        {
            var at = key.LastIndexOf('@');
            return at > 0 && int.TryParse(key.AsSpan(at + 1), out var gid) && map.TryGetValue(gid, out var to) ? $"{key[..at]}@{to}" : key;
        }
        int Id(int gid) => map.TryGetValue(gid, out var to) ? to : gid;
        var oldMax = previous is { Count: > 0 } ? previous.Values.Max() : 0; // fresh ids start above it
        NocturneLogger.Info("DatabaseService", $"Rebuild keeps the previous id of {groups.Count(g => Id(g.GroupId) <= oldMax)} of {groups.Count} groups.");

        // Only the *_rebuild tables, which nothing else reads: on its own connection, so UI reads go on (#20 item 4). In
        // transactions of 50,000 rows, so the WAL recycles between them (one transaction grew it by about 1 GB); an
        // interrupted write leaves tables the next rebuild drops first.
        WithWritesQueued("rebuild state write", conn =>
        {
            var tx = conn.BeginTransaction();
            var commands = new List<SqliteCommand>();
            int rows = 0;
            void Step(SqliteCommand cmd)
            {
                cmd.ExecuteNonQuery();
                if (++rows % 50_000 != 0) return;
                tx.Commit();
                tx.Dispose();
                tx = conn.BeginTransaction();
                foreach (var c in commands) c.Transaction = tx;
            }
            try
            {
                // 1. dedup_groups_rebuild
                using var grpCmd = conn.CreateCommand();
                commands.Add(grpCmd);
                grpCmd.Transaction = tx;
                grpCmd.CommandText = @"
                    INSERT INTO dedup_groups_rebuild (
                        group_id, item_type, primary_item_id, primary_server, is_provisional, version_count,
                        name, sort_name, year, overview, image_tag, backdrop_tag, community_rating,
                        date_created, series_group_id, season_number, episode_number,
                        max_height, has_4k, max_bitrate, audio_formats,
                        image_item_id, image_server_id, backdrop_item_id, backdrop_server_id, tmdb_key, primary_score
                    ) VALUES (
                        $group_id, $item_type, $primary_item_id, $primary_server, $is_provisional, $version_count,
                        $name, $sort_name, $year, $overview, $image_tag, $backdrop_tag, $community_rating,
                        $date_created, $series_group_id, $season_number, $episode_number,
                        $max_height, $has_4k, $max_bitrate, $audio_formats,
                        $image_item_id, $image_server_id, $backdrop_item_id, $backdrop_server_id, $tmdb_key, $primary_score
                    );
                ";
                var pGroupId = grpCmd.Parameters.Add("$group_id", SqliteType.Integer);
                var pItemType = grpCmd.Parameters.Add("$item_type", SqliteType.Text);
                var pPrimaryItemId = grpCmd.Parameters.Add("$primary_item_id", SqliteType.Text);
                var pPrimaryServer = grpCmd.Parameters.Add("$primary_server", SqliteType.Text);
                var pIsProvisional = grpCmd.Parameters.Add("$is_provisional", SqliteType.Integer);
                var pVersionCount = grpCmd.Parameters.Add("$version_count", SqliteType.Integer);
                var pName = grpCmd.Parameters.Add("$name", SqliteType.Text);
                var pSortName = grpCmd.Parameters.Add("$sort_name", SqliteType.Text);
                var pYear = grpCmd.Parameters.Add("$year", SqliteType.Integer);
                var pOverview = grpCmd.Parameters.Add("$overview", SqliteType.Text);
                var pImageTag = grpCmd.Parameters.Add("$image_tag", SqliteType.Text);
                var pBackdropTag = grpCmd.Parameters.Add("$backdrop_tag", SqliteType.Text);
                var pCommunityRating = grpCmd.Parameters.Add("$community_rating", SqliteType.Real);
                var pDateCreated = grpCmd.Parameters.Add("$date_created", SqliteType.Integer);
                var pSeriesGroupId = grpCmd.Parameters.Add("$series_group_id", SqliteType.Integer);
                var pSeasonNumber = grpCmd.Parameters.Add("$season_number", SqliteType.Integer);
                var pEpisodeNumber = grpCmd.Parameters.Add("$episode_number", SqliteType.Integer);
                var pMaxHeight = grpCmd.Parameters.Add("$max_height", SqliteType.Integer);
                var pHas4k = grpCmd.Parameters.Add("$has_4k", SqliteType.Integer);
                var pMaxBitrate = grpCmd.Parameters.Add("$max_bitrate", SqliteType.Integer);
                var pAudioFormats = grpCmd.Parameters.Add("$audio_formats", SqliteType.Text);
                var pImageItemId = grpCmd.Parameters.Add("$image_item_id", SqliteType.Text);
                var pImageServerId = grpCmd.Parameters.Add("$image_server_id", SqliteType.Text);
                var pBackdropItemId = grpCmd.Parameters.Add("$backdrop_item_id", SqliteType.Text);
                var pBackdropServerId = grpCmd.Parameters.Add("$backdrop_server_id", SqliteType.Text);
                var pTmdbKey = grpCmd.Parameters.Add("$tmdb_key", SqliteType.Text);
                var pPrimaryScore = grpCmd.Parameters.Add("$primary_score", SqliteType.Integer);

                foreach (var g in groups)
                {
                    pGroupId.Value = Id(g.GroupId);
                    pItemType.Value = g.ItemType;
                    pPrimaryItemId.Value = g.PrimaryItemId;
                    pPrimaryServer.Value = g.PrimaryServerId;
                    pIsProvisional.Value = g.IsProvisional;
                    pVersionCount.Value = g.VersionCount;
                    pName.Value = g.Name;
                    pSortName.Value = (object?)g.SortName ?? DBNull.Value;
                    pYear.Value = (object?)g.Year ?? DBNull.Value;
                    pOverview.Value = (object?)g.Overview ?? DBNull.Value;
                    pImageTag.Value = (object?)g.ImageTag ?? DBNull.Value;
                    pBackdropTag.Value = (object?)g.BackdropTag ?? DBNull.Value;
                    pCommunityRating.Value = (object?)g.CommunityRating ?? DBNull.Value;
                    pDateCreated.Value = (object?)g.DateCreated ?? DBNull.Value;
                    pSeriesGroupId.Value = (object?)g.SeriesGroupId ?? DBNull.Value;
                    pSeasonNumber.Value = (object?)g.SeasonNumber ?? DBNull.Value;
                    pEpisodeNumber.Value = (object?)g.EpisodeNumber ?? DBNull.Value;
                    pMaxHeight.Value = (object?)g.MaxHeight ?? DBNull.Value;
                    pHas4k.Value = g.Has4k;
                    pMaxBitrate.Value = (object?)g.MaxBitrate ?? DBNull.Value;
                    pAudioFormats.Value = (object?)g.AudioFormats ?? DBNull.Value;
                    pImageItemId.Value = (object?)g.ImageItemId ?? DBNull.Value;
                    pImageServerId.Value = (object?)g.ImageServerId ?? DBNull.Value;
                    pBackdropItemId.Value = (object?)g.BackdropItemId ?? DBNull.Value;
                    pBackdropServerId.Value = (object?)g.BackdropServerId ?? DBNull.Value;
                    pTmdbKey.Value = (object?)g.TmdbKey ?? DBNull.Value;
                    pPrimaryScore.Value = g.PrimaryScore;
                    Step(grpCmd);
                }

                // 2. dedup_keys_rebuild
                using var keyCmd = conn.CreateCommand();
                commands.Add(keyCmd);
                keyCmd.Transaction = tx;
                keyCmd.CommandText = "INSERT INTO dedup_keys_rebuild (key, group_id, is_authoritative) VALUES ($k, $g, $a);";
                var pkK = keyCmd.Parameters.Add("$k", SqliteType.Text);
                var pkG = keyCmd.Parameters.Add("$g", SqliteType.Integer);
                var pkA = keyCmd.Parameters.Add("$a", SqliteType.Integer);
                // In key order: these B-trees are keyed by text, and 5 M keys inserted at random touched a random page each,
                // about 200 MB of WAL a transaction.
                foreach (var (k, gid, isAuth) in keys.Select(x => (Key: Key(x.Key), x.GroupId, x.IsAuthoritative)).OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    pkK.Value = k;
                    pkG.Value = Id(gid);
                    pkA.Value = isAuth ? 1 : 0;
                    Step(keyCmd);
                }

                // 3. item_group_rebuild
                using var mapCmd = conn.CreateCommand();
                commands.Add(mapCmd);
                mapCmd.Transaction = tx;
                mapCmd.CommandText = "INSERT INTO item_group_rebuild (id, server_id, group_id) VALUES ($id, $srv, $gid);";
                var pmId = mapCmd.Parameters.Add("$id", SqliteType.Text);
                var pmSrv = mapCmd.Parameters.Add("$srv", SqliteType.Text);
                var pmGid = mapCmd.Parameters.Add("$gid", SqliteType.Integer);
                foreach (var (srv, id, gid) in items.OrderBy(x => x.ItemId, StringComparer.Ordinal).ThenBy(x => x.ServerId, StringComparer.Ordinal))
                {
                    pmId.Value = id;
                    pmSrv.Value = srv;
                    pmGid.Value = Id(gid);
                    Step(mapCmd);
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                NocturneLogger.Error("DatabaseService", "Failed to save rebuild dedup state", ex);
                throw;
            }
            finally
            {
                tx.Dispose();
            }
        });
    }

    private static readonly string SqlGetItemsWithNullGroupId = Checked("SELECT json_data, server_id FROM cached_items WHERE group_id IS NULL AND type IN ('Movie', 'Series', 'Episode');");

    public List<EmbyItem> GetItemsWithNullGroupId()
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            if (_connection == null) return items;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetItemsWithNullGroupId;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var item = JsonSerializer.Deserialize<EmbyItem>(json);
                if (item != null)
                {
                    item.ServerId = sid;
                    items.Add(item);
                }
            }
        }
        return items;
    }

    // The sync's end pass: untagged episodes held in their own group during the sync (ConcurrentDedupIndex.HeldGroup),
    // plus any Movie/Series/Episode row an interrupted sync left ungrouped, in the order they were first written.
    private static readonly string SqlGetItemsToPlace = Checked(@"
                SELECT c.json_data, c.server_id FROM cached_items c
                WHERE c.type IN ('Movie', 'Series', 'Episode')
                  AND (c.group_id IS NULL OR c.group_id IN (SELECT group_id FROM dedup_groups WHERE is_provisional = 2))
                ORDER BY c.rowid;
            ");
    private static readonly string SqlDeleteEmptyHeldGroups = Checked(@"
                DELETE FROM dedup_groups
                WHERE is_provisional = 2 AND NOT EXISTS (SELECT 1 FROM cached_items c WHERE c.group_id = dedup_groups.group_id);
            ");

    public List<EmbyItem> GetItemsToPlace()
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            if (_connection == null) return items;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetItemsToPlace;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var item = JsonSerializer.Deserialize<EmbyItem>(reader.GetString(0));
                if (item == null) continue;
                item.ServerId = reader.GetString(1);
                items.Add(item);
            }
        }
        return items;
    }

    public int DeleteEmptyHeldGroups()
    {
        lock (_dbLock)
        {
            if (_connection == null) return 0;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlDeleteEmptyHeldGroups;
            return cmd.ExecuteNonQuery();
        }
    }

    // "+server_id": by the parent index; the planner otherwise picks ix_items_server_sync and walks every row of the
    // server (25 s a library on the live DB).
    private static readonly string SqlGetLibraryItemIds = Checked("SELECT id FROM cached_items WHERE +server_id = $srv AND parent_id = $lib;");

    public List<string> GetLibraryItemIds(string serverId, string libraryId)
    {
        var ids = new List<string>();
        lock (_dbLock)
        {
            if (_connection == null) return ids;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGetLibraryItemIds;
            cmd.Parameters.AddWithValue("$srv", serverId);
            cmd.Parameters.AddWithValue("$lib", libraryId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        return ids;
    }

    private static readonly string SqlRemoveDeletedItem = Checked(@"
                DELETE FROM cached_items WHERE id = $id AND server_id = $srv;
                DELETE FROM user_data WHERE item_id = $id AND server_id = $srv;
                DELETE FROM continue_watching_cache WHERE id = $id AND server_id = $srv;
                DELETE FROM sibling_write_queue WHERE item_id = $id AND server_id = $srv;
                DELETE FROM watchlist_entries WHERE source = $srv AND ref = $id;
                DELETE FROM dedup_keys WHERE key = 'item:' || $srv || ':' || $id;
            ");
    private static readonly string SqlDropTraktEvents = Checked("DELETE FROM trakt_queue WHERE group_id = $gid;");

    // The plan's §5 delete lifecycle, for items the server no longer has (SyncEngine.RunDeletionSweepAsync): the rows
    // go with everything keyed by them; a group left empty goes with its keys and queued Trakt events; a group with
    // members left recounts, and passes the primary, poster and backdrop on if the removed item held them.
    // ponytail: provider keys the removed item brought stay with its group until the next rebuild.
    public int RemoveDeletedItems(string serverId, IReadOnlyCollection<string> itemIds, IList<string>? serverPriority = null)
    {
        lock (_dbLock)
        {
            if (_connection == null || itemIds.Count == 0) return 0;
            var gone = itemIds.ToHashSet();
            var dirty = new List<DedupGroupRecord>();
            var dropped = new List<int>();
            foreach (var g in itemIds.Select(id => GroupIdOfItem(serverId, id)).OfType<long>().Distinct().Select(GetDedupGroup).OfType<DedupGroupRecord>())
            {
                var left = GetGroupVersions(g.GroupId.ToString()).Where(m => m.ServerId != serverId || !gone.Contains(m.Id)).ToList();
                if (left.Count == 0) { dropped.Add(g.GroupId); continue; }
                RecountAfterRemoval(g, left, serverPriority);
                dirty.Add(g);
            }

            using var tx = _connection.BeginTransaction();
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = SqlRemoveDeletedItem;
                    cmd.Parameters.AddWithValue("$srv", serverId);
                    var pId = cmd.Parameters.Add("$id", SqliteType.Text);
                    foreach (var id in itemIds)
                    {
                        pId.Value = id;
                        cmd.ExecuteNonQuery();
                    }
                }
                ApplyDedupBatch(tx, dirty, dropped, new(), new(), null);
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = SqlDropTraktEvents;
                    var pGid = cmd.Parameters.Add("$gid", SqliteType.Integer);
                    foreach (var gid in dropped)
                    {
                        pGid.Value = gid;
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                NocturneLogger.Error("DatabaseService", $"Failed to remove {itemIds.Count} deleted item(s) of {serverId}", ex);
                throw;
            }
            NocturneLogger.Info("DatabaseService", $"Removed {itemIds.Count} deleted item(s) of {serverId}: {dropped.Count} group(s) dropped, {dirty.Count} recounted.");
            return itemIds.Count;
        }
    }

    // By ProcessBatch's rules: the highest-scoring member is primary (ElectPrimary), and a poster or backdrop
    // comes from the highest-priority server's member, then the lowest "server:id".
    private static void RecountAfterRemoval(DedupGroupRecord g, List<EmbyItem> left, IList<string>? priority)
    {
        bool Has(EmbyItem m, string? srv, string? id) => m.ServerId == srv && m.Id == id;

        g.VersionCount = left.Count;
        g.MaxHeight = left.Max(m => m.Height ?? 0);
        g.MaxBitrate = left.Max(m => m.MeasuredBitrate ?? 0);
        g.Has4k = left.Any(m => (m.Height ?? 0) >= 2160 || (m.Width ?? 0) >= 3800) ? 1 : 0;
        var audio = left.Select(m => m.AudioCodec).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        g.AudioFormats = audio.Count > 0 ? string.Join(",", audio) : null;

        if (!left.Any(m => Has(m, g.PrimaryServerId, g.PrimaryItemId)))
        {
            var best = ConcurrentDedupIndex.ElectPrimary(left, priority);
            ConcurrentDedupIndex.UpdatePrimary(g, best, DedupEngine.ScorePrimaryItem(best, priority));
        }
        if (!left.Any(m => Has(m, g.ImageServerId, g.ImageItemId)) || !left.Any(m => Has(m, g.BackdropServerId, g.BackdropItemId)))
            ConcurrentDedupIndex.ElectArtwork(g, left, priority);
    }

    // Non-null while one of the rebuild's long write transactions runs on its own connection (WithWritesQueued):
    // writes on _connection wait here instead of failing with SQLITE_BUSY after busy_timeout. Checked under _dbLock,
    // which the window takes to open, so no write can be mid-statement when it starts.
    // Bounded by time, not size: a UI write a few seconds apart at most (playback progress) for the minutes a window
    // stays open. In memory only, so Dispose ends an open window and applies them before the connection closes (#21
    // item 4); a crash loses them, as it loses any write in flight.
    private List<Action>? _queuedWrites;
    private readonly ManualResetEventSlim _windowClosed = new(true);
    private volatile bool _abortWindow;
    internal Action<SqliteConnection>? SwapHeldForTest;

    // Runs work on a connection of its own with writes queued, then applies them in order under _dbLock before any
    // newer write. For the rebuild's state write and swap, which held _dbLock, and so every UI read, for minutes
    // (#20 item 4). The WAL is checkpointed inside the window: a first rebuild rewrites every row, and the next UI
    // write's auto-checkpoint copied those gigabytes back under _dbLock (93 s, every read waiting behind it).
    private void WithWritesQueued(string what, Action<SqliteConnection> work)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            _queuedWrites = new List<Action>();
            _windowClosed.Reset();
        }
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString).ToString());
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA cache_size = -262144; PRAGMA busy_timeout = 5000; PRAGMA journal_size_limit = 67108864;";
                pragma.ExecuteNonQuery();
            }
            // Dispose aborts the running statement through this (its transaction rolls back). Not sqlite3_interrupt:
            // its flag also fails the next prepare, so the ROLLBACK on close failed and the write lock stayed held.
            SQLitePCL.raw.sqlite3_progress_handler(conn.Handle, 1000, _ => _abortWindow ? 1 : 0, null);
            work(conn);
            if (!_abortWindow)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var wal = File.Exists(_dbPath + "-wal") ? new FileInfo(_dbPath + "-wal").Length / 1048576 : 0;
                using var checkpoint = conn.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; // waits out readers on the pre-swap snapshot
                using var r = checkpoint.ExecuteReader();
                r.Read();
                NocturneLogger.Info("DatabaseService", $"WAL checkpoint after the {what}: {wal} MB, {(r.GetInt32(0) == 0 ? "complete" : "partial (readers)")} in {clock.ElapsedMilliseconds} ms.");
            }
        }
        finally
        {
            lock (_dbLock)
            {
                var queued = _queuedWrites ?? new List<Action>();
                _queuedWrites = null;
                foreach (var write in queued)
                {
                    try { write(); }
                    catch (Exception ex) { NocturneLogger.Error("DatabaseService", $"A write made during the {what} failed when applied", ex); }
                }
                if (queued.Count > 0) NocturneLogger.Info("DatabaseService", $"Applied {queued.Count} write(s) made during the {what}.");
            }
            _windowClosed.Set();
        }
    }

    private bool QueueDuringRebuildWrite(Action write)
    {
        lock (_dbLock)
        {
            if (_queuedWrites == null) return false;
            _queuedWrites.Add(write);
            return true;
        }
    }

    private static readonly string SqlMarkLocalItemsPlayed = Checked(@"
                UPDATE user_data
                   SET played = 1, position_ticks = 0, last_played_utc = $now, origin = 'local', updated_locally = 1
                 WHERE server_id = $server_id AND item_id = $item_id;
            ");

    // MarkLocalGroupPlayed for members resolved before a rebuild swap changed their group ids.
    private void MarkLocalItemsPlayed(List<(string ServerId, string ItemId)> members)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlMarkLocalItemsPlayed;
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var pSrv = cmd.Parameters.Add("$server_id", SqliteType.Text);
            var pId = cmd.Parameters.Add("$item_id", SqliteType.Text);
            foreach (var (srv, id) in members)
            {
                pSrv.Value = srv;
                pId.Value = id;
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static readonly string SqlGroupIdOfItem = Checked("SELECT group_id FROM cached_items WHERE id = $id AND server_id = $srv;");

    private long? GroupIdOfItem(string serverId, string itemId)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqlGroupIdOfItem;
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$srv", serverId);
            return cmd.ExecuteScalar() is long gid ? gid : null;
        }
    }

    // The build step reads chunks and fills the *_rebuild tables through the normal methods, each taking _dbLock
    // briefly, so UI reads interleave with it; holding the lock for the whole build froze the UI for minutes on
    // a large library. Only the swap holds it. Callers exclude sync via DedupEngine.WriterGate.
    public void SwapInRebuild(
        Action createMappingAndRebuildTables,
        Action<List<EmbyItem>>? postCommitReingest = null)
    {
        // 1. Build new tables alongside existing tables
        var clock = System.Diagnostics.Stopwatch.StartNew();
        createMappingAndRebuildTables();
        NocturneLogger.Info("DatabaseService", $"Rebuild tables built in {clock.ElapsedMilliseconds} ms; swapping.");
        clock.Restart();

        // 2. The swap runs on its own connection, outside _dbLock (#20 item 4): it rewrites every cached_items row and
        // took 233-507 s on the live library, during which every UI read used to wait. Reads now carry on from the
        // pre-swap snapshot (WAL); writes queue and are applied once it commits.
        WithWritesQueued("rebuild swap", swap =>
        {
            using (var pragmaCmd = swap.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA legacy_alter_table = ON;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var tx = swap.BeginTransaction();
            using var cmd = swap.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                    -- Swap tables
                    DROP TABLE IF EXISTS dedup_groups_old;
                    DROP TABLE IF EXISTS dedup_keys_old;
                    ALTER TABLE dedup_groups RENAME TO dedup_groups_old;
                    ALTER TABLE dedup_keys RENAME TO dedup_keys_old;
                    ALTER TABLE dedup_groups_rebuild RENAME TO dedup_groups;
                    ALTER TABLE dedup_keys_rebuild RENAME TO dedup_keys;

                    -- Queued Trakt events name a group, and group ids restart at 1: follow each one to the new group of
                    -- its old group's primary item, or 0 (never a group id) when that item has none.
                    UPDATE trakt_queue
                       SET group_id = COALESCE((SELECT r.group_id FROM dedup_groups_old o
                                                  JOIN item_group_rebuild r ON r.id = o.primary_item_id AND r.server_id = o.primary_server
                                                 WHERE o.group_id = trakt_queue.group_id), 0);

                    DROP TABLE dedup_groups_old;
                    DROP TABLE dedup_keys_old;

                    -- Re-create FTS5 triggers destroyed by rename
                    DROP TRIGGER IF EXISTS dg_fts_ai;
                    DROP TRIGGER IF EXISTS dg_fts_ad;
                    DROP TRIGGER IF EXISTS dg_fts_au;

                    CREATE TRIGGER dg_fts_ai AFTER INSERT ON dedup_groups BEGIN
                      INSERT INTO dedup_groups_fts(rowid, name, sort_name)
                        VALUES (new.group_id, new.name, new.sort_name);
                    END;
                    CREATE TRIGGER dg_fts_ad AFTER DELETE ON dedup_groups BEGIN
                      INSERT INTO dedup_groups_fts(dedup_groups_fts, rowid, name, sort_name)
                        VALUES ('delete', old.group_id, old.name, old.sort_name);
                    END;
                    CREATE TRIGGER dg_fts_au AFTER UPDATE ON dedup_groups BEGIN
                      INSERT INTO dedup_groups_fts(dedup_groups_fts, rowid, name, sort_name)
                        VALUES ('delete', old.group_id, old.name, old.sort_name);
                      INSERT INTO dedup_groups_fts(rowid, name, sort_name)
                        VALUES (new.group_id, new.name, new.sort_name);
                    END;

                    -- 3a. Reassign mapped items, setting the legacy text column in the same pass: GetGroupVersions
                    -- and the list queries still match on it, and group ids restart at 1, so a stale value names an
                    -- unrelated group. Each update rewrites the whole row (json_data included), so rows whose ids
                    -- are unchanged are skipped (most of them on a repeat rebuild).
                    UPDATE cached_items
                       SET group_id = r.group_id, dedup_group_id = CAST(r.group_id AS TEXT)
                      FROM item_group_rebuild r
                     WHERE r.id = cached_items.id AND r.server_id = cached_items.server_id
                       AND (cached_items.group_id IS NOT r.group_id OR cached_items.dedup_group_id IS NOT CAST(r.group_id AS TEXT));

                    -- 3b. Clear unmapped items to NULL so they never hold stale IDs
                    UPDATE cached_items
                       SET group_id = NULL, dedup_group_id = NULL
                     WHERE (group_id IS NOT NULL OR dedup_group_id IS NOT NULL)
                       AND NOT EXISTS (SELECT 1 FROM item_group_rebuild r
                                        WHERE r.id = cached_items.id AND r.server_id = cached_items.server_id);

                    DROP TABLE IF EXISTS item_group_rebuild;

                    -- 4. Rebuild FTS5 index
                    INSERT INTO dedup_groups_fts(dedup_groups_fts) VALUES('rebuild');
                ";
            cmd.ExecuteNonQuery();
            SwapHeldForTest?.Invoke(swap);
            tx.Commit();
        });
        NocturneLogger.Info("DatabaseService", $"Rebuild swap took {clock.ElapsedMilliseconds} ms on its own connection.");

        // Post-commit reingest of any items where group_id IS NULL
        if (postCommitReingest != null)
        {
            var unmapped = GetItemsWithNullGroupId();
            if (unmapped.Count > 0)
            {
                postCommitReingest(unmapped);
            }
        }
    }

    // =========================================================================
    // Trakt Scrobble Queue (§4.4, §7)
    // =========================================================================

    private static readonly string SqlEnqueueTraktEvent = Checked(@"
                    INSERT INTO trakt_queue (group_id, action, progress, payload, queued_utc, attempts)
                    VALUES ($group_id, $action, $progress, $payload, $queued_utc, 0);
                ");

    // origin: where the change came from (Origin); D7 drops it when that is Trakt itself.
    public void EnqueueTraktEvent(long groupId, string action, double progress, string? payload, string origin)
    {
        if (!Origin.WritesToTrakt(origin)) return;
        lock (_dbLock)
        {
            // During a rebuild write the group id may be about to change: follow the group's primary item instead.
            if (_queuedWrites != null && GetDedupGroup(groupId) is { } group)
            {
                var (srv, id) = (group.PrimaryServerId, group.PrimaryItemId);
                if (QueueDuringRebuildWrite(() => EnqueueTraktEvent(GroupIdOfItem(srv, id) ?? 0, action, progress, payload, origin))) return;
            }
            if (_connection == null) return;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlEnqueueTraktEvent;
                cmd.Parameters.AddWithValue("$group_id", groupId);
                cmd.Parameters.AddWithValue("$action", action);
                cmd.Parameters.AddWithValue("$progress", progress);
                cmd.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$queued_utc", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DatabaseService", $"Failed to enqueue Trakt event ({action})", ex);
            }
        }
    }

    private static readonly string SqlGetPendingTraktEvents = Checked(@"
                    SELECT queue_id, group_id, action, progress, payload, queued_utc, attempts
                    FROM trakt_queue
                    ORDER BY queued_utc ASC
                    LIMIT $limit;
                ");

    public List<TraktQueueItem> GetPendingTraktEvents(int limit = 50)
    {
        var list = new List<TraktQueueItem>();
        lock (_dbLock)
        {
            if (_connection == null) return list;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetPendingTraktEvents;
                cmd.Parameters.AddWithValue("$limit", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new TraktQueueItem(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetDouble(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6)
                    ));
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DatabaseService", "Failed to get pending Trakt events", ex);
            }
        }
        return list;
    }

    private static readonly string SqlRemoveTraktEvent = Checked("DELETE FROM trakt_queue WHERE queue_id = $id;");

    public void RemoveTraktEvent(long queueId)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => RemoveTraktEvent(queueId))) return;
            if (_connection == null) return;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlRemoveTraktEvent;
                cmd.Parameters.AddWithValue("$id", queueId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DatabaseService", $"Failed to delete Trakt queue event {queueId}", ex);
            }
        }
    }

    private static readonly string SqlIncrementTraktEventAttempts = Checked("UPDATE trakt_queue SET attempts = attempts + 1 WHERE queue_id = $id;");

    public void IncrementTraktEventAttempts(long queueId)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => IncrementTraktEventAttempts(queueId))) return;
            if (_connection == null) return;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlIncrementTraktEventAttempts;
                cmd.Parameters.AddWithValue("$id", queueId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("DatabaseService", $"Failed to increment Trakt event attempts: {ex.Message}");
            }
        }
    }

    private static readonly string SqlGetTraktQueueCount = Checked("SELECT COUNT(*) FROM trakt_queue;");

    public int GetTraktQueueCount()
    {
        lock (_dbLock)
        {
            if (_connection == null) return 0;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetTraktQueueCount;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex) when (QueryFailed(ex, SqlGetTraktQueueCount)) { return 0; }
        }
    }

    // =========================================================================
    // Watchlist Entries (§4.2, §9)
    // =========================================================================

    private static readonly string SqlUpsertWatchlistEntry = Checked(@"
                    INSERT INTO watchlist_entries (source, ref, item_type, added_utc, title, year, tmdb_key, pending_op)
                    VALUES ($source, $ref, $item_type, $added_utc, $title, $year, $tmdb_key, $pending_op)
                    ON CONFLICT(source, ref) DO UPDATE SET
                        item_type = excluded.item_type,
                        added_utc = excluded.added_utc,
                        title = COALESCE(excluded.title, watchlist_entries.title),
                        year = COALESCE(excluded.year, watchlist_entries.year),
                        tmdb_key = COALESCE(excluded.tmdb_key, watchlist_entries.tmdb_key),
                        pending_op = excluded.pending_op;
                ");

    public void UpsertWatchlistEntry(string source, string @ref, string itemType, long addedUtc, string? title = null, int? year = null, string? tmdbKey = null, string? pendingOp = null)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => UpsertWatchlistEntry(source, @ref, itemType, addedUtc, title, year, tmdbKey, pendingOp))) return;
            if (_connection == null) return;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlUpsertWatchlistEntry;
                cmd.Parameters.AddWithValue("$source", source);
                cmd.Parameters.AddWithValue("$ref", @ref);
                cmd.Parameters.AddWithValue("$item_type", itemType);
                cmd.Parameters.AddWithValue("$added_utc", addedUtc);
                cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$year", (object?)year ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tmdb_key", (object?)tmdbKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pending_op", (object?)pendingOp ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DatabaseService", $"Failed to upsert watchlist entry ({source}:{@ref})", ex);
            }
        }
    }

    private static readonly string SqlRemoveWatchlistEntry = Checked("DELETE FROM watchlist_entries WHERE source = $source AND ref = $ref;");

    public void RemoveWatchlistEntry(string source, string @ref)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => RemoveWatchlistEntry(source, @ref))) return;
            if (_connection == null) return;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlRemoveWatchlistEntry;
                cmd.Parameters.AddWithValue("$source", source);
                cmd.Parameters.AddWithValue("$ref", @ref);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DatabaseService", $"Failed to remove watchlist entry ({source}:{@ref})", ex);
            }
        }
    }

    private static readonly string SqlSetWatchlistPendingOp = Checked("UPDATE watchlist_entries SET pending_op = $pending_op WHERE source = $source AND ref = $ref;");

    public void SetWatchlistPendingOp(string source, string @ref, string? pendingOp)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => SetWatchlistPendingOp(source, @ref, pendingOp))) return;
            if (_connection == null) return;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlSetWatchlistPendingOp;
                cmd.Parameters.AddWithValue("$source", source);
                cmd.Parameters.AddWithValue("$ref", @ref);
                cmd.Parameters.AddWithValue("$pending_op", (object?)pendingOp ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DatabaseService", $"Failed to set pending_op for ({source}:{@ref})", ex);
            }
        }
    }

    private static readonly string SqlGetMergedWatchlist = Checked(@"
                    SELECT g.group_id, g.name, g.year, g.image_tag, g.image_server_id, g.image_item_id, g.tmdb_key,
                           MIN(w.added_utc) AS added, 1 AS in_lib, w.source, w.ref, g.item_type, t.poster_path
                    FROM watchlist_entries w
                    JOIN cached_items c ON c.server_id = w.source AND c.id = w.ref
                    JOIN dedup_groups g ON g.group_id = c.group_id
                    LEFT JOIN tmdb_metadata t ON t.provider_key = g.tmdb_key
                    WHERE w.source <> 'trakt' AND COALESCE(w.pending_op, '') <> 'remove'
                    GROUP BY g.group_id

                    UNION

                    SELECT g.group_id, g.name, g.year, g.image_tag, g.image_server_id, g.image_item_id, g.tmdb_key,
                           w.added_utc AS added, 1 AS in_lib, w.source, w.ref, g.item_type, t.poster_path
                    FROM watchlist_entries w
                    JOIN dedup_keys k ON k.key = w.ref
                    JOIN dedup_groups g ON g.group_id = k.group_id
                    LEFT JOIN tmdb_metadata t ON t.provider_key = g.tmdb_key
                    WHERE w.source = 'trakt' AND COALESCE(w.pending_op, '') <> 'remove'

                    UNION

                    SELECT 0 AS group_id, w.title, w.year, NULL AS image_tag, NULL AS image_server_id, NULL AS image_item_id, w.tmdb_key,
                           w.added_utc AS added, 0 AS in_lib, w.source, w.ref, w.item_type, t.poster_path
                    FROM watchlist_entries w
                    LEFT JOIN tmdb_metadata t ON t.provider_key = w.tmdb_key
                    WHERE w.source = 'trakt' AND COALESCE(w.pending_op, '') <> 'remove'
                      AND NOT EXISTS (SELECT 1 FROM dedup_keys k WHERE k.key = w.ref)
                    ORDER BY added DESC;
                ");

    public List<WatchlistCard> GetMergedWatchlist()
    {
        var list = new List<WatchlistCard>();
        lock (_dbLock)
        {
            if (_connection == null) return list;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetMergedWatchlist;
                using var reader = cmd.ExecuteReader();
                var seenGroups = new HashSet<long>();
                var seenRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                while (reader.Read())
                {
                    long gid = reader.GetInt64(0);
                    string title = reader.IsDBNull(1) ? "Unknown" : reader.GetString(1);
                    int? year = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                    string? imgTag = reader.IsDBNull(3) ? null : reader.GetString(3);
                    string? imgSrv = reader.IsDBNull(4) ? null : reader.GetString(4);
                    string? imgItem = reader.IsDBNull(5) ? null : reader.GetString(5);
                    string? tmdbKey = reader.IsDBNull(6) ? null : reader.GetString(6);
                    long added = reader.GetInt64(7);
                    bool inLib = reader.GetInt32(8) == 1;
                    string source = reader.GetString(9);
                    string @ref = reader.GetString(10);
                    string itemType = reader.IsDBNull(11) ? "Movie" : reader.GetString(11);
                    string? tmdbPoster = reader.IsDBNull(12) ? null : reader.GetString(12);

                    if (inLib && gid > 0)
                    {
                        if (!seenGroups.Add(gid)) continue;
                    }
                    else
                    {
                        if (!seenRefs.Add(@ref)) continue;
                    }

                    list.Add(new WatchlistCard
                    {
                        GroupId = gid,
                        Title = title,
                        Year = year,
                        ImageTag = imgTag,
                        ImageServerId = imgSrv,
                        ImageItemId = imgItem,
                        TmdbKey = tmdbKey,
                        TmdbPosterPath = tmdbPoster,
                        Source = source,
                        Ref = @ref,
                        ItemType = itemType,
                        AddedUtc = added,
                        IsInLibrary = inLib
                    });
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DatabaseService", "Failed to get merged watchlist", ex);
            }
        }
        return list;
    }

    private static readonly string SqlIsInWatchlist = Checked(@"
                    SELECT 1 FROM watchlist_entries w
                    JOIN cached_items c ON c.server_id = w.source AND c.id = w.ref
                    WHERE c.group_id = $gid AND COALESCE(w.pending_op, '') <> 'remove'
                    LIMIT 1;
                ");
    private static readonly string SqlIsInWatchlist2 = Checked("SELECT 1 FROM watchlist_entries WHERE tmdb_key = $key AND COALESCE(pending_op, '') <> 'remove' LIMIT 1;");

    public bool IsInWatchlist(long groupId, string? tmdbKey = null)
    {
        lock (_dbLock)
        {
            if (_connection == null) return false;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlIsInWatchlist;
                cmd.Parameters.AddWithValue("$gid", groupId);
                var res = cmd.ExecuteScalar();
                if (res != null) return true;

                if (!string.IsNullOrEmpty(tmdbKey))
                {
                    using var cmd2 = _connection.CreateCommand();
                    cmd2.CommandText = SqlIsInWatchlist2;
                    cmd2.Parameters.AddWithValue("$key", tmdbKey);
                    return cmd2.ExecuteScalar() != null;
                }
            }
            catch (Exception ex) when (QueryFailed(ex, SqlIsInWatchlist + " / " + SqlIsInWatchlist2)) { }
        }
        return false;
    }

    // =========================================================================
    // TMDB Fallback Metadata (§4.3, §10)
    // =========================================================================

    private static readonly string SqlUpsertTmdbMetadata = Checked(@"
                    INSERT INTO tmdb_metadata (
                        provider_key, poster_path, backdrop_path, still_path, overview, vote_average, fetched_utc, not_found
                    ) VALUES (
                        $provider_key, $poster_path, $backdrop_path, $still_path, $overview, $vote_average, $fetched_utc, $not_found
                    )
                    ON CONFLICT(provider_key) DO UPDATE SET
                        poster_path = excluded.poster_path,
                        backdrop_path = excluded.backdrop_path,
                        still_path = excluded.still_path,
                        overview = excluded.overview,
                        vote_average = excluded.vote_average,
                        fetched_utc = excluded.fetched_utc,
                        not_found = excluded.not_found;
                ");

    public void UpsertTmdbMetadata(TmdbMetadata metadata)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => UpsertTmdbMetadata(metadata))) return;
            if (_connection == null) return;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlUpsertTmdbMetadata;
                cmd.Parameters.AddWithValue("$provider_key", metadata.ProviderKey);
                cmd.Parameters.AddWithValue("$poster_path", (object?)metadata.PosterPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$backdrop_path", (object?)metadata.BackdropPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$still_path", (object?)metadata.StillPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$overview", (object?)metadata.Overview ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$vote_average", (object?)metadata.VoteAverage ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fetched_utc", metadata.FetchedUtc);
                cmd.Parameters.AddWithValue("$not_found", metadata.NotFound ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DatabaseService", $"Failed to upsert TMDB metadata for {metadata.ProviderKey}", ex);
            }
        }
    }

    private static readonly string SqlGetTmdbMetadata = Checked(@"
                    SELECT provider_key, poster_path, backdrop_path, still_path, overview, vote_average, fetched_utc, not_found
                    FROM tmdb_metadata
                    WHERE provider_key = $key;
                ");

    public TmdbMetadata? GetTmdbMetadata(string providerKey)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetTmdbMetadata;
                cmd.Parameters.AddWithValue("$key", providerKey);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new TmdbMetadata
                    {
                        ProviderKey = reader.GetString(0),
                        PosterPath = reader.IsDBNull(1) ? null : reader.GetString(1),
                        BackdropPath = reader.IsDBNull(2) ? null : reader.GetString(2),
                        StillPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Overview = reader.IsDBNull(4) ? null : reader.GetString(4),
                        VoteAverage = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                        FetchedUtc = reader.GetInt64(6),
                        NotFound = reader.GetInt32(7) == 1
                    };
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("DatabaseService", $"GetTmdbMetadata failed for {providerKey}: {ex.Message}");
            }
        }
        return null;
    }

    private static readonly string SqlGetGroupsNeedingTmdbArt = Checked(@"
                    SELECT g.group_id, g.item_type, g.tmdb_key, g.name, g.year, g.season_number, g.episode_number
                    FROM dedup_groups g
                    WHERE g.image_tag IS NULL
                      AND g.tmdb_key IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM tmdb_metadata t
                          WHERE t.provider_key = g.tmdb_key
                            AND (t.not_found = 1 OR t.fetched_utc > $staleCutoff)
                      )
                    LIMIT $limit;
                ");

    public List<(int GroupId, string ItemType, string TmdbKey, string? Title, int? Year, int? SeasonNumber, int? EpisodeNumber)> GetGroupsNeedingTmdbArt(int limit = 1000)
    {
        var list = new List<(int GroupId, string ItemType, string TmdbKey, string? Title, int? Year, int? SeasonNumber, int? EpisodeNumber)>();
        lock (_dbLock)
        {
            if (_connection == null) return list;
            try
            {
                long staleCutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (30 * 86400); // 30 days
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetGroupsNeedingTmdbArt;
                cmd.Parameters.AddWithValue("$staleCutoff", staleCutoff);
                cmd.Parameters.AddWithValue("$limit", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add((
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6)
                    ));
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DatabaseService", "GetGroupsNeedingTmdbArt failed", ex);
            }
        }
        return list;
    }

    private static readonly string SqlGetTmdbEnrichmentStats = Checked("SELECT COUNT(*) FROM tmdb_metadata WHERE not_found = 0;");
    private static readonly string SqlGetTmdbEnrichmentStats2 = Checked("SELECT COUNT(*) FROM tmdb_metadata WHERE not_found = 1;");
    private static readonly string SqlGetTmdbEnrichmentStats3 = Checked(@"
                        SELECT COUNT(*)
                        FROM dedup_groups g
                        WHERE g.image_tag IS NULL
                          AND g.tmdb_key IS NOT NULL
                          AND NOT EXISTS (SELECT 1 FROM tmdb_metadata t WHERE t.provider_key = g.tmdb_key);
                    ");

    public (int EnrichedCount, int PendingCount, int NotFoundCount) GetTmdbEnrichmentStats()
    {
        lock (_dbLock)
        {
            if (_connection == null) return (0, 0, 0);
            try
            {
                int enriched = 0;
                int notFound = 0;
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = SqlGetTmdbEnrichmentStats;
                    enriched = Convert.ToInt32(cmd.ExecuteScalar());
                }
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = SqlGetTmdbEnrichmentStats2;
                    notFound = Convert.ToInt32(cmd.ExecuteScalar());
                }
                int pending = 0;
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = SqlGetTmdbEnrichmentStats3;
                    pending = Convert.ToInt32(cmd.ExecuteScalar());
                }
                return (enriched, pending, notFound);
            }
            catch (Exception ex) when (QueryFailed(ex, SqlGetTmdbEnrichmentStats)) { return (0, 0, 0); }
        }
    }

    // =========================================================================
    // Integration State (§4.5)
    // =========================================================================

    private static readonly string SqlSetIntegrationState = Checked(@"
                    INSERT INTO integration_state (name, key, value)
                    VALUES ($name, $key, $value)
                    ON CONFLICT(name, key) DO UPDATE SET value = excluded.value;
                ");

    public void SetIntegrationState(string name, string key, string value)
    {
        lock (_dbLock)
        {
            if (QueueDuringRebuildWrite(() => SetIntegrationState(name, key, value))) return;
            if (_connection == null) return;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlSetIntegrationState;
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$value", value);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DatabaseService", $"SetIntegrationState failed for {name}:{key}", ex);
            }
        }
    }

    private static readonly string SqlGetIntegrationState = Checked("SELECT value FROM integration_state WHERE name = $name AND key = $key;");

    public string? GetIntegrationState(string name, string key)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetIntegrationState;
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$key", key);
                var res = cmd.ExecuteScalar();
                return res as string;
            }
            catch (Exception ex) when (QueryFailed(ex, SqlGetIntegrationState)) { return null; }
        }
    }

    private static readonly string SqlGetAllIntegrationState = Checked("SELECT key, value FROM integration_state WHERE name = $name;");

    public Dictionary<string, string> GetAllIntegrationState(string name)
    {
        var dict = new Dictionary<string, string>();
        lock (_dbLock)
        {
            if (_connection == null) return dict;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetAllIntegrationState;
                cmd.Parameters.AddWithValue("$name", name);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    dict[reader.GetString(0)] = reader.GetString(1);
                }
            }
            catch (Exception ex) when (QueryFailed(ex, SqlGetAllIntegrationState)) { }
        }
        return dict;
    }

    // =========================================================================
    // User Data & Group Identity Helpers (§7, §8)
    // =========================================================================

    private static readonly string SqlGetUserData = Checked(@"
                    SELECT position_ticks, runtime_ticks, played, last_played_utc, origin
                    FROM user_data
                    WHERE server_id = $srv AND item_id = $id;
                ");

    public UserItemData? GetUserData(string serverId, string itemId)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetUserData;
                cmd.Parameters.AddWithValue("$srv", serverId);
                cmd.Parameters.AddWithValue("$id", itemId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new UserItemData
                    {
                        PlaybackPositionTicks = reader.GetInt64(0),
                        Played = reader.GetInt32(2) == 1,
                        LastPlayedDate = reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)).UtcDateTime
                    };
                }
            }
            catch (Exception ex) when (QueryFailed(ex, SqlGetUserData)) { }
        }
        return null;
    }

    private static readonly string SqlGetUserDataOrigin = Checked("SELECT origin FROM user_data WHERE server_id = $srv AND item_id = $id;");

    public string? GetUserDataOrigin(string serverId, string itemId)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetUserDataOrigin;
                cmd.Parameters.AddWithValue("$srv", serverId);
                cmd.Parameters.AddWithValue("$id", itemId);
                return cmd.ExecuteScalar() as string;
            }
            catch (Exception ex) when (QueryFailed(ex, SqlGetUserDataOrigin)) { }
        }
        return null;
    }

    private static readonly string SqlGetGroupItemIds = Checked("SELECT server_id, id FROM cached_items WHERE group_id = $gid;");

    public List<(string ServerId, string ItemId)> GetGroupItemIds(int groupId)
    {
        var list = new List<(string ServerId, string ItemId)>();
        lock (_dbLock)
        {
            if (_connection == null) return list;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetGroupItemIds;
                cmd.Parameters.AddWithValue("$gid", groupId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
            catch (Exception ex) when (QueryFailed(ex, SqlGetGroupItemIds)) { }
        }
        return list;
    }

    private static readonly string SqlGetGroupIdForDedupKey = Checked("SELECT group_id FROM dedup_keys WHERE key = $key;");

    public int? GetGroupIdForDedupKey(string key)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetGroupIdForDedupKey;
                cmd.Parameters.AddWithValue("$key", key);
                var res = cmd.ExecuteScalar();
                if (res != null && res != DBNull.Value)
                {
                    return Convert.ToInt32(res);
                }
            }
            catch (Exception ex) when (QueryFailed(ex, SqlGetGroupIdForDedupKey)) { }
        }
        return null;
    }

    private static readonly string SqlGetAuthoritativeKeys = Checked("SELECT key FROM dedup_keys WHERE group_id = $gid AND is_authoritative = 1;");

    public List<string> GetAuthoritativeKeys(int groupId)
    {
        var keys = new List<string>();
        lock (_dbLock)
        {
            if (_connection == null) return keys;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetAuthoritativeKeys;
                cmd.Parameters.AddWithValue("$gid", groupId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) keys.Add(reader.GetString(0));
            }
            catch (Exception ex) when (QueryFailed(ex, SqlGetAuthoritativeKeys)) { }
        }
        return keys;
    }

    private static readonly string SqlGetDedupGroup = Checked(@"
                    SELECT group_id, item_type, primary_item_id, primary_server, is_provisional, version_count,
                           name, sort_name, year, overview, image_tag, backdrop_tag, community_rating,
                           date_created, series_group_id, season_number, episode_number,
                           max_height, has_4k, max_bitrate, audio_formats,
                           image_item_id, image_server_id, backdrop_item_id, backdrop_server_id, tmdb_key, primary_score
                    FROM dedup_groups
                    WHERE group_id = $gid;
                ");

    public DedupGroupRecord? GetDedupGroup(long groupId)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = SqlGetDedupGroup;
                cmd.Parameters.AddWithValue("$gid", groupId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new DedupGroupRecord
                    {
                        GroupId = reader.GetInt32(0),
                        ItemType = reader.GetString(1),
                        PrimaryItemId = reader.GetString(2),
                        PrimaryServerId = reader.GetString(3),
                        IsProvisional = reader.GetInt32(4),
                        VersionCount = reader.GetInt32(5),
                        Name = reader.GetString(6),
                        SortName = reader.IsDBNull(7) ? null : reader.GetString(7),
                        Year = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        Overview = reader.IsDBNull(9) ? null : reader.GetString(9),
                        ImageTag = reader.IsDBNull(10) ? null : reader.GetString(10),
                        BackdropTag = reader.IsDBNull(11) ? null : reader.GetString(11),
                        CommunityRating = reader.IsDBNull(12) ? null : reader.GetFloat(12),
                        DateCreated = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                        SeriesGroupId = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                        SeasonNumber = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                        EpisodeNumber = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                        MaxHeight = reader.IsDBNull(17) ? null : reader.GetInt32(17),
                        Has4k = reader.GetInt32(18),
                        MaxBitrate = reader.IsDBNull(19) ? null : reader.GetInt64(19),
                        AudioFormats = reader.IsDBNull(20) ? null : reader.GetString(20),
                        ImageItemId = reader.IsDBNull(21) ? null : reader.GetString(21),
                        ImageServerId = reader.IsDBNull(22) ? null : reader.GetString(22),
                        BackdropItemId = reader.IsDBNull(23) ? null : reader.GetString(23),
                        BackdropServerId = reader.IsDBNull(24) ? null : reader.GetString(24),
                        TmdbKey = reader.IsDBNull(25) ? null : reader.GetString(25),
                        PrimaryScore = reader.GetInt32(26),
                        IsDirty = false
                    };
                }
            }
            catch (Exception ex) when (QueryFailed(ex, SqlGetDedupGroup)) { }
        }
        return null;
    }

    // #22 item 5: at idle (after a sync, the deletion sweep, a drift measurement, and at exit), the WAL is copied back and
    // truncated, so it does not keep its high-water mark (217 MB after a sync on the live copy, gigabytes after a first
    // rebuild until the next commit paid for it). Own connection: readers carry on; it waits out those on an older
    // snapshot (busy_timeout).
    public void CheckpointWal(string after)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var walPath = _dbPath + "-wal";
            var before = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                pragma.ExecuteNonQuery();
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; // alone: a reader over both would read busy_timeout's row
            using var r = cmd.ExecuteReader();
            r.Read();
            NocturneLogger.Info("DatabaseService", $"WAL checkpoint after {after}: {before / 1048576} MB, {(r.GetInt32(0) == 0 ? "truncated" : "partial (readers)")} in {clock.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("DatabaseService", $"WAL checkpoint after {after} failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // An open rebuild window holds UI writes in memory (a playback stop's last position, at exit): abort its
        // transaction, which rolls back (the rebuild is lost and reruns), and wait while the window applies them.
        _abortWindow = true;
        if (!_windowClosed.Wait(TimeSpan.FromSeconds(30)))
            NocturneLogger.Warn("DatabaseService", "Closing with a rebuild window still open; writes made during it are lost.");
        lock (_dbLock)
        {
            if (_connection == null) return;
            _connection.Close();
            _connection.Dispose();
            _connection = null;
        }
        CheckpointWal("closing");
    }
}

public class SyncCheckpoint
{
    public string ServerId { get; set; } = string.Empty;
    public string LibraryId { get; set; } = string.Empty;
    public int StartIndex { get; set; }
    public int TotalItems { get; set; }
    public bool IsCompleted { get; set; }
    public long LastSynced { get; set; }
}
