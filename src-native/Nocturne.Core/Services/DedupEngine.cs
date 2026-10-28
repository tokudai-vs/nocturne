using System.Text.RegularExpressions;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class DedupGroup
{
    public string GroupId { get; set; } = Guid.NewGuid().ToString("N");
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string Type { get; set; } = "Movie"; // Movie or Series
    public EmbyItem PrimaryItem { get; set; } = null!;
    public List<EmbyItem> SiblingItems { get; set; } = new();

    public int VersionCount => 1 + SiblingItems.Count;
    public List<EmbyItem> AllVersions
    {
        get
        {
            var list = new List<EmbyItem> { PrimaryItem };
            list.AddRange(SiblingItems);
            return list;
        }
    }
}

public class DedupEngine
{
    private static readonly Regex ReleaseTagsRegex = new(@"\b(4k|2160p|1080p|720p|uhd|fhd|hd|remux|bluray|blu-ray|bdmv|web-dl|webdl|webrip|hdr|hdr10|hdr10\+|dv|dovi|imax|extended|directors cut|unrated|edition)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BracketsRegex = new(@"\[[^\]]*\]|\([^\)]*\)", RegexOptions.Compiled);
    private static readonly Regex PunctuationRegex = new(@"[^\w\d\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    // One dedup writer at a time: a sync and a rebuild both rewrite group ids, and a sync running across a
    // rebuild's swap would write old ids into the new tables. Held for a whole sync or rebuild.
    public SemaphoreSlim WriterGate { get; } = new(1, 1);

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var t = title;
        // Strip bracketed content [4K Remux], (2010), etc.
        t = BracketsRegex.Replace(t, " ");
        // Strip common technical/release tokens
        t = ReleaseTagsRegex.Replace(t, " ");
        // Strip punctuation and normalize whitespace
        var stripped = PunctuationRegex.Replace(t.ToLowerInvariant(), " ");
        return MultiSpaceRegex.Replace(stripped, " ").Trim();
    }

    public static bool IsGenericTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var norm = NormalizeTitle(title);
        if (norm.Length <= 1) return true;

        string[] generic = { "episode", "pilot", "special", "part", "chapter", "finale", "series finale", "season finale", "premiere", "season premiere", "movie", "feature" };
        if (generic.Contains(norm)) return true;
        if (Regex.IsMatch(norm, @"^(episode|part|chapter)\s+\d+$", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    public List<DedupGroup> Deduplicate(IEnumerable<EmbyItem> items)
    {
        var allItemsList = items.ToList();
        var groups = new List<DedupGroup>();
        var groupedItemIds = new HashSet<string>();

        // Separate Movies and Series
        var movies = allItemsList.Where(i => i.Type == "Movie").ToList();
        var series = allItemsList.Where(i => i.Type == "Series").ToList();

        // ── Pass 1: TMDB Movie ───────────────────────────
        RunMatchingPass(movies, groups, groupedItemIds, "Movie", i => !string.IsNullOrEmpty(i.TmdbId) ? $"tmdb_{i.TmdbId}" : null);

        // ── Pass 2: IMDB Movie ───────────────────────────
        RunMatchingPass(movies, groups, groupedItemIds, "Movie", i => !string.IsNullOrEmpty(i.ImdbId) ? $"imdb_{i.ImdbId}" : null);

        // ── Pass 3: Name + Year Movie ─────────────────────
        RunMatchingPass(movies, groups, groupedItemIds, "Movie", i =>
        {
            var norm = NormalizeTitle(i.Name);
            return !string.IsNullOrEmpty(norm) ? $"name_{norm}_{i.ProductionYear ?? 0}" : null;
        });

        // ── Pass 4: TMDB Series ──────────────────────────
        RunMatchingPass(series, groups, groupedItemIds, "Series", i => !string.IsNullOrEmpty(i.TmdbId) ? $"tmdb_{i.TmdbId}" : null);

        // ── Pass 5: IMDB Series ──────────────────────────
        RunMatchingPass(series, groups, groupedItemIds, "Series", i => !string.IsNullOrEmpty(i.ImdbId) ? $"imdb_{i.ImdbId}" : null);

        // ── Pass 6: Name + Year Series ────────────────────
        RunMatchingPass(series, groups, groupedItemIds, "Series", i =>
        {
            var norm = NormalizeTitle(i.Name);
            return !string.IsNullOrEmpty(norm) ? $"name_{norm}_{i.ProductionYear ?? 0}" : null;
        });

        // Add remaining un-grouped items as singleton groups (so they are searchable and displayable)
        foreach (var item in allItemsList.Where(i => !groupedItemIds.Contains(i.Id)))
        {
            groups.Add(new DedupGroup
            {
                GroupId = Guid.NewGuid().ToString("N"),
                TmdbId = item.TmdbId,
                ImdbId = item.ImdbId,
                Title = item.Name,
                Year = item.ProductionYear,
                Type = item.Type,
                PrimaryItem = item,
                SiblingItems = new List<EmbyItem>()
            });
        }

        // Apply primary scoring & version sorting for each group
        foreach (var group in groups)
        {
            if (group.SiblingItems.Count > 0)
            {
                var allCopies = group.AllVersions;
                var best = allCopies.OrderByDescending(c => ScorePrimaryItem(c, null)).First();
                group.PrimaryItem = best;
                group.SiblingItems = allCopies.Where(c => c.Id != best.Id)
                                              .OrderByDescending(c => ScorePrimaryItem(c, null))
                                              .ToList();

                // Annotate items with group info
                group.PrimaryItem.DedupGroupId = group.GroupId;
                group.PrimaryItem.VersionCount = group.VersionCount;
                foreach (var sib in group.SiblingItems)
                {
                    sib.DedupGroupId = group.GroupId;
                    sib.VersionCount = group.VersionCount;
                }
            }
        }

        NocturneLogger.Info("DedupEngine", $"Deduplication complete: {allItemsList.Count} items -> {groups.Count} unique groups ({groups.Count(g => g.SiblingItems.Count > 0)} multi-version groups).");
        return groups;
    }

    private void RunMatchingPass(
        List<EmbyItem> pool,
        List<DedupGroup> groups,
        HashSet<string> groupedItemIds,
        string type,
        Func<EmbyItem, string?> keySelector)
    {
        var bucket = new Dictionary<string, List<EmbyItem>>();

        foreach (var item in pool)
        {
            if (groupedItemIds.Contains(item.Id)) continue;
            var key = keySelector(item);
            if (string.IsNullOrEmpty(key)) continue;

            if (!bucket.TryGetValue(key, out var list))
            {
                list = new List<EmbyItem>();
                bucket[key] = list;
            }
            list.Add(item);
        }

        foreach (var kv in bucket)
        {
            if (kv.Value.Count > 1)
            {
                var group = new DedupGroup
                {
                    GroupId = Guid.NewGuid().ToString("N"),
                    TmdbId = kv.Value.FirstOrDefault(i => !string.IsNullOrEmpty(i.TmdbId))?.TmdbId,
                    ImdbId = kv.Value.FirstOrDefault(i => !string.IsNullOrEmpty(i.ImdbId))?.ImdbId,
                    Title = kv.Value[0].Name,
                    Year = kv.Value[0].ProductionYear,
                    Type = type,
                    PrimaryItem = kv.Value[0],
                    SiblingItems = kv.Value.Skip(1).ToList()
                };

                foreach (var item in kv.Value)
                {
                    groupedItemIds.Add(item.Id);
                }

                groups.Add(group);
            }
        }
    }

    public static int ScorePrimaryItem(EmbyItem item, IList<string>? serverPriority = null)
    {
        int score = 0;
        bool hasBackdrop = (item.BackdropImageTags != null && item.BackdropImageTags.Count > 0) || !string.IsNullOrEmpty(item.BackdropTag);
        bool hasPoster = (item.ImageTags != null && item.ImageTags.ContainsKey("Primary")) || !string.IsNullOrEmpty(item.ImageTag);
        bool hasOverview = !string.IsNullOrWhiteSpace(item.Overview);
        bool hasRating = item.CommunityRating.HasValue && item.CommunityRating > 0;

        if (hasBackdrop) score += 100;
        if (hasPoster) score += 50;
        if (hasOverview) score += 30;
        if (hasRating) score += 10;

        if (serverPriority != null && !string.IsNullOrEmpty(item.ServerId))
        {
            int idx = serverPriority.IndexOf(item.ServerId);
            if (idx >= 0)
            {
                score += Math.Max(0, 20 - idx * 2);
            }
        }

        return score;
    }

    public int RebuildIndex(DatabaseService db, IList<string>? serverPriority = null)
    {
        if (!WriterGate.Wait(0))
        {
            NocturneLogger.Info("DedupEngine", "Rebuild waiting for the running sync to finish.");
            WriterGate.Wait();
        }
        try
        {
            return RebuildIndexCore(db, serverPriority);
        }
        finally
        {
            WriterGate.Release();
        }
    }

    private int RebuildIndexCore(DatabaseService db, IList<string>? serverPriority)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var dedupIndex = new ConcurrentDedupIndex();
        int groups = 0, multiVersion = 0;
        var pendingCorrections = db.OverridesPending(); // #38: what this rebuild applies; later ones keep waiting

        db.SwapInRebuild(() =>
        {
            db.ExecuteSql(@"
                DROP TABLE IF EXISTS dedup_groups_rebuild;
                DROP TABLE IF EXISTS dedup_keys_rebuild;
                DROP TABLE IF EXISTS item_group_rebuild;

                CREATE TABLE dedup_groups_rebuild (
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

                CREATE TABLE dedup_keys_rebuild (
                    key              TEXT PRIMARY KEY,
                    group_id         INTEGER NOT NULL,
                    is_authoritative INTEGER NOT NULL DEFAULT 1
                ) WITHOUT ROWID;

                CREATE TABLE item_group_rebuild (
                    id        TEXT NOT NULL,
                    server_id TEXT NOT NULL,
                    group_id  INTEGER NOT NULL,
                    PRIMARY KEY (id, server_id)
                ) WITHOUT ROWID;
            ");

            var previous = new Dictionary<(string, string), int>();
            dedupIndex = GroupInMemory(db, serverPriority, previous);

            var save = System.Diagnostics.Stopwatch.StartNew();
            db.SaveRebuildDedupState(dedupIndex, previous);
            NocturneLogger.Info("DedupEngine", $"Rebuild state written in {save.ElapsedMilliseconds} ms.");
            // Counted now: the post-commit pass below works through the database, and the index then holds one batch.
            groups = dedupIndex.TotalGroupsCount;
            multiVersion = dedupIndex.MultiVersionGroupsCount;
        }, unmapped =>
        {
            dedupIndex.InitializeFromDb(db); // the saved ids are not the index's: new groups number after them
            dedupIndex.ProcessBatch(unmapped, db);
        });

        db.SetMigrationState(ConcurrentDedupIndex.EngineMarkerKey, ConcurrentDedupIndex.EngineVersion);
        // A rebuild is what drift is measured against: none is left.
        db.SetMigrationState(DriftItemsKey, "0");
        db.SetMigrationState(DriftGroupsKey, "0");
        db.SetMigrationState(DriftMeasuredKey, DateTime.UtcNow.ToString("o"));
        db.SetMigrationState(DatabaseService.OverridesPendingKey, Math.Max(0, db.OverridesPending() - pendingCorrections).ToString());
        FeatureFlags.MeasureCascadeGate(db);
        NocturneLogger.Info("DedupEngine", $"Rebuild complete in {clock.ElapsedMilliseconds} ms: {groups} groups, {multiVersion} multi-version, {dedupIndex.ConflictsRefused} merges refused on conflicting ids.");
        return multiVersion;
    }

    // The rebuild's grouping: every Movie/Series/Episode row in insertion order, untagged episodes last, held in memory.
    // With previous, also each item's group before the rebuild (SaveRebuildDedupState keeps ids where it can).
    private static ConcurrentDedupIndex GroupInMemory(DatabaseService db, IList<string>? serverPriority, Dictionary<(string, string), int>? previous = null)
    {
        var index = new ConcurrentDedupIndex();
        index.SetServerPriority(serverPriority);
        long afterRowId = 0;
        const int chunkSize = 5000;
        var untagged = new List<EmbyItem>();
        while (true)
        {
            var (batch, lastRowId) = db.GetCachedItemsChunk(afterRowId, chunkSize);
            if (lastRowId == afterRowId) break;
            if (previous != null)
                foreach (var i in batch)
                    if (int.TryParse(i.DedupGroupId, out var gid)) previous[(i.ServerId ?? string.Empty, i.Id)] = gid;
            index.ResolveSeriesProviderIds(batch, db);
            index.ProcessBatch(batch, null, ConcurrentDedupIndex.UntaggedEpisodes.Defer);
            untagged.AddRange(batch.Where(ConcurrentDedupIndex.IsUntaggedEpisode));
            afterRowId = lastRowId;
        }
        index.ProcessBatch(untagged, null);
        return index;
    }

    // The plan's §8C drift counter (review V1): the items whose group differs from what a rebuild of the same items
    // gives. A clean full pass gives 0 (#20 item 2); outages and interleaved writes are what move it. The rebuild's
    // grouping runs in memory, nothing is written, and the two partitions are compared (PartitionOracle). Weekly
    // after a sync (SyncEngine), and from Settings (#21 item 5).
    public const string DriftItemsKey = "dedup_drift_items";
    public const string DriftGroupsKey = "dedup_drift_groups";
    public const string DriftMeasuredKey = "dedup_drift_utc";
    // The plan's drift panel also flags groups over the size cap (M6): counted from the same read of the partition.
    public const string OverCapKey = "dedup_over_cap";
    public const string OverCapNamesKey = "dedup_over_cap_names";

    public record Drift(int Items, int Groups, int OverCap);

    public Drift? MeasureDrift(DatabaseService db, IList<string>? serverPriority, int servers)
    {
        WriterGate.Wait();
        try
        {
            var drift = MeasureDriftCore(db, serverPriority, servers);
            db.CheckpointWal("a drift measurement");
            return drift;
        }
        finally
        {
            WriterGate.Release();
        }
    }

    internal static Drift? MeasureDriftCore(DatabaseService db, IList<string>? serverPriority, int servers)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var rebuilt = GroupInMemory(db, serverPriority).Partition(); // the index is dropped before the read below
            var stored = PartitionOracle.Read(db.DbPath);
            var divergence = PartitionOracle.Compare(stored, rebuilt);
            var cap = ConcurrentDedupIndex.GroupSizeCap(servers);
            var overCap = stored.Where(g => g.Members.Count > cap && g.GroupId != null).OrderByDescending(g => g.Members.Count).ToList();
            var drift = new Drift(divergence.OnlyInA.Sum(g => g.Members.Count), divergence.OnlyInA.Count, overCap.Count);
            db.SetMigrationState(DriftItemsKey, drift.Items.ToString());
            db.SetMigrationState(DriftGroupsKey, drift.Groups.ToString());
            db.SetMigrationState(OverCapKey, drift.OverCap.ToString());
            db.SetMigrationState(OverCapNamesKey, string.Join("; ", overCap.Take(5).Select(g => $"{db.GetDedupGroup(g.GroupId!.Value)?.Name} ({g.Members.Count})")));
            db.SetMigrationState(DriftMeasuredKey, DateTime.UtcNow.ToString("o"));
            NocturneLogger.Info("DedupEngine", $"Drift: {drift.Items} item(s) in {drift.Groups} group(s) grouped otherwise than a rebuild would; {drift.OverCap} group(s) over the size cap of {cap} ({clock.ElapsedMilliseconds} ms).");
            return drift;
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("DedupEngine", "Drift measurement failed", ex);
            return null;
        }
    }
}
