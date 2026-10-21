using System;
using System.Collections.Generic;
using System.Linq;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class DedupGroupRecord
{
    public int GroupId { get; set; }
    public string ItemType { get; set; } = "Movie";
    public string PrimaryItemId { get; set; } = string.Empty;
    public string PrimaryServerId { get; set; } = string.Empty;
    public int PrimaryScore { get; set; }
    public int IsProvisional { get; set; } // 1 = fuzzy match only; 0 = has authoritative key
    public int VersionCount { get; set; } = 1;

    // Metadata copied from elected primary
    public string Name { get; set; } = string.Empty;
    public string? SortName { get; set; }
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public string? ImageTag { get; set; }
    public string? BackdropTag { get; set; }
    public float? CommunityRating { get; set; }
    public long? DateCreated { get; set; }
    public int? SeriesGroupId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    // Aggregates computed across all versions
    public int? MaxHeight { get; set; }
    public int Has4k { get; set; }
    public long? MaxBitrate { get; set; }
    public string? AudioFormats { get; set; }

    // Sibling item tracking: (ServerId, ItemId)
    public HashSet<(string ServerId, string ItemId)> ItemIds { get; } = new();
    public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDirty { get; set; }
}

public readonly record struct CandidateKey(string Key, bool IsAuthoritative);

