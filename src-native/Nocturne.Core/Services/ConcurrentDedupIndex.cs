using System;
using System.Collections.Generic;
using System.Linq;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class DedupGroupRecord
{
    public string GroupId { get; set; } = Guid.NewGuid().ToString("N");
    public string PrimaryItemId { get; set; } = string.Empty;
    public string PrimaryServerId { get; set; } = string.Empty;
    public int PrimaryScore { get; set; }
    public string Type { get; set; } = "Movie";
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public HashSet<string> ItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsModified { get; set; }
}

public class SingleItemRef
{
    public string ItemId { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public EmbyItem? ItemRef { get; set; }
    public int Score { get; set; }
    public string Type { get; set; } = "Movie";
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public List<string> Keys { get; } = new();
}

/// <summary>
/// Incremental concurrent hash-index deduplication engine.
/// Matches media items across servers in real-time as batches arrive from the network,
/// eliminating expensive blocking post-sync deserialization and memory spikes.
/// </summary>
public class ConcurrentDedupIndex
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DedupGroupRecord> _keyToGroup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SingleItemRef> _keyToSingle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DedupGroupRecord> _groups = new(StringComparer.OrdinalIgnoreCase);

    public int MultiVersionGroupsCount
    {
        get
        {
            lock (_lock)
            {
                return _groups.Values.Count(g => g.ItemIds.Count > 1);
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
    /// Loads existing deduplication groups from SQLite into the in-memory hash index.
    /// Fast column-only query that avoids loading or deserializing JSON blobs.
    /// </summary>
    public void InitializeFromDb(DatabaseService db)
    {
        lock (_lock)
        {
            _keyToGroup.Clear();
            _keyToSingle.Clear();
            _groups.Clear();

            try
            {
                var existingGroups = db.GetExistingDedupGroupsForIndex();
                foreach (var (gid, pid, idsStr, tmdb, imdb, title, year, type) in existingGroups)
                {
                    var record = new DedupGroupRecord
                    {
                        GroupId = gid,
                        PrimaryItemId = pid,
                        Type = type,
                        Title = title,
                        Year = year,
                        TmdbId = tmdb,
                        ImdbId = imdb,
                        IsModified = false
                    };

                    if (!string.IsNullOrEmpty(idsStr))
                    {
                        foreach (var id in idsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            record.ItemIds.Add(id);
                        }
                    }

                    _groups[gid] = record;

                    foreach (var key in GenerateKeys(type, title, year, tmdb, imdb))
                    {
                        _keyToGroup[key] = record;
                    }
                }

                NocturneLogger.Info("ConcurrentDedupIndex", $"Initialized hash index with {_groups.Count} existing dedup groups.");
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("ConcurrentDedupIndex", $"Could not initialize existing dedup groups: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Processes an incoming batch of items in real-time:
    /// - Checks keys against existing groups and single items.
    /// - Assigns DedupGroupId directly on each item before SQLite writes.
    /// - Upserts modified groups directly to SQLite.
    /// </summary>
    public void ProcessBatch(IEnumerable<EmbyItem> items, DatabaseService? db = null)
    {
        var dirtyGroups = new List<DedupGroupRecord>();
        var updatedSingles = new List<(string ItemId, string ServerId, string GroupId)>();

        lock (_lock)
        {
            foreach (var item in items)
            {
                // Only Movies and Series participate in multi-server deduplication
                if (item.Type != "Movie" && item.Type != "Series") continue;

                int score = DedupEngine.ScorePrimaryItem(item);
                var keys = GenerateKeys(item.Type, item.Name, item.ProductionYear, item.TmdbId, item.ImdbId);
                if (keys.Count == 0) continue;

                // 1. Check if any key matches an existing group
                DedupGroupRecord? matchedGroup = null;
                foreach (var k in keys)
                {
                    if (_keyToGroup.TryGetValue(k, out var g))
                    {
                        matchedGroup = g;
                        break;
                    }
                }

                if (matchedGroup != null)
                {
                    // Add this item to the matched group
                    matchedGroup.ItemIds.Add(item.Id);
                    item.DedupGroupId = matchedGroup.GroupId;

                    // Promote this item to primary if it has higher score (e.g. 4K, backdrops)
                    if (score > matchedGroup.PrimaryScore)
                    {
                        matchedGroup.PrimaryItemId = item.Id;
                        matchedGroup.PrimaryServerId = item.ServerId ?? string.Empty;
                        matchedGroup.PrimaryScore = score;
                    }

                    if (string.IsNullOrEmpty(matchedGroup.TmdbId) && !string.IsNullOrEmpty(item.TmdbId))
                        matchedGroup.TmdbId = item.TmdbId;
                    if (string.IsNullOrEmpty(matchedGroup.ImdbId) && !string.IsNullOrEmpty(item.ImdbId))
                        matchedGroup.ImdbId = item.ImdbId;

                    // Register any newly discovered keys for this group
                    foreach (var k in keys)
                    {
                        _keyToGroup[k] = matchedGroup;
                    }

                    matchedGroup.IsModified = true;
                    if (!dirtyGroups.Contains(matchedGroup))
                    {
                        dirtyGroups.Add(matchedGroup);
                    }
                    continue;
                }

                // 2. Check if any key matches a previously seen single item
                SingleItemRef? matchedSingle = null;
                foreach (var k in keys)
                {
                    if (_keyToSingle.TryGetValue(k, out var s))
                    {
                        matchedSingle = s;
                        break;
                    }
                }

                if (matchedSingle != null)
                {
                    // Prevent linking an item to itself (e.g. re-sync of same item)
                    if (string.Equals(matchedSingle.ItemId, item.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Duplicate discovered! Form a new 2-item DedupGroup
                    var newGroup = new DedupGroupRecord
                    {
                        GroupId = Guid.NewGuid().ToString("N"),
                        Type = item.Type,
                        Title = item.Name,
                        Year = item.ProductionYear ?? matchedSingle.Year,
                        TmdbId = item.TmdbId ?? matchedSingle.TmdbId,
                        ImdbId = item.ImdbId ?? matchedSingle.ImdbId,
                        IsModified = true
                    };

                    newGroup.ItemIds.Add(matchedSingle.ItemId);
                    newGroup.ItemIds.Add(item.Id);

                    if (score >= matchedSingle.Score)
                    {
                        newGroup.PrimaryItemId = item.Id;
                        newGroup.PrimaryServerId = item.ServerId ?? string.Empty;
                        newGroup.PrimaryScore = score;
                    }
                    else
                    {
                        newGroup.PrimaryItemId = matchedSingle.ItemId;
                        newGroup.PrimaryServerId = matchedSingle.ServerId;
                        newGroup.PrimaryScore = matchedSingle.Score;
                    }

                    _groups[newGroup.GroupId] = newGroup;

                    // Remove single from singletons and map all keys to new group
                    foreach (var k in matchedSingle.Keys)
                    {
                        _keyToSingle.Remove(k);
                        _keyToGroup[k] = newGroup;
                    }
                    foreach (var k in keys)
                    {
                        _keyToGroup[k] = newGroup;
                    }

                    item.DedupGroupId = newGroup.GroupId;
                    if (matchedSingle.ItemRef != null)
                    {
                        matchedSingle.ItemRef.DedupGroupId = newGroup.GroupId;
                    }
                    updatedSingles.Add((matchedSingle.ItemId, matchedSingle.ServerId, newGroup.GroupId));
                    dirtyGroups.Add(newGroup);
                    continue;
                }

                // 3. First time seeing this item: register in singletons
                var singleRef = new SingleItemRef
                {
                    ItemId = item.Id,
                    ServerId = item.ServerId ?? string.Empty,
                    ItemRef = item,
                    Score = score,
                    Type = item.Type,
                    Title = item.Name,
                    Year = item.ProductionYear,
                    TmdbId = item.TmdbId,
                    ImdbId = item.ImdbId
                };
                singleRef.Keys.AddRange(keys);

                foreach (var k in keys)
                {
                    _keyToSingle[k] = singleRef;
                }
            }
        }

        // Flush modified groups and single-item dedup references to SQLite
        if (db != null)
        {
            foreach (var (sId, sSrv, sGid) in updatedSingles)
            {
                db.UpdateItemDedupGroupId(sId, sSrv, sGid);
            }

            foreach (var g in dirtyGroups)
            {
                db.UpsertDedupGroup(g.GroupId, g.PrimaryItemId, g.ItemIds, g.TmdbId, g.ImdbId, g.Title, g.Year, g.Type);
                g.IsModified = false;
            }
        }
    }

    public void FlushAll(DatabaseService db)
    {
        lock (_lock)
        {
            var dirty = _groups.Values.Where(g => g.IsModified).ToList();
            foreach (var g in dirty)
            {
                db.UpsertDedupGroup(g.GroupId, g.PrimaryItemId, g.ItemIds, g.TmdbId, g.ImdbId, g.Title, g.Year, g.Type);
                g.IsModified = false;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _keyToGroup.Clear();
            _keyToSingle.Clear();
            _groups.Clear();
        }
    }

    public static List<string> GenerateKeys(string type, string? title, int? year, string? tmdbId, string? imdbId)
    {
        var keys = new List<string>(3);
        if (!string.IsNullOrEmpty(tmdbId))
        {
            keys.Add($"tmdb_{type}_{tmdbId}");
        }
        if (!string.IsNullOrEmpty(imdbId))
        {
            keys.Add($"imdb_{type}_{imdbId}");
        }
        var norm = DedupEngine.NormalizeTitle(title);
        if (!string.IsNullOrEmpty(norm))
        {
            keys.Add($"name_{type}_{norm}_{year ?? 0}");
        }
        return keys;
    }
}
