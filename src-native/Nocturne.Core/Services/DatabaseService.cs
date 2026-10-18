using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public DatabaseService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Nocturne");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "nocturne.db");
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
            ";
            cmd.ExecuteNonQuery();
            NocturneLogger.Info("DatabaseService", "Database initialized in WAL mode successfully.");
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("DatabaseService", "Database initialization error", ex);
        }
    }

    public void UpsertItem(EmbyItem item, string serverId)
    {
        lock (_connection!)
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
                    dedup_group_id = excluded.dedup_group_id,
                    json_data = excluded.json_data,
                    last_synced = excluded.last_synced;
            ";

            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.Parameters.AddWithValue("$parent_id", (object?)item.SeasonId ?? DBNull.Value);
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

    public List<EmbyItem> GetCachedItems(string? type = null, string? serverId = null, int limit = 50)
    {
        var items = new List<EmbyItem>();
        lock (_connection!)
        {
            using var cmd = _connection.CreateCommand();
            var sql = "SELECT json_data, server_id, dedup_group_id FROM cached_items WHERE 1=1 ";
            if (!string.IsNullOrEmpty(type))
            {
                sql += "AND type = $type ";
                cmd.Parameters.AddWithValue("$type", type);
            }
            if (!string.IsNullOrEmpty(serverId))
            {
                sql += "AND server_id = $server_id ";
                cmd.Parameters.AddWithValue("$server_id", serverId);
            }
            sql += "ORDER BY name ASC LIMIT $limit;";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var dedupId = reader.IsDBNull(2) ? null : reader.GetString(2);

                var item = JsonSerializer.Deserialize<EmbyItem>(json);
                if (item != null)
                {
                    item.ServerId = sid;
                    item.DedupGroupId = dedupId;
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

        lock (_connection!)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT json_data, server_id FROM cached_items WHERE dedup_group_id = $group_id;";
            cmd.Parameters.AddWithValue("$group_id", groupId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var sid = reader.GetString(1);
                var item = JsonSerializer.Deserialize<EmbyItem>(json);
                if (item != null)
                {
                    item.ServerId = sid;
                    item.DedupGroupId = groupId;
                    list.Add(item);
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
        lock (_connection!)
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
        lock (_connection!)
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
        lock (_connection!)
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
        lock (_connection!)
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
        lock (_connection!)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM cached_items;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public int GetDedupGroupsCount()
    {
        lock (_connection!)
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
            var dbPath = Path.Combine(appData, "Nocturne", "nocturne.db");
            if (File.Exists(dbPath))
            {
                return new FileInfo(dbPath).Length;
            }
        }
        catch { }
        return 0;
    }

    public void ClearAllCache()
    {
        lock (_connection!)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM cached_items; DELETE FROM dedup_groups; DELETE FROM trakt_watchlist;";
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearServerData(string serverId)
    {
        lock (_connection!)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM cached_items WHERE server_id = $server_id;";
            cmd.Parameters.AddWithValue("$server_id", serverId);
            cmd.ExecuteNonQuery();
        }
    }

    public void SaveDedupGroups(List<DedupGroup> groups)
    {
        lock (_connection!)
        {
            using var tx = _connection.BeginTransaction();
            using var clearCmd = _connection.CreateCommand();
            clearCmd.Transaction = tx;
            clearCmd.CommandText = "DELETE FROM dedup_groups;";
            clearCmd.ExecuteNonQuery();

            foreach (var g in groups)
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

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