/// <summary>
/// Incremental concurrent hash-index deduplication engine (v4.2).
/// Matches media items across servers in real-time as batches arrive from the network,
/// eliminating expensive blocking post-sync deserialization and memory spikes.
/// Implements stable episode provider keys (B2), 4-row N-Way Merge Matrix (M1/B3),
/// group size caps (M6), and monotonic aggregates (M2/R3.3).
/// </summary>
public class ConcurrentDedupIndex
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (int GroupId, bool IsAuthoritative)> _keyToGroup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DedupGroupRecord> _groups = new();
    private int _nextGroupId = 1;
    private IList<string>? _serverPriority;
    private int _connectedServerCount = 2;

    public void SetServerPriority(IList<string>? priority)
    {
        lock (_lock)
        {
            _serverPriority = priority;
            if (priority != null && priority.Count > 0)
            {
                _connectedServerCount = Math.Max(1, priority.Count);
            }
        }
    }

    public void SetConnectedServerCount(int count)
    {
        lock (_lock)
        {
            _connectedServerCount = Math.Max(1, count);
        }
    }

    public int MultiVersionGroupsCount
    {
        get
        {
            lock (_lock)
            {
                return _groups.Values.Count(g => g.VersionCount > 1 || g.ItemIds.Count > 1);
            }
        }
    }

    public int TotalGroupsCount
    {
        get
        {
            lock (_lock)
            {
                return _groups.Count;
            }
        }
    }

    /// <summary>
    /// Loads existing deduplication groups and keys from SQLite into the in-memory hash index.
    /// Fast column-only query that avoids loading or deserializing JSON blobs.
    /// </summary>
    public void InitializeFromDb(DatabaseService db)
    {
        lock (_lock)
        {
            _keyToGroup.Clear();
            _groups.Clear();
            _nextGroupId = 1;

            try
            {
                var (groups, keys, items) = db.GetDedupStateForIndex();
                foreach (var g in groups)
                {
                    _groups[g.GroupId] = g;
                    if (g.GroupId >= _nextGroupId)
                    {
                        _nextGroupId = g.GroupId + 1;
                    }
                }

                foreach (var (key, gid, isAuth) in keys)
                {
                    _keyToGroup[key] = (gid, isAuth);
                    if (_groups.TryGetValue(gid, out var g))
                    {
                        g.Keys.Add(key);
                    }
                }

                foreach (var (srv, id, gid) in items)
                {
                    if (_groups.TryGetValue(gid, out var g))
                    {
                        g.ItemIds.Add((srv, id));
                    }
                }

                NocturneLogger.Info("ConcurrentDedupIndex", $"Initialized hash index with {_groups.Count} existing groups and {_keyToGroup.Count} keys.");
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("ConcurrentDedupIndex", $"Could not initialize existing dedup groups: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Generates candidate deduplication keys for Movies, Series, and Episodes.
    /// Incorporates stable series provider keys (B2) and guards against null years / generic titles (S6).
    /// </summary>
    public static List<CandidateKey> GenerateCandidateKeys(EmbyItem item)
    {
        var keys = new List<CandidateKey>(4);
        var type = item.Type ?? string.Empty;

        if (string.Equals(type, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            int season = item.ParentIndexNumber ?? 1;
            int ep = item.IndexNumber ?? 0;
            string sXee = $"s{season:D2}e{ep:D2}";

            // 1. Authoritative series provider keys (stable forever across series merges - B2)
            if (!string.IsNullOrEmpty(item.SeriesTvdbId))
                keys.Add(new CandidateKey($"ep:tvdb:{item.SeriesTvdbId}:{sXee}", true));
            if (!string.IsNullOrEmpty(item.SeriesTmdbId))
                keys.Add(new CandidateKey($"ep:tmdb:{item.SeriesTmdbId}:{sXee}", true));
            if (!string.IsNullOrEmpty(item.SeriesImdbId))
                keys.Add(new CandidateKey($"ep:imdb:{item.SeriesImdbId}:{sXee}", true));

            // 2. Direct episode provider keys
            if (!string.IsNullOrEmpty(item.TmdbId))
                keys.Add(new CandidateKey($"tmdb:ep:{item.TmdbId}", true));
            if (!string.IsNullOrEmpty(item.ImdbId))
                keys.Add(new CandidateKey($"imdb:ep:{item.ImdbId}", true));
            if (!string.IsNullOrEmpty(item.TvdbId))
                keys.Add(new CandidateKey($"tvdb:ep:{item.TvdbId}", true));

            // 3. Fallback series/name keys (is_authoritative = false)
            if (!string.IsNullOrEmpty(item.SeriesId))
                keys.Add(new CandidateKey($"ep:series:{item.SeriesId}:{sXee}", false));

            if (!string.IsNullOrEmpty(item.SeriesName))
            {
                var normSeries = DedupEngine.NormalizeTitle(item.SeriesName);
                if (!string.IsNullOrEmpty(normSeries) && !DedupEngine.IsGenericTitle(item.SeriesName))
                {
                    keys.Add(new CandidateKey($"ep:name:{normSeries}:{sXee}", false));
                }
            }
        }
        else
        {
            // Movie or Series
            string normType = string.Equals(type, "Series", StringComparison.OrdinalIgnoreCase) ? "series" : "movie";

            if (!string.IsNullOrEmpty(item.TmdbId))
                keys.Add(new CandidateKey($"tmdb:{normType}:{item.TmdbId}", true));
            if (!string.IsNullOrEmpty(item.ImdbId))
                keys.Add(new CandidateKey($"imdb:{normType}:{item.ImdbId}", true));
            if (!string.IsNullOrEmpty(item.TvdbId))
                keys.Add(new CandidateKey($"tvdb:{normType}:{item.TvdbId}", true));

            // Fuzzy key: ONLY if year is valid (> 0) and title is not generic (S6 Guard)
            if (item.ProductionYear.HasValue && item.ProductionYear.Value > 0 && !DedupEngine.IsGenericTitle(item.Name))
            {
                var normTitle = DedupEngine.NormalizeTitle(item.Name);
                if (!string.IsNullOrEmpty(normTitle))
                {
                    keys.Add(new CandidateKey($"name:{normType}:{normTitle}:{item.ProductionYear.Value}", false));
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Processes an incoming batch of items in real-time within the single writer thread:
    /// - Checks keys against existing groups.
    /// - Executes N-Way Merge Matrix (M1/B3) with group size caps (M6).
    /// - Assigns DedupGroupId directly on each item before SQLite writes.
    /// - Upserts modified groups and remapped keys to SQLite atomically.
    /// </summary>
    public void ProcessBatch(IEnumerable<EmbyItem> items, DatabaseService? db = null)
    {
        var dirtyGroups = new List<DedupGroupRecord>();
        var deletedGroupIds = new List<int>();
        var remappedGroups = new List<(int FromGid, int ToGid)>();
        var keysToSave = new List<(string Key, int GroupId, bool IsAuthoritative)>();
        var itemAssignments = new List<(string ServerId, string ItemId, int GroupId)>();

        lock (_lock)
        {
            foreach (var item in items)
            {
                // Movies, Series, and Episodes participate in deduplication (B1 Option A)
                if (item.Type != "Movie" && item.Type != "Series" && item.Type != "Episode") continue;

                var candidateKeys = GenerateCandidateKeys(item);
                if (candidateKeys.Count == 0) continue;

                int score = DedupEngine.ScorePrimaryItem(item, _serverPriority);
                int height = item.Height ?? 0;
                long bitrate = item.MeasuredBitrate ?? 0;
                int is4k = (height >= 2160 || (item.Width ?? 0) >= 3800) ? 1 : 0;
                string? audio = item.AudioCodec;
                bool isItemAuth = candidateKeys.Any(k => k.IsAuthoritative);

                // Collect matched groups partitioned into authoritative and provisional
                var authMatches = new HashSet<int>();
                var provMatches = new HashSet<int>();
                var authLinkedGroupIds = new HashSet<int>();

                foreach (var ck in candidateKeys)
                {
                    if (_keyToGroup.TryGetValue(ck.Key, out var mapping))
                    {
                        if (_groups.TryGetValue(mapping.GroupId, out var g))
                        {
                            if (g.IsProvisional == 0)
                            {
                                authMatches.Add(g.GroupId);
                                if (ck.IsAuthoritative)
                                {
                                    authLinkedGroupIds.Add(g.GroupId);
                                }
                            }
                            else
                            {
                                provMatches.Add(g.GroupId);
                            }
                        }
                    }
                }

                // ── Case 4: Zero Groups Matched -> CREATE NEW GROUP ──
                if (authMatches.Count == 0 && provMatches.Count == 0)
                {
                    int newGid = _nextGroupId++;
                    var newGroup = new DedupGroupRecord
                    {
                        GroupId = newGid,
                        ItemType = item.Type ?? "Movie",
                        PrimaryItemId = item.Id,
                        PrimaryServerId = item.ServerId ?? string.Empty,
                        PrimaryScore = score,
                        IsProvisional = isItemAuth ? 0 : 1,
                        VersionCount = 1,
                        Name = item.Name,
                        SortName = item.SortName,
                        Year = item.ProductionYear,
                        Overview = item.Overview,
                        ImageTag = item.ImageTag,
                        BackdropTag = item.BackdropTag,
                        CommunityRating = item.CommunityRating,
                        DateCreated = item.PremiereDate.HasValue ? new DateTimeOffset(item.PremiereDate.Value).ToUnixTimeSeconds() : null,
                        SeasonNumber = item.ParentIndexNumber,
                        EpisodeNumber = item.IndexNumber,
                        MaxHeight = height > 0 ? height : null,
                        Has4k = is4k,
                        MaxBitrate = bitrate > 0 ? bitrate : null,
                        AudioFormats = audio,
                        IsDirty = true
                    };
                    newGroup.ItemIds.Add((item.ServerId ?? string.Empty, item.Id));

                    _groups[newGid] = newGroup;

                    foreach (var ck in candidateKeys)
                    {
                        _keyToGroup[ck.Key] = (newGid, ck.IsAuthoritative);
                        newGroup.Keys.Add(ck.Key);
                        keysToSave.Add((ck.Key, newGid, ck.IsAuthoritative));
                    }

                    item.DedupGroupId = newGid.ToString();
                    itemAssignments.Add((item.ServerId ?? string.Empty, item.Id, newGid));
                    dirtyGroups.Add(newGroup);
                    continue;
                }

                // ── Case 1: Multiple Authoritative Groups Reached Authoritatively (>= 2) -> MERGE ALL ──
                if (authLinkedGroupIds.Count >= 2)
                {
                    int winnerId = authLinkedGroupIds.Min();
                    var winner = _groups[winnerId];
                    var loserIds = authMatches.Concat(provMatches).Where(id => id != winnerId).Distinct().ToList();

                    foreach (var loserId in loserIds)
                    {
                        if (_groups.Remove(loserId, out var loser))
                        {
                            deletedGroupIds.Add(loserId);
                            remappedGroups.Add((loserId, winnerId));
                            foreach (var itm in loser.ItemIds) winner.ItemIds.Add(itm);
                            foreach (var k in loser.Keys)
                            {
                                winner.Keys.Add(k);
                                bool isAuthKey = _keyToGroup.TryGetValue(k, out var km) ? km.IsAuthoritative : true;
                                _keyToGroup[k] = (winnerId, isAuthKey);
                                keysToSave.Add((k, winnerId, isAuthKey));
                            }
                            winner.MaxHeight = Math.Max(winner.MaxHeight ?? 0, loser.MaxHeight ?? 0);
                            winner.MaxBitrate = Math.Max(winner.MaxBitrate ?? 0, loser.MaxBitrate ?? 0);
                            winner.Has4k = Math.Max(winner.Has4k, loser.Has4k);
                            AppendAudioCodec(winner, loser.AudioFormats);
                        }
                    }

                    winner.ItemIds.Add((item.ServerId ?? string.Empty, item.Id));
                    winner.VersionCount = winner.ItemIds.Count;
                    winner.MaxHeight = Math.Max(winner.MaxHeight ?? 0, height);
                    winner.MaxBitrate = Math.Max(winner.MaxBitrate ?? 0, bitrate);
                    winner.Has4k = Math.Max(winner.Has4k, is4k);
                    AppendAudioCodec(winner, audio);
                    winner.IsProvisional = 0;

                    if (score > winner.PrimaryScore)
                    {
                        UpdatePrimary(winner, item, score);
                    }

                    foreach (var ck in candidateKeys)
                    {
                        winner.Keys.Add(ck.Key);
                        _keyToGroup[ck.Key] = (winnerId, ck.IsAuthoritative);
                        keysToSave.Add((ck.Key, winnerId, ck.IsAuthoritative));
                    }

                    item.DedupGroupId = winnerId.ToString();
                    itemAssignments.Add((item.ServerId ?? string.Empty, item.Id, winnerId));
                    winner.IsDirty = true;
                    if (!dirtyGroups.Contains(winner)) dirtyGroups.Add(winner);
                    continue;
                }

                // ── Case 2: One Authoritative Group Reached Authoritatively (== 1) -> ATTACH & ABSORB PROVISIONAL ──
                if (authLinkedGroupIds.Count == 1)
                {
                    int targetId = authLinkedGroupIds.First();
                    var target = _groups[targetId];

                    // Absorb any matched provisional groups into the authoritative group
                    foreach (var provId in provMatches)
                    {
                        if (_groups.Remove(provId, out var provLoser))
                        {
                            deletedGroupIds.Add(provId);
                            remappedGroups.Add((provId, targetId));
                            foreach (var itm in provLoser.ItemIds) target.ItemIds.Add(itm);
                            foreach (var k in provLoser.Keys)
                            {
                                target.Keys.Add(k);
                                bool isAuthKey = _keyToGroup.TryGetValue(k, out var km) ? km.IsAuthoritative : true;
                                _keyToGroup[k] = (targetId, isAuthKey);
                                keysToSave.Add((k, targetId, isAuthKey));
                            }
                            target.MaxHeight = Math.Max(target.MaxHeight ?? 0, provLoser.MaxHeight ?? 0);
                            target.MaxBitrate = Math.Max(target.MaxBitrate ?? 0, provLoser.MaxBitrate ?? 0);
                            target.Has4k = Math.Max(target.Has4k, provLoser.Has4k);
                            AppendAudioCodec(target, provLoser.AudioFormats);
                        }
                    }

                    target.ItemIds.Add((item.ServerId ?? string.Empty, item.Id));
                    target.VersionCount = target.ItemIds.Count;
                    target.MaxHeight = Math.Max(target.MaxHeight ?? 0, height);
                    target.MaxBitrate = Math.Max(target.MaxBitrate ?? 0, bitrate);
                    target.Has4k = Math.Max(target.Has4k, is4k);
                    AppendAudioCodec(target, audio);
                    if (isItemAuth) target.IsProvisional = 0;

                    if (score > target.PrimaryScore)
                    {
                        UpdatePrimary(target, item, score);
                    }

                    foreach (var ck in candidateKeys)
                    {
                        target.Keys.Add(ck.Key);
                        _keyToGroup[ck.Key] = (targetId, ck.IsAuthoritative);
                        keysToSave.Add((ck.Key, targetId, ck.IsAuthoritative));
                    }

                    item.DedupGroupId = targetId.ToString();
                    itemAssignments.Add((item.ServerId ?? string.Empty, item.Id, targetId));
                    target.IsDirty = true;
                    if (!dirtyGroups.Contains(target)) dirtyGroups.Add(target);
                    // Remake protection: Any other auth groups reached strictly via fuzzy keys are refused merge!
                    continue;
                }

                // ── Case 3: Only Provisional Groups Matched (or reached fuzzily) ──
                if (provMatches.Count > 0)
                {
                    if (isItemAuth)
                    {
                        // Inbound item is authoritative: Merge all provisional groups into lowest id
                        int winnerId = provMatches.Min();
                        var winner = _groups[winnerId];
                        var provLosers = provMatches.Where(id => id != winnerId).ToList();

                        foreach (var loserId in provLosers)
                        {
                            if (_groups.Remove(loserId, out var loser))
                            {
                                deletedGroupIds.Add(loserId);
                                remappedGroups.Add((loserId, winnerId));
                                foreach (var itm in loser.ItemIds) winner.ItemIds.Add(itm);
                                foreach (var k in loser.Keys)
                                {
                                    winner.Keys.Add(k);
                                    bool isAuthKey = _keyToGroup.TryGetValue(k, out var km) ? km.IsAuthoritative : true;
                                    _keyToGroup[k] = (winnerId, isAuthKey);
                                    keysToSave.Add((k, winnerId, isAuthKey));
                                }
                                winner.MaxHeight = Math.Max(winner.MaxHeight ?? 0, loser.MaxHeight ?? 0);
                                winner.MaxBitrate = Math.Max(winner.MaxBitrate ?? 0, loser.MaxBitrate ?? 0);
                                winner.Has4k = Math.Max(winner.Has4k, loser.Has4k);
                                AppendAudioCodec(winner, loser.AudioFormats);
                            }
                        }

                        winner.ItemIds.Add((item.ServerId ?? string.Empty, item.Id));
                        winner.VersionCount = winner.ItemIds.Count;
                        winner.MaxHeight = Math.Max(winner.MaxHeight ?? 0, height);
                        winner.MaxBitrate = Math.Max(winner.MaxBitrate ?? 0, bitrate);
                        winner.Has4k = Math.Max(winner.Has4k, is4k);
                        AppendAudioCodec(winner, audio);
                        winner.IsProvisional = 0; // cleared

                        if (score > winner.PrimaryScore)
                        {
                            UpdatePrimary(winner, item, score);
                        }

                        foreach (var ck in candidateKeys)
                        {
                            winner.Keys.Add(ck.Key);
                            _keyToGroup[ck.Key] = (winnerId, ck.IsAuthoritative);
                            keysToSave.Add((ck.Key, winnerId, ck.IsAuthoritative));
                        }

                        item.DedupGroupId = winnerId.ToString();
                        itemAssignments.Add((item.ServerId ?? string.Empty, item.Id, winnerId));
                        winner.IsDirty = true;
                        if (!dirtyGroups.Contains(winner)) dirtyGroups.Add(winner);
                    }
                    else
                    {
                        // Fuzzy link only: attach to lowest provisional group without merging others
                        int targetId = provMatches.Min();
                        var target = _groups[targetId];

                        // Group Size Cap Guard (M6 & R4.Minor): cap fuzzy attach at 4 * serverCount
                        int maxGroupCap = 4 * Math.Max(2, _connectedServerCount);
                        if (target.VersionCount >= maxGroupCap)
                        {
                            NocturneLogger.Warn("ConcurrentDedupIndex", $"Fuzzy attach rejected: group {targetId} reached cap of {target.VersionCount} versions. Creating new group for '{item.Name}'.");

                            int newGid = _nextGroupId++;
                            var newGroup = new DedupGroupRecord
                            {
                                GroupId = newGid,
                                ItemType = item.Type ?? "Movie",
                                PrimaryItemId = item.Id,
                                PrimaryServerId = item.ServerId ?? string.Empty,
                                PrimaryScore = score,
                                IsProvisional = 1,
                                VersionCount = 1,
                                Name = item.Name,
                                SortName = item.SortName,
                                Year = item.ProductionYear,
                                Overview = item.Overview,
                                ImageTag = item.ImageTag,
                                BackdropTag = item.BackdropTag,
                                CommunityRating = item.CommunityRating,
                                MaxHeight = height > 0 ? height : null,
                                Has4k = is4k,
                                MaxBitrate = bitrate > 0 ? bitrate : null,
                                AudioFormats = audio,
                                IsDirty = true
                            };
                            newGroup.ItemIds.Add((item.ServerId ?? string.Empty, item.Id));
                            _groups[newGid] = newGroup;

                            foreach (var ck in candidateKeys)
                            {
                                _keyToGroup[ck.Key] = (newGid, ck.IsAuthoritative);
                                newGroup.Keys.Add(ck.Key);
                                keysToSave.Add((ck.Key, newGid, ck.IsAuthoritative));
                            }

                            item.DedupGroupId = newGid.ToString();
                            itemAssignments.Add((item.ServerId ?? string.Empty, item.Id, newGid));
                            dirtyGroups.Add(newGroup);
                        }
                        else
                        {
                            target.ItemIds.Add((item.ServerId ?? string.Empty, item.Id));
                            target.VersionCount = target.ItemIds.Count;
                            target.MaxHeight = Math.Max(target.MaxHeight ?? 0, height);
                            target.MaxBitrate = Math.Max(target.MaxBitrate ?? 0, bitrate);
                            target.Has4k = Math.Max(target.Has4k, is4k);
                            AppendAudioCodec(target, audio);

                            if (score > target.PrimaryScore)
                            {
                                UpdatePrimary(target, item, score);
                            }

                            foreach (var ck in candidateKeys)
                            {
                                target.Keys.Add(ck.Key);
                                _keyToGroup[ck.Key] = (targetId, ck.IsAuthoritative);
                                keysToSave.Add((ck.Key, targetId, ck.IsAuthoritative));
                            }

                            item.DedupGroupId = targetId.ToString();
                            itemAssignments.Add((item.ServerId ?? string.Empty, item.Id, targetId));
                            target.IsDirty = true;
                            if (!dirtyGroups.Contains(target)) dirtyGroups.Add(target);
                        }
                    }
                    continue;
                }

                // If only authMatches exist but none were auth-linked, attach to lowest auth group (remake guard)
                if (authMatches.Count > 0)
                {
                    int targetId = authMatches.Min();
                    var target = _groups[targetId];

                    target.ItemIds.Add((item.ServerId ?? string.Empty, item.Id));
                    target.VersionCount = target.ItemIds.Count;
                    target.MaxHeight = Math.Max(target.MaxHeight ?? 0, height);
                    target.MaxBitrate = Math.Max(target.MaxBitrate ?? 0, bitrate);
                    target.Has4k = Math.Max(target.Has4k, is4k);
                    AppendAudioCodec(target, audio);

                    if (score > target.PrimaryScore)
                    {
                        UpdatePrimary(target, item, score);
                    }

                    foreach (var ck in candidateKeys)
                    {
                        target.Keys.Add(ck.Key);
                        _keyToGroup[ck.Key] = (targetId, ck.IsAuthoritative);
                        keysToSave.Add((ck.Key, targetId, ck.IsAuthoritative));
                    }

                    item.DedupGroupId = targetId.ToString();
                    itemAssignments.Add((item.ServerId ?? string.Empty, item.Id, targetId));
                    target.IsDirty = true;
                    if (!dirtyGroups.Contains(target)) dirtyGroups.Add(target);
                }
            }
        }

        // Flush modified groups, remapped keys, and item group assignments to SQLite in a single transaction
        if (db != null && (dirtyGroups.Count > 0 || deletedGroupIds.Count > 0 || keysToSave.Count > 0 || itemAssignments.Count > 0))
        {
            db.SaveDedupBatch(dirtyGroups, deletedGroupIds, remappedGroups, keysToSave, itemAssignments);
        }
    }

    public void FlushAll(DatabaseService db)
    {
        lock (_lock)
        {
            var dirty = _groups.Values.Where(g => g.IsDirty).ToList();
            if (dirty.Count > 0)
            {
                db.SaveDedupBatch(dirty, new List<int>(), new List<(int, int)>(), new List<(string, int, bool)>());
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _keyToGroup.Clear();
            _groups.Clear();
            _nextGroupId = 1;
        }
    }

    public (List<DedupGroupRecord> Groups, List<(string Key, int GroupId, bool IsAuthoritative)> Keys, List<(string ServerId, string ItemId, int GroupId)> Items) GetRebuildState()
    {
        lock (_lock)
        {
            var groups = _groups.Values.ToList();
            var keys = _keyToGroup.Select(kv => (kv.Key, kv.Value.GroupId, kv.Value.IsAuthoritative)).ToList();
            var items = new List<(string ServerId, string ItemId, int GroupId)>();
            foreach (var g in groups)
            {
                foreach (var (srv, id) in g.ItemIds)
                {
                    items.Add((srv, id, g.GroupId));
                }
            }
            return (groups, keys, items);
        }
    }

    private static void AppendAudioCodec(DedupGroupRecord g, string? audio)
    {
        if (string.IsNullOrWhiteSpace(audio)) return;
        if (string.IsNullOrEmpty(g.AudioFormats))
        {
            g.AudioFormats = audio;
            return;
        }
        var existing = g.AudioFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!existing.Contains(audio, StringComparer.OrdinalIgnoreCase))
        {
            g.AudioFormats = $"{g.AudioFormats},{audio}";
        }
    }

    private static void UpdatePrimary(DedupGroupRecord g, EmbyItem item, int score)
    {
        g.PrimaryItemId = item.Id;
        g.PrimaryServerId = item.ServerId ?? string.Empty;
        g.PrimaryScore = score;
        g.Name = item.Name;
        g.SortName = item.SortName;
        g.Year = item.ProductionYear ?? g.Year;
        g.Overview = item.Overview ?? g.Overview;
        g.ImageTag = item.ImageTag ?? g.ImageTag;
        g.BackdropTag = item.BackdropTag ?? g.BackdropTag;
        g.CommunityRating = item.CommunityRating ?? g.CommunityRating;
    }
}
