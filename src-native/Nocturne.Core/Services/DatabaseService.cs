using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class DatabaseService : IDisposable
{
    private readonly object _dbLock = new();
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public DatabaseService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nocturne", "nocturne.db"))
    {
    }

    public DatabaseService(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
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

                DROP TABLE IF EXISTS items;
                DROP TABLE IF EXISTS dedup_episode_groups;
                DROP TABLE IF EXISTS sync_state;
                DROP TABLE IF EXISTS image_cache;

                CREATE TABLE IF NOT EXISTS cached_items (
                    id TEXT NOT NULL,
                    server_id TEXT NOT NULL,
                    parent_id TEXT,
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
                    json_data TEXT NOT NULL,
                    last_synced INTEGER NOT NULL,
                    PRIMARY KEY (id, server_id)
                );

                CREATE INDEX IF NOT EXISTS idx_cached_items_type ON cached_items(type);
                CREATE INDEX IF NOT EXISTS idx_cached_items_parent ON cached_items(parent_id);
                CREATE INDEX IF NOT EXISTS idx_cached_items_series ON cached_items(series_id);
                CREATE INDEX IF NOT EXISTS idx_cached_items_dedup ON cached_items(dedup_group_id);
                CREATE INDEX IF NOT EXISTS idx_cached_items_tmdb ON cached_items(tmdb_id);
                CREATE INDEX IF NOT EXISTS idx_cached_items_imdb ON cached_items(imdb_id);

                CREATE TABLE IF NOT EXISTS dedup_groups (
                    group_id TEXT PRIMARY KEY,
                    primary_item_id TEXT NOT NULL,
                    item_ids TEXT NOT NULL,
                    tmdb_id TEXT,
                    imdb_id TEXT,
                    title TEXT NOT NULL,
                    year INTEGER,
                    type TEXT NOT NULL
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
            ";
            cmd.ExecuteNonQuery();

            EnsureSchema(_connection);

            NocturneLogger.Info("DatabaseService", "Database initialized in WAL mode successfully.");
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("DatabaseService", "Database initialization error", ex);
        }
    }

    private void EnsureSchema(SqliteConnection conn)
    {
        // 1. cached_items migration
        MigrateTableColumns(conn, "cached_items", new Dictionary<string, string>
        {
            { "genres", "TEXT" },
            { "tmdb_id", "TEXT" },
            { "imdb_id", "TEXT" },
            { "dedup_group_id", "TEXT" }
        });

        // 2. watch_events migration
        MigrateTableColumns(conn, "watch_events", new Dictionary<string, string>
        {
            { "series_name", "TEXT" },
            { "genres", "TEXT" }
        });

        // 3. dedup_groups migration
        MigrateTableColumns(conn, "dedup_groups", new Dictionary<string, string>
        {
            { "item_ids", "TEXT" },
            { "title", "TEXT" }
        });

        using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = @"
            CREATE INDEX IF NOT EXISTS idx_cached_items_type ON cached_items(type);
            CREATE INDEX IF NOT EXISTS idx_cached_items_parent ON cached_items(parent_id);
            CREATE INDEX IF NOT EXISTS idx_cached_items_series ON cached_items(series_id);
            CREATE INDEX IF NOT EXISTS idx_cached_items_dedup ON cached_items(dedup_group_id);
            CREATE INDEX IF NOT EXISTS idx_cached_items_tmdb ON cached_items(tmdb_id);
            CREATE INDEX IF NOT EXISTS idx_cached_items_imdb ON cached_items(imdb_id);
            CREATE INDEX IF NOT EXISTS idx_watch_events_time ON watch_events(watched_at);
            CREATE INDEX IF NOT EXISTS idx_watch_events_source ON watch_events(source);
        ";
        idxCmd.ExecuteNonQuery();
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

    public void UpsertItem(EmbyItem item, string serverId, string? parentId = null)
    {
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO cached_items (
                    id, server_id, parent_id, name, type, run_time_ticks, production_year,
                    community_rating, index_number, parent_index_number, series_id, series_name,
                    season_id, season_name, genres, tmdb_id, imdb_id, dedup_group_id, json_data, last_synced
                ) VALUES (
                    $id, $server_id, $parent_id, $name, $type, $run_time_ticks, $production_year,
                    $community_rating, $index_number, $parent_index_number, $series_id, $series_name,
                    $season_id, $season_name, $genres, $tmdb_id, $imdb_id, $dedup_group_id, $json_data, $last_synced
                )
                ON CONFLICT(id, server_id) DO UPDATE SET
                    name = excluded.name,
                    parent_id = COALESCE(excluded.parent_id, cached_items.parent_id),
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
                    json_data = excluded.json_data,
                    last_synced = excluded.last_synced;
            ";

            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$parent_id", (object?)parentId ?? (object?)item.SeasonId ?? DBNull.Value);
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
            cmd.Parameters.AddWithValue("$json_data", JsonSerializer.Serialize(item));
            cmd.Parameters.AddWithValue("$last_synced", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            cmd.ExecuteNonQuery();
        }
    }

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
                cmd.CommandText = @"
                    INSERT INTO cached_items (
                        id, server_id, parent_id, name, type, run_time_ticks, production_year,
                        community_rating, index_number, parent_index_number, series_id, series_name,
                        season_id, season_name, genres, tmdb_id, imdb_id, dedup_group_id, json_data, last_synced
                    ) VALUES (
                        $id, $server_id, $parent_id, $name, $type, $run_time_ticks, $production_year,
                        $community_rating, $index_number, $parent_index_number, $series_id, $series_name,
                        $season_id, $season_name, $genres, $tmdb_id, $imdb_id, $dedup_group_id, $json_data, $last_synced
                    )
                    ON CONFLICT(id, server_id) DO UPDATE SET
                        name = excluded.name,
                        parent_id = COALESCE(excluded.parent_id, cached_items.parent_id),
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
                        json_data = excluded.json_data,
                        last_synced = excluded.last_synced;
                ";

                var pId = cmd.Parameters.Add("$id", SqliteType.Text);
                var pServerId = cmd.Parameters.Add("$server_id", SqliteType.Text);
                var pParentId = cmd.Parameters.Add("$parent_id", SqliteType.Text);
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
                var pJsonData = cmd.Parameters.Add("$json_data", SqliteType.Text);
                var pLastSynced = cmd.Parameters.Add("$last_synced", SqliteType.Integer);

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var item in chunk)
                {
                    pId.Value = item.Id;
                    pServerId.Value = serverId;
                    pParentId.Value = (object?)parentId ?? (object?)item.SeasonId ?? DBNull.Value;
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
                    pJsonData.Value = JsonSerializer.Serialize(item);
                    pLastSynced.Value = now;

                    cmd.ExecuteNonQuery();
                }

                // Crash safety: write the checkpoint in the exact same transaction as the data it covers
                if (isFinalChunk && checkpointStartIndex.HasValue && !string.IsNullOrEmpty(parentId))
                {
                    using var cpCmd = _connection.CreateCommand();
                    cpCmd.Transaction = tx;
                    cpCmd.CommandText = @"
                        INSERT INTO sync_checkpoints (server_id, library_id, start_index, total_items, is_completed, last_synced)
                        VALUES ($cs_server_id, $cs_library_id, $cs_start_index, $cs_total_items, $cs_is_completed, $cs_last_synced)
                        ON CONFLICT(server_id, library_id) DO UPDATE SET
                            start_index = excluded.start_index,
                            total_items = excluded.total_items,
                            is_completed = excluded.is_completed,
                            last_synced = excluded.last_synced;
                    ";
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
        return items;
    }

    public void SaveContinueWatchingCache(IEnumerable<EmbyItem> items)
    {
        var list = items.ToList();
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var tx = _connection.BeginTransaction();
            using var delCmd = _connection.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM continue_watching_cache;";
            delCmd.ExecuteNonQuery();

            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO continue_watching_cache (id, server_id, sort_order, json_data, updated_at)
                    VALUES ($id, $server_id, $sort_order, $json_data, $updated_at);";
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

    public List<EmbyItem> GetCachedContinueWatching()
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            if (_connection == null) return items;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT json_data, server_id FROM continue_watching_cache ORDER BY sort_order ASC;";
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

    public void RemoveFromContinueWatchingCache(string itemId)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM continue_watching_cache WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.ExecuteNonQuery();
        }
    }

    public EmbyItem? GetCachedItem(string itemId, string? serverId = null)
    {
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            if (!string.IsNullOrEmpty(serverId))
            {
                cmd.CommandText = "SELECT json_data FROM cached_items WHERE id = $id AND server_id = $serverId LIMIT 1;";
                cmd.Parameters.AddWithValue("$id", itemId);
                cmd.Parameters.AddWithValue("$serverId", serverId);
            }
            else
            {
                cmd.CommandText = "SELECT json_data FROM cached_items WHERE id = $id LIMIT 1;";
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
                return item;
            }
            catch
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
                catch { }
            }
        }
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
                       COALESCE((SELECT COUNT(*) FROM cached_items sub WHERE sub.dedup_group_id = c.dedup_group_id AND sub.dedup_group_id IS NOT NULL AND sub.dedup_group_id != ''), 1) AS v_count
                FROM cached_items c
                LEFT JOIN dedup_groups dg ON c.dedup_group_id = dg.group_id
                WHERE (dg.group_id IS NULL OR c.id = dg.primary_item_id) ";

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
        return items;
    }

    public List<EmbyItem> GetGroupVersions(string groupId)
    {
        var list = new List<EmbyItem>();
        if (string.IsNullOrEmpty(groupId)) return list;

        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE dedup_group_id = $group_id;";
            cmd.Parameters.AddWithValue("$group_id", groupId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var gid = reader.IsDBNull(2) ? groupId : reader.GetString(2);
                var item = JsonSerializer.Deserialize<EmbyItem>(json);
                if (item != null)
                {
                    item.ServerId = sid;
                    item.DedupGroupId = gid;
                    list.Add(item);
                }
            }
        }
        return list;
    }

    public List<EmbyItem> GetSiblingVersions(EmbyItem item)
    {
        if (!string.IsNullOrEmpty(item.DedupGroupId))
        {
            var byGroup = GetGroupVersions(item.DedupGroupId);
            if (byGroup.Count > 0) return byGroup;
        }

        var list = new List<EmbyItem> { item };
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            if (!string.IsNullOrEmpty(item.TmdbId))
            {
                cmd.CommandText = "SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE tmdb_id = $val AND id != $id;";
                cmd.Parameters.AddWithValue("$val", item.TmdbId);
                cmd.Parameters.AddWithValue("$id", item.Id);
            }
            else if (!string.IsNullOrEmpty(item.ImdbId))
            {
                cmd.CommandText = "SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE imdb_id = $val AND id != $id;";
                cmd.Parameters.AddWithValue("$val", item.ImdbId);
                cmd.Parameters.AddWithValue("$id", item.Id);
            }
            else if (!string.IsNullOrEmpty(item.Name) && item.ProductionYear.HasValue)
            {
                cmd.CommandText = "SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE LOWER(name) = $name AND year = $year AND id != $id;";
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
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO watch_events (id, item_id, server_id, item_name, series_name, item_type, duration_seconds, watched_at, source, genres)
                VALUES ($id, $item_id, $server_id, $item_name, $series_name, $item_type, $duration_seconds, $watched_at, $source, $genres);
            ";
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

    public void SaveTraktWatchlist(List<TraktWatchlistItem> items)
    {
        lock (_dbLock)
        {
            using var tx = _connection.BeginTransaction();
            using var delCmd = _connection.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM trakt_watchlist;";
            delCmd.ExecuteNonQuery();

            foreach (var item in items)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO trakt_watchlist (id, trakt_id, tmdb_id, imdb_id, title, year, type, added_at)
                    VALUES ($id, $trakt_id, $tmdb_id, $imdb_id, $title, $year, $type, $added_at);
                ";
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

    public List<TraktWatchlistItem> GetTraktWatchlist()
    {
        var list = new List<TraktWatchlistItem>();
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, title, year, type, tmdb_id, imdb_id, added_at FROM trakt_watchlist ORDER BY added_at DESC;";
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

    public int GetCachedItemsCount()
    {
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM cached_items;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public int GetDedupGroupsCount()
    {
        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM dedup_groups;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public long GetDatabaseSize()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "Nocturne");
            long total = 0;
            var dbPath = Path.Combine(dir, "nocturne.db");
            var walPath = Path.Combine(dir, "nocturne.db-wal");
            var shmPath = Path.Combine(dir, "nocturne.db-shm");
            var imgDir = Path.Combine(dir, "image-cache");

            if (File.Exists(dbPath)) total += new FileInfo(dbPath).Length;
            if (File.Exists(walPath)) total += new FileInfo(walPath).Length;
            if (File.Exists(shmPath)) total += new FileInfo(shmPath).Length;

            if (Directory.Exists(imgDir))
            {
                try
                {
                    var di = new DirectoryInfo(imgDir);
                    total += di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
                }
                catch { }
            }
            return total;
        }
        catch { }
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

    private List<PreservedWatchEvent> ReadAllWatchEvents()
    {
        var list = new List<PreservedWatchEvent>();
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open) return list;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, item_id, server_id, item_name, series_name, item_type, duration_seconds, watched_at, source, genres FROM watch_events;";
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

    private List<PreservedTraktHistory> ReadAllTraktHistory()
    {
        var list = new List<PreservedTraktHistory>();
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open) return list;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT key, trakt_type, tmdb_id, imdb_id, show_tmdb_id, season_number, episode_number, watched_at, synced_at FROM trakt_watched_history;";
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

    private void RestoreWatchEvents(List<PreservedWatchEvent> events)
    {
        if (events.Count == 0 || _connection == null) return;
        try
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR IGNORE INTO watch_events (id, item_id, server_id, item_name, series_name, item_type, duration_seconds, watched_at, source, genres)
                VALUES ($id, $item_id, $server_id, $item_name, $series_name, $item_type, $duration_seconds, $watched_at, $source, $genres);
            ";
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

    private void RestoreTraktHistory(List<PreservedTraktHistory> items)
    {
        if (items.Count == 0 || _connection == null) return;
        try
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR IGNORE INTO trakt_watched_history (key, trakt_type, tmdb_id, imdb_id, show_tmdb_id, season_number, episode_number, watched_at, synced_at)
                VALUES ($key, $trakt_type, $tmdb_id, $imdb_id, $show_tmdb_id, $season_number, $episode_number, $watched_at, $synced_at);
            ";
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

                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(appData, "Nocturne");
                var dbPath = Path.Combine(dir, "nocturne.db");
                var walPath = Path.Combine(dir, "nocturne.db-wal");
                var shmPath = Path.Combine(dir, "nocturne.db-shm");
                var imgDir = Path.Combine(dir, "image-cache");

                if (File.Exists(dbPath)) File.Delete(dbPath);
                if (File.Exists(walPath)) File.Delete(walPath);
                if (File.Exists(shmPath)) File.Delete(shmPath);

                if (Directory.Exists(imgDir))
                {
                    try { Directory.Delete(imgDir, recursive: true); } catch { }
                }

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
                        DELETE FROM trakt_watchlist;
                        DELETE FROM sync_checkpoints;
                        DROP TABLE IF EXISTS items;
                        DROP TABLE IF EXISTS dedup_episode_groups;
                        DROP TABLE IF EXISTS sync_state;
                        DROP TABLE IF EXISTS image_cache;
                        VACUUM;
                        PRAGMA wal_checkpoint(TRUNCATE);
                    ";
                    cmd.ExecuteNonQuery();
                }
                catch (Exception sqlEx)
                {
                    NocturneLogger.Error("DatabaseService", "SQL fallback wipe error", sqlEx);
                }

                try
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var imgDir = Path.Combine(appData, "Nocturne", "image-cache");
                    if (Directory.Exists(imgDir))
                    {
                        Directory.Delete(imgDir, recursive: true);
                    }
                }
                catch { }
            }

            NocturneLogger.Info("DatabaseService", $"ClearAllCache completed. Preserved {preservedEvents.Count} watch events, {preservedHistory.Count} trakt history.");
        }
    }

    public void ClearServerData(string serverId)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM cached_items WHERE server_id = $server_id; DELETE FROM sync_checkpoints WHERE server_id = $server_id;";
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.ExecuteNonQuery();
        }
    }

    public List<EmbyItem> GetItemsForDeduplication()
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE type IN ('Movie', 'Series');";
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
                catch { }
            }
        }
        return items;
    }

    public void SaveDedupGroups(List<DedupGroup> groups)
    {
        var multiVersionGroups = groups.Where(g => g.SiblingItems != null && g.SiblingItems.Count > 0).ToList();

        lock (_dbLock)
        {
            using var tx = _connection!.BeginTransaction();
            using var clearCmd = _connection.CreateCommand();
            clearCmd.Transaction = tx;
            clearCmd.CommandText = "DELETE FROM dedup_groups; UPDATE cached_items SET dedup_group_id = NULL;";
            clearCmd.ExecuteNonQuery();

            foreach (var g in multiVersionGroups)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO dedup_groups (group_id, primary_item_id, item_ids, tmdb_id, imdb_id, title, year, type)
                    VALUES ($group_id, $primary_item_id, $item_ids, $tmdb_id, $imdb_id, $title, $year, $type);
                ";
                var itemIds = new List<string> { g.PrimaryItem.Id };
                itemIds.AddRange(g.SiblingItems.Select(x => x.Id));

                cmd.Parameters.AddWithValue("$group_id", g.GroupId);
                cmd.Parameters.AddWithValue("$primary_item_id", g.PrimaryItem.Id);
                cmd.Parameters.AddWithValue("$item_ids", string.Join(",", itemIds));
                cmd.Parameters.AddWithValue("$tmdb_id", (object?)g.TmdbId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$imdb_id", (object?)g.ImdbId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$title", g.PrimaryItem.Name);
                cmd.Parameters.AddWithValue("$year", (object?)g.PrimaryItem.ProductionYear ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$type", g.PrimaryItem.Type);
                cmd.ExecuteNonQuery();

                // Update dedup_group_id on cached_items
                using var updateCmd = _connection.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = $"UPDATE cached_items SET dedup_group_id = $gid WHERE id IN ('{string.Join("','", itemIds)}');";
                updateCmd.Parameters.AddWithValue("$gid", g.GroupId);
                updateCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void ClearResumeProgress(string itemId)
    {
        lock (_dbLock)
        {
            try
            {
                using var cmd = _connection?.CreateCommand();
                if (cmd == null) return;
                cmd.CommandText = "SELECT id, server_id, json_data FROM cached_items WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", itemId);
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
                    upCmd.CommandText = "UPDATE cached_items SET json_data = @json WHERE id = @id AND server_id = @srv";
                    upCmd.Parameters.AddWithValue("@json", newJson);
                    upCmd.Parameters.AddWithValue("@id", uId);
                    upCmd.Parameters.AddWithValue("@srv", uSrv);
                    upCmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("DatabaseService", $"ClearResumeProgress failed for {itemId}: {ex.Message}");
            }
        }
    }

    public SyncCheckpoint? GetSyncCheckpoint(string serverId, string libraryId)
    {
        lock (_dbLock)
        {
            if (_connection == null) return null;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT start_index, total_items, is_completed, last_synced FROM sync_checkpoints WHERE server_id = $server_id AND library_id = $library_id;";
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

    public void SaveSyncCheckpoint(string serverId, string libraryId, int startIndex, int totalItems, bool isCompleted)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO sync_checkpoints (server_id, library_id, start_index, total_items, is_completed, last_synced)
                VALUES ($server_id, $library_id, $start_index, $total_items, $is_completed, $last_synced)
                ON CONFLICT(server_id, library_id) DO UPDATE SET
                    start_index = excluded.start_index,
                    total_items = excluded.total_items,
                    is_completed = excluded.is_completed,
                    last_synced = excluded.last_synced;
            ";
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$library_id", libraryId);
            cmd.Parameters.AddWithValue("$start_index", startIndex);
            cmd.Parameters.AddWithValue("$total_items", totalItems);
            cmd.Parameters.AddWithValue("$is_completed", isCompleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_synced", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearSyncCheckpoints(string? serverId = null)
    {
        lock (_dbLock)
        {
            if (_connection == null) return;
            using var cmd = _connection.CreateCommand();
            if (string.IsNullOrEmpty(serverId))
            {
                cmd.CommandText = "DELETE FROM sync_checkpoints;";
            }
            else
            {
                cmd.CommandText = "DELETE FROM sync_checkpoints WHERE server_id = $server_id;";
                cmd.Parameters.AddWithValue("$server_id", serverId);
            }
            cmd.ExecuteNonQuery();
        }
    }

    public List<EmbyItem> GetAllCachedItemsForParent(string parentId, string serverId)
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            if (_connection == null) return items;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE parent_id = $parent_id AND server_id = $server_id;";
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
                catch { }
            }
        }
        return items;
    }

    public List<EmbyItem> GetAllCachedItems(string? serverId = null)
    {
        var items = new List<EmbyItem>();
        lock (_dbLock)
        {
            if (_connection == null) return items;
            using var cmd = _connection.CreateCommand();
            if (string.IsNullOrEmpty(serverId))
            {
                cmd.CommandText = "SELECT json_data, server_id, dedup_group_id FROM cached_items;";
            }
            else
            {
                cmd.CommandText = "SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE server_id = $server_id;";
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
                catch { }
            }
        }
        return items;
    }

    public void Dispose()
    {
        lock (_dbLock)
        {
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
        }
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
