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

    // Artwork election & TMDB key (v4.3 Trakt/TMDB spec §3)
    public string? ImageItemId { get; set; }
    public string? ImageServerId { get; set; }
    public string? BackdropItemId { get; set; }
    public string? BackdropServerId { get; set; }
    public string? TmdbKey { get; set; }

    // Sibling item tracking: (ServerId, ItemId) -> the episode's (season, episode) on its server, null otherwise
    public Dictionary<(string ServerId, string ItemId), (int Season, int Episode)?> ItemIds { get; } = new();
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
    // migration_state marker: set once the groups in the database were built by this (conflict-guarded) engine.
    // FeatureFlags.SiblingCascade engages only then. Bump the version when grouping rules change.
    public const string EngineMarkerKey = "dedup_engine";
    public const string EngineVersion = "4";

    private readonly object _lock = new();
    private Dictionary<string, (int GroupId, bool IsAuthoritative)> _keyToGroup = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, DedupGroupRecord> _groups = new();
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

    // What the index holds: every group during a rebuild; with a database, the groups of the last batch.
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

    // Sync start. Nothing is loaded: each ProcessBatch given a database reads only what its items' keys reach
    // (LoadWorkingSet). Only the next group id is read here, and a failure throws: an index minting ids from 1
    // would have SaveDedupBatch's ON CONFLICT(group_id) upsert write new groups over existing ones (D1).
    public void InitializeFromDb(DatabaseService db)
    {
        var next = db.GetMaxGroupId() + 1;
        lock (_lock)
        {
            _groups = new Dictionary<int, DedupGroupRecord>();
            _keyToGroup = new Dictionary<string, (int GroupId, bool IsAuthoritative)>(StringComparer.OrdinalIgnoreCase);
            _nextGroupId = next;
        }
        _seriesProviderIds.Clear();

        // An empty database is populated entirely by this engine. Outside _lock: the rebuild path takes _dbLock first.
        if (next == 1) db.SetMigrationState(EngineMarkerKey, EngineVersion);
    }

    // With a database the index is a per-batch cache of it: one indexed lookup per candidate key, then each reached
    // group's row, keys and members, replacing what the previous batch held. A resident copy of every group took
    // 54 s and gigabytes at each sync start (#19 item 6). Without a database (a rebuild) the index stays resident.
    private void LoadWorkingSet(IList<EmbyItem> batch, DatabaseService db, bool skipUntagged)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in batch)
        {
            if (item.Type != "Movie" && item.Type != "Series" && item.Type != "Episode") continue;
            if (skipUntagged && IsUntaggedEpisode(item)) continue;
            foreach (var ck in GenerateCandidateKeys(item)) wanted.Add(ck.Key);
            wanted.Add(IdentityKey(item).Key);
        }
        var (found, groups, groupKeys, members) = db.LoadDedupWorkingSet(wanted);
        lock (_lock)
        {
            _groups = groups.ToDictionary(g => g.GroupId);
            _keyToGroup = new Dictionary<string, (int GroupId, bool IsAuthoritative)>(found, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, gid, isAuth) in groupKeys)
            {
                _keyToGroup[key] = (gid, isAuth);
                if (_groups.TryGetValue(gid, out var g)) g.Keys.Add(key);
            }
            foreach (var (srv, id, gid, position) in members)
            {
                if (_groups.TryGetValue(gid, out var g)) g.ItemIds[(srv, id)] = position;
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
            // Season/episode keys only when both numbers are known: defaulting a missing number (to s01 / e00)
            // collapses unrelated episodes onto one key, the same failure the S6 guard prevents for null years.
            string? sXee = item.ParentIndexNumber.HasValue && item.IndexNumber.HasValue
                ? $"s{item.ParentIndexNumber.Value:D2}e{item.IndexNumber.Value:D2}"
                : null;

            if (sXee != null)
            {
                // 1. Authoritative series provider keys (stable forever across series merges - B2)
                if (IsValidProviderId("Tvdb", item.SeriesTvdbId))
                    keys.Add(new CandidateKey($"ep:tvdb:{item.SeriesTvdbId}:{sXee}", true));
                if (IsValidProviderId("Tmdb", item.SeriesTmdbId))
                    keys.Add(new CandidateKey($"ep:tmdb:{item.SeriesTmdbId}:{sXee}", true));
                if (IsValidProviderId("Imdb", item.SeriesImdbId))
                    keys.Add(new CandidateKey($"ep:imdb:{item.SeriesImdbId}:{sXee}", true));
            }

            // 2. Direct episode provider keys
            if (IsValidProviderId("Tmdb", item.TmdbId))
                keys.Add(new CandidateKey($"tmdb:ep:{item.TmdbId}", true));
            if (IsValidProviderId("Imdb", item.ImdbId))
                keys.Add(new CandidateKey($"imdb:ep:{item.ImdbId}", true));
            if (IsValidProviderId("Tvdb", item.TvdbId))
                keys.Add(new CandidateKey($"tvdb:ep:{item.TvdbId}", true));

            // 3. Fallback series/name keys (is_authoritative = false)
            if (sXee != null)
            {
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
        }
        else
        {
            // Movie or Series
            string normType = string.Equals(type, "Series", StringComparison.OrdinalIgnoreCase) ? "series" : "movie";

            if (IsValidProviderId("Tmdb", item.TmdbId))
                keys.Add(new CandidateKey($"tmdb:{normType}:{item.TmdbId}", true));
            if (IsValidProviderId("Imdb", item.ImdbId))
                keys.Add(new CandidateKey($"imdb:{normType}:{item.ImdbId}", true));
            if (IsValidProviderId("Tvdb", item.TvdbId))
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

    // Servers hold malformed ids ("../tv/222031", bare digits in the Imdb slot, URL fragments); keying on them
    // groups unrelated items, so only well-formed ids become keys.
    public static bool IsValidProviderId(string provider, string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (provider == "Imdb")
            return value.Length > 2 && value.StartsWith("tt", StringComparison.Ordinal) && value.Skip(2).All(char.IsAsciiDigit);
        return value.All(char.IsAsciiDigit);
    }

    // Stable key for an item with no usable key: it still gets a (singleton) group, and re-ingesting it finds
    // that group instead of minting a new one.
    private static CandidateKey IdentityKey(EmbyItem item) => new($"item:{item.ServerId}:{item.Id}", false);

    private static (int Season, int Episode)? PositionOf(EmbyItem item) =>
        item.Type == "Episode" && item.ParentIndexNumber is int s && item.IndexNumber is int e ? (s, e) : null;

    // Season/episode keys ("ep:tvdb:{series}:s02e10", "ep:series:...", "ep:name:..."), as opposed to the
    // episode's own provider ids ("tvdb:ep:{id}").
    private static bool IsPositionalKey(string key) => key.StartsWith("ep:", StringComparison.OrdinalIgnoreCase);

    // An episode with no id of its own matches only by season/episode, and the positional rule can judge that
    // only once its server's tagged copies are in their groups. Callers place these after everything else in a sync
    // or rebuild (ProcessBatch UntaggedEpisodes); an untagged file that arrived first joined by number unchecked.
    public static bool IsUntaggedEpisode(EmbyItem item) =>
        PositionOf(item) != null && !GenerateCandidateKeys(item).Any(k => k.IsAuthoritative && !IsPositionalKey(k.Key));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string ServerId, string SeriesId), Dictionary<string, string>> _seriesProviderIds = new();

    /// <summary>
    /// Episodes carry only the server-local SeriesId. Their series' provider ids (for the ep:tvdb/tmdb/imdb keys)
    /// are taken from the Series row, from this batch or the local cache; no server round-trip.
    /// </summary>
    public void ResolveSeriesProviderIds(IEnumerable<EmbyItem> items, DatabaseService db)
    {
        var list = items as IList<EmbyItem> ?? items.ToList();
        foreach (var s in list)
        {
            if (s.Type == "Series" && s.ProviderIds is { Count: > 0 } && !string.IsNullOrEmpty(s.Id))
                _seriesProviderIds[(s.ServerId ?? string.Empty, s.Id)] = s.ProviderIds;
        }

        foreach (var ep in list)
        {
            if (ep.Type != "Episode" || ep.SeriesProviderIds != null || string.IsNullOrEmpty(ep.SeriesId)) continue;
            var key = (ep.ServerId ?? string.Empty, ep.SeriesId);
            if (!_seriesProviderIds.TryGetValue(key, out var ids))
            {
                // Not cached as a miss: the Series row may arrive later in the same sync.
                ids = db.GetSeriesProviderIds(key.Item1, key.Item2);
                if (ids == null) continue;
                _seriesProviderIds[key] = ids;
            }
            ep.SeriesProviderIds = ids;
        }
    }

    // Authoritative keys split into provider type ("tmdb:movie", "ep:tvdb", "tvdb:ep", ...) and value. A
    // season/episode key's value is its series id: two servers numbering one episode differently hold the same
    // series, not different ids. Numbering is judged by the positional rule in ProcessBatch.
    private static (string Type, string Value)? ProviderTypeOf(string key)
    {
        var first = key.IndexOf(':');
        var second = first < 0 ? -1 : key.IndexOf(':', first + 1);
        if (second < 0) return null;
        var value = key[(second + 1)..];
        if (IsPositionalKey(key) && value.LastIndexOf(':') is var sxe and > 0) value = value[..sxe];
        return (key[..second], value);
    }

    // Keys GenerateCandidateKeys emits as authoritative: provider ids, as opposed to fuzzy name/series keys.
    private static readonly string[] ProviderKeyPrefixes = { "tmdb:", "imdb:", "tvdb:", "ep:tvdb:", "ep:tmdb:", "ep:imdb:" };

    private static bool IsProviderKey(string key) => ProviderKeyPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // The ids a group's items carry: every provider key it owns, demoted ones included (demotion stops a key
    // from linking, not from being evidence), plus its "{key}@{groupId}" evidence keys (RecordEvidence).
    private Dictionary<string, HashSet<string>> AuthIdsOf(DedupGroupRecord g)
    {
        var ids = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var owned in g.Keys)
        {
            if (!_keyToGroup.TryGetValue(owned, out var m) || m.GroupId != g.GroupId) continue;
            var at = owned.IndexOf('@');
            var k = at < 0 ? owned : owned[..at];
            if (!IsProviderKey(k) || ProviderTypeOf(k) is not { } p) continue;
            if (!ids.TryGetValue(p.Type, out var set)) ids[p.Type] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(p.Value);
        }
        return ids;
    }

    private static Dictionary<string, HashSet<string>> AuthIdsOf(IEnumerable<CandidateKey> keys)
    {
        var ids = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            if (!k.IsAuthoritative || ProviderTypeOf(k.Key) is not { } p) continue;
            if (!ids.TryGetValue(p.Type, out var set)) ids[p.Type] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(p.Value);
        }
        return ids;
    }

    // C4: two identities conflict when they carry different ids of the same provider type. Agreement on one
    // provider never outweighs disagreement on another. Returns the conflicting type, or null.
    private static string? FindConflict(Dictionary<string, HashSet<string>> a, Dictionary<string, HashSet<string>> b)
    {
        foreach (var (type, values) in a)
        {
            if (b.TryGetValue(type, out var other) && !values.SetEquals(other)) return type;
        }
        return null;
    }

    private int _conflictsRefused;
    public int ConflictsRefused { get { lock (_lock) return _conflictsRefused; } }

    private void LogRefusal(string message)
    {
        // Bounded: a rebuild over a badly-tagged library can refuse thousands of merges.
        if (++_conflictsRefused <= 100) NocturneLogger.Warn("ConcurrentDedupIndex", message);
        else if (_conflictsRefused == 101) NocturneLogger.Warn("ConcurrentDedupIndex", "Further merge refusals are counted but not logged.");
    }

    // A key that led to a group the item conflicts with is suspect: it is demoted to non-authoritative where it
    // stands, and the item does not take it over.
    private List<CandidateKey> DropSuspectKeys(List<CandidateKey> candidateKeys, int ownerGroupId, List<(string Key, int GroupId, bool IsAuthoritative)> keysToSave)
    {
        var kept = new List<CandidateKey>(candidateKeys.Count);
        foreach (var ck in candidateKeys)
        {
            if (_keyToGroup.TryGetValue(ck.Key, out var m) && m.GroupId == ownerGroupId)
            {
                if (m.IsAuthoritative)
                {
                    _keyToGroup[ck.Key] = (ownerGroupId, false);
                    keysToSave.Add((ck.Key, ownerGroupId, false));
                }
                continue;
            }
            kept.Add(ck);
        }
        return kept;
    }

    // A group takes only keys no live group holds. Taking another group's key erases that group's conflict
    // evidence, and re-asserting a key this group demoted undoes the suspect flag. Returns false if any was held.
    private bool ClaimKeys(DedupGroupRecord g, IEnumerable<CandidateKey> keys, List<(string Key, int GroupId, bool IsAuthoritative)> keysToSave)
    {
        bool all = true;
        foreach (var ck in keys)
        {
            if (_keyToGroup.TryGetValue(ck.Key, out var owner) && _groups.ContainsKey(owner.GroupId))
            {
                if (owner.GroupId != g.GroupId) all = false;
                continue;
            }
            g.Keys.Add(ck.Key);
            _keyToGroup[ck.Key] = (g.GroupId, ck.IsAuthoritative);
            keysToSave.Add((ck.Key, g.GroupId, ck.IsAuthoritative));
        }
        return all;
    }

    // A provider id an item carries but its group does not own (another group holds the key: a refused merge,
    // or a key ClaimKeys would not take) is stored as "{key}@{groupId}". No lookup matches that string, so it
    // never links; it keeps the id visible to the conflict guard, including after a reload from the database.
    private void RecordEvidence(DedupGroupRecord g, EmbyItem item, List<(string Key, int GroupId, bool IsAuthoritative)> keysToSave)
    {
        foreach (var ck in GenerateCandidateKeys(item))
        {
            if (!ck.IsAuthoritative) continue;
            if (_keyToGroup.TryGetValue(ck.Key, out var owner) && owner.GroupId == g.GroupId) continue;
            var evidence = $"{ck.Key}@{g.GroupId}";
            if (_keyToGroup.ContainsKey(evidence)) continue;
            g.Keys.Add(evidence);
            _keyToGroup[evidence] = (g.GroupId, false);
            keysToSave.Add((evidence, g.GroupId, false));
        }
    }

    private void CreateGroup(
        EmbyItem item,
        List<CandidateKey> candidateKeys,
        int score,
        int height,
        long bitrate,
        int is4k,
        string? audio,
        string? itemTmdbKey,
        List<(string Key, int GroupId, bool IsAuthoritative)> keysToSave,
        List<(string ServerId, string ItemId, int GroupId)> itemAssignments,
        List<DedupGroupRecord> dirtyGroups,
        bool held = false)
    {
        int newGid = _nextGroupId++;
        var newGroup = new DedupGroupRecord
        {
            GroupId = newGid,
            ItemType = item.Type ?? "Movie",
            PrimaryItemId = item.Id,
            PrimaryServerId = item.ServerId ?? string.Empty,
            PrimaryScore = score,
            VersionCount = 1,
            Name = item.Name,
            SortName = item.SortName,
            Year = item.ProductionYear,
            Overview = item.Overview,
            ImageTag = item.ImageTag,
            ImageItemId = !string.IsNullOrEmpty(item.ImageTag) ? item.Id : null,
            ImageServerId = !string.IsNullOrEmpty(item.ImageTag) ? item.ServerId : null,
            BackdropTag = item.BackdropTag,
            BackdropItemId = !string.IsNullOrEmpty(item.BackdropTag) ? item.Id : null,
            BackdropServerId = !string.IsNullOrEmpty(item.BackdropTag) ? item.ServerId : null,
            TmdbKey = itemTmdbKey,
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
        newGroup.ItemIds[(item.ServerId ?? string.Empty, item.Id)] = PositionOf(item);
        _groups[newGid] = newGroup;

        if (held)
        {
            newGroup.IsProvisional = HeldGroup; // no keys, not even the identity key: the end pass places the item
        }
        else
        {
            // An item split off (conflict or cap) whose keys other groups hold gets its identity key, so the next
            // ingest finds this group instead of minting another.
            if (!ClaimKeys(newGroup, candidateKeys, keysToSave) || newGroup.Keys.Count == 0)
                ClaimKeys(newGroup, new[] { IdentityKey(item) }, keysToSave);
            newGroup.IsProvisional = newGroup.Keys.Any(k => _keyToGroup[k].IsAuthoritative) ? 0 : 1;
            RecordEvidence(newGroup, item, keysToSave);
        }

        item.DedupGroupId = newGid.ToString();
        itemAssignments.Add((item.ServerId ?? string.Empty, item.Id, newGid));
        dirtyGroups.Add(newGroup);
    }

    // Absorbs each loser into the winner unless their authoritative ids conflict (C4). A refused loser keeps
    // its identity; the item's keys that point at it become suspect. Returns the item's remaining keys.
    private List<CandidateKey> MergeInto(
        DedupGroupRecord winner,
        IEnumerable<int> loserIds,
        EmbyItem item,
        List<CandidateKey> candidateKeys,
        List<int> deletedGroupIds,
        List<(int FromGid, int ToGid)> remappedGroups,
        List<(string Key, int GroupId, bool IsAuthoritative)> keysToSave)
    {
        foreach (var loserId in loserIds.Where(id => id != winner.GroupId).Distinct().OrderBy(id => id).ToList())
        {
            if (!_groups.TryGetValue(loserId, out var loser)) continue;
            if (FindConflict(AuthIdsOf(winner), AuthIdsOf(loser)) is { } clash)
            {
                LogRefusal($"Merge refused: group {winner.GroupId} '{winner.Name}' and group {loserId} '{loser.Name}' hold different {clash} ids (via '{item.Name}' {item.ServerId}/{item.Id}).");
                candidateKeys = DropSuspectKeys(candidateKeys, loserId, keysToSave);
                continue;
            }
            _groups.Remove(loserId);
            AbsorbLoserGroup(winner, loser, deletedGroupIds, remappedGroups, keysToSave);
        }
        return candidateKeys;
    }

    /// <summary>
    /// Processes an incoming batch of items in real-time within the single writer thread:
    /// - Checks keys against existing groups.
    /// - Executes N-Way Merge Matrix (M1/B3) with group size caps (M6) and the conflicting-id guard (C4).
    /// - Assigns DedupGroupId directly on each item before SQLite writes.
    /// - Upserts modified groups and remapped keys to SQLite atomically.
    /// </summary>
    // What a batch does with untagged episodes (IsUntaggedEpisode). Defer skips them (the rebuild keeps them for
    // its last pass). Hold, for a sync, writes each into a held group of its own (IsProvisional = HeldGroup) so it
    // shows in lists at once: the group has no keys, so nothing matches into it, and the sync's end pass releases
    // it and places the item exactly as a deferred one, keeping the partition a rebuild produces.
    public enum UntaggedEpisodes { Match, Defer, Hold }
    public const int HeldGroup = 2;

    public void ProcessBatch(IEnumerable<EmbyItem> items, DatabaseService? db = null, UntaggedEpisodes untagged = UntaggedEpisodes.Match)
    {
        var dirtyGroups = new List<DedupGroupRecord>();
        var deletedGroupIds = new List<int>();
        var remappedGroups = new List<(int FromGid, int ToGid)>();
        var keysToSave = new List<(string Key, int GroupId, bool IsAuthoritative)>();
        var itemAssignments = new List<(string ServerId, string ItemId, int GroupId)>();

        var batch = items as IList<EmbyItem> ?? items.ToList();
        if (db != null)
        {
            ResolveSeriesProviderIds(batch, db);
            LoadWorkingSet(batch, db, untagged != UntaggedEpisodes.Match);
        }

        lock (_lock)
        {
            int maxGroupCap = 4 * Math.Max(2, _connectedServerCount);

            foreach (var item in batch)
            {
                // Movies, Series, and Episodes participate in deduplication (B1 Option A)
                if (item.Type != "Movie" && item.Type != "Series" && item.Type != "Episode") continue;

                var candidateKeys = GenerateCandidateKeys(item);

                int score = DedupEngine.ScorePrimaryItem(item, _serverPriority);
                int height = item.Height ?? 0;
                long bitrate = item.MeasuredBitrate ?? 0;
                int is4k = (height >= 2160 || (item.Width ?? 0) >= 3800) ? 1 : 0;
                string? audio = item.AudioCodec;
                string? itemTmdbKey = ExtractTmdbKey(item);

                if (untagged != UntaggedEpisodes.Match && IsUntaggedEpisode(item))
                {
                    if (untagged == UntaggedEpisodes.Hold)
                        CreateGroup(item, new List<CandidateKey>(), score, height, bitrate, is4k, audio, itemTmdbKey, keysToSave, itemAssignments, dirtyGroups, held: true);
                    continue;
                }

                // An item holding an identity key was split off earlier (or has no usable key): it stays there.
                // ponytail: a server-side id fix doesn't re-merge it until the next rebuild.
                if (_keyToGroup.TryGetValue(IdentityKey(item).Key, out var home) && _groups.TryGetValue(home.GroupId, out var homeGroup))
                {
                    AttachItemToGroupRecord(homeGroup, item, height, bitrate, is4k, audio, score, itemTmdbKey, candidateKeys, keysToSave, itemAssignments, dirtyGroups);
                    continue;
                }
                if (candidateKeys.Count == 0) candidateKeys.Add(IdentityKey(item));

                // Collect matched groups partitioned into authoritative and provisional
                var authMatches = new HashSet<int>();
                var provMatches = new HashSet<int>();
                var authLinkedGroupIds = new HashSet<int>();
                var idReached = new HashSet<int>(); // reached through a key that is not positional

                foreach (var ck in candidateKeys)
                {
                    if (_keyToGroup.TryGetValue(ck.Key, out var mapping))
                    {
                        if (_groups.TryGetValue(mapping.GroupId, out var g))
                        {
                            if (!IsPositionalKey(ck.Key)) idReached.Add(g.GroupId);
                            if (g.IsProvisional == 0)
                            {
                                authMatches.Add(g.GroupId);
                                if (ck.IsAuthoritative && mapping.IsAuthoritative)
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

                // C4: never join a group that holds a different authoritative id of a provider type the item
                // carries (The Banker / Bunker shared a bad tvdb:movie id while holding different tmdb ids).
                var itemIds = AuthIdsOf(candidateKeys);
                if (itemIds.Count > 0)
                {
                    foreach (var gid in authMatches.Concat(provMatches).ToList())
                    {
                        var g = _groups[gid];
                        if (FindConflict(itemIds, AuthIdsOf(g)) is not { } clash) continue;
                        LogRefusal($"Merge refused: '{item.Name}' ({item.ServerId}/{item.Id}) and group {gid} '{g.Name}' hold different {clash} ids; shared key(s) flagged suspect.");
                        authMatches.Remove(gid);
                        provMatches.Remove(gid);
                        authLinkedGroupIds.Remove(gid);
                        candidateKeys = DropSuspectKeys(candidateKeys, gid, keysToSave);
                    }
                    if (candidateKeys.Count == 0) candidateKeys.Add(IdentityKey(item));
                }

                // A season/episode key is only as good as this server's numbering. One that leads into a group
                // where this server already has a member numbered differently joins two different episodes: an
                // untagged S02E10 into the group of the episode another server numbers S02E10 and this one S02E04.
                // Same-server copies share numbering; a bare file joining a tagged copy on another server is unaffected.
                if (PositionOf(item) is { } position)
                {
                    var server = item.ServerId ?? string.Empty;
                    foreach (var gid in authMatches.Concat(provMatches).Where(g => !idReached.Contains(g)).ToList())
                    {
                        var g = _groups[gid];
                        if (!g.ItemIds.Any(m => m.Key.ServerId == server && m.Key.ItemId != item.Id && m.Value is { } p && p != position)) continue;
                        LogRefusal($"Merge refused: '{item.Name}' ({server}/{item.Id}) S{position.Season:D2}E{position.Episode:D2} reached group {gid} '{g.Name}' only by season/episode, and that server numbers its member differently.");
                        authMatches.Remove(gid);
                        provMatches.Remove(gid);
                        authLinkedGroupIds.Remove(gid);
                    }
                }
                bool isItemAuth = candidateKeys.Any(k => k.IsAuthoritative);

                // ── Case 4: Zero Groups Matched -> CREATE NEW GROUP ──
                if (authMatches.Count == 0 && provMatches.Count == 0)
                {
                    CreateGroup(item, candidateKeys, score, height, bitrate, is4k, audio, itemTmdbKey, keysToSave, itemAssignments, dirtyGroups);
                    continue;
                }

                // ── Case 1: Multiple Authoritative Groups Reached Authoritatively (>= 2) -> MERGE ALL ──
                if (authLinkedGroupIds.Count >= 2)
                {
                    var winner = _groups[authLinkedGroupIds.Min()];
                    candidateKeys = MergeInto(winner, authLinkedGroupIds.Concat(provMatches), item, candidateKeys, deletedGroupIds, remappedGroups, keysToSave);
                    winner.IsProvisional = 0;
                    AttachItemToGroupRecord(winner, item, height, bitrate, is4k, audio, score, itemTmdbKey, candidateKeys, keysToSave, itemAssignments, dirtyGroups);
                    continue;
                }

                // ── Case 2: One Authoritative Group Reached Authoritatively (== 1) -> ATTACH & ABSORB PROVISIONAL ──
                if (authLinkedGroupIds.Count == 1)
                {
                    var target = _groups[authLinkedGroupIds.First()];
                    candidateKeys = MergeInto(target, provMatches, item, candidateKeys, deletedGroupIds, remappedGroups, keysToSave);
                    if (isItemAuth) target.IsProvisional = 0;
                    // Remake protection: other auth groups reached only through fuzzy keys are not merged.
                    AttachItemToGroupRecord(target, item, height, bitrate, is4k, audio, score, itemTmdbKey, candidateKeys, keysToSave, itemAssignments, dirtyGroups);
                    continue;
                }

                // ── Case 3: Only Provisional Groups Matched (or reached fuzzily) ──
                if (provMatches.Count > 0)
                {
                    if (isItemAuth)
                    {
                        // Inbound item is authoritative: Merge all provisional groups into lowest id
                        var winner = _groups[provMatches.Min()];
                        candidateKeys = MergeInto(winner, provMatches, item, candidateKeys, deletedGroupIds, remappedGroups, keysToSave);
                        winner.IsProvisional = 0; // cleared
                        AttachItemToGroupRecord(winner, item, height, bitrate, is4k, audio, score, itemTmdbKey, candidateKeys, keysToSave, itemAssignments, dirtyGroups);
                    }
                    else
                    {
                        // Fuzzy link only: attach to lowest provisional group without merging others
                        var target = _groups[provMatches.Min()];

                        // Group Size Cap Guard (M6 & R4.Minor): cap fuzzy attach at 4 * serverCount
                        if (target.VersionCount >= maxGroupCap)
                        {
                            NocturneLogger.Warn("ConcurrentDedupIndex", $"Fuzzy attach rejected: group {target.GroupId} reached cap of {target.VersionCount} versions. Creating new group for '{item.Name}'.");
                            CreateGroup(item, candidateKeys.Where(k => k.IsAuthoritative).DefaultIfEmpty(IdentityKey(item)).ToList(), score, height, bitrate, is4k, audio, itemTmdbKey, keysToSave, itemAssignments, dirtyGroups);
                        }
                        else
                        {
                            AttachItemToGroupRecord(target, item, height, bitrate, is4k, audio, score, itemTmdbKey, candidateKeys, keysToSave, itemAssignments, dirtyGroups);
                        }
                    }
                    continue;
                }

                // Only authoritative groups matched, all through fuzzy keys. Conflicting groups were already
                // removed above; the size cap applies here as on the provisional path (C5).
                var fuzzyTarget = _groups[authMatches.Min()];
                if (fuzzyTarget.VersionCount >= maxGroupCap)
                {
                    NocturneLogger.Warn("ConcurrentDedupIndex", $"Fuzzy attach rejected: group {fuzzyTarget.GroupId} reached cap of {fuzzyTarget.VersionCount} versions. Creating new group for '{item.Name}'.");
                    CreateGroup(item, candidateKeys.Where(k => k.IsAuthoritative).DefaultIfEmpty(IdentityKey(item)).ToList(), score, height, bitrate, is4k, audio, itemTmdbKey, keysToSave, itemAssignments, dirtyGroups);
                }
                else
                {
                    AttachItemToGroupRecord(fuzzyTarget, item, height, bitrate, is4k, audio, score, itemTmdbKey, candidateKeys, keysToSave, itemAssignments, dirtyGroups);
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

    public (List<DedupGroupRecord> Groups, List<(string Key, int GroupId, bool IsAuthoritative)> Keys, List<(string ServerId, string ItemId, int GroupId)> Items) GetRebuildState()
    {
        lock (_lock)
        {
            var groups = _groups.Values.ToList();
            var keys = _keyToGroup.Select(kv => (kv.Key, kv.Value.GroupId, kv.Value.IsAuthoritative)).ToList();
            var items = new List<(string ServerId, string ItemId, int GroupId)>();
            foreach (var g in groups)
            {
                foreach (var (srv, id) in g.ItemIds.Keys)
                {
                    items.Add((srv, id, g.GroupId));
                }
            }
            return (groups, keys, items);
        }
    }

    public static string? ExtractTmdbKey(EmbyItem item)
    {
        var type = item.Type ?? string.Empty;
        if (string.Equals(type, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            if (IsValidProviderId("Tmdb", item.TmdbId)) return $"tmdb:movie:{item.TmdbId}";
        }
        else if (string.Equals(type, "Series", StringComparison.OrdinalIgnoreCase))
        {
            if (IsValidProviderId("Tmdb", item.TmdbId)) return $"tmdb:tv:{item.TmdbId}";
        }
        else if (string.Equals(type, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            if (IsValidProviderId("Tmdb", item.SeriesTmdbId))
            {
                if (!item.ParentIndexNumber.HasValue || !item.IndexNumber.HasValue) return null;
                return $"tmdb:tv:{item.SeriesTmdbId}:s{item.ParentIndexNumber.Value:D2}e{item.IndexNumber.Value:D2}";
            }
            else if (IsValidProviderId("Tmdb", item.TmdbId))
            {
                return $"tmdb:movie:{item.TmdbId}";
            }
        }
        return null;
    }

    private int GetServerPriorityRank(string? serverId)
    {
        if (string.IsNullOrEmpty(serverId) || _serverPriority == null) return 999;
        var idx = _serverPriority.IndexOf(serverId);
        return idx >= 0 ? idx : 999;
    }

    private bool ShouldElectImage(DedupGroupRecord g, string? serverId, string itemId, string? imageTag)
    {
        if (string.IsNullOrEmpty(imageTag)) return false;
        if (string.IsNullOrEmpty(g.ImageTag) || string.IsNullOrEmpty(g.ImageServerId)) return true;

        int newRank = GetServerPriorityRank(serverId);
        int curRank = GetServerPriorityRank(g.ImageServerId);
        if (newRank < curRank) return true;
        if (newRank > curRank) return false;

        var newKey = $"{serverId}:{itemId}";
        var curKey = $"{g.ImageServerId}:{g.ImageItemId}";
        return string.Compare(newKey, curKey, StringComparison.Ordinal) < 0;
    }

    private bool ShouldElectBackdrop(DedupGroupRecord g, string? serverId, string itemId, string? backdropTag)
    {
        if (string.IsNullOrEmpty(backdropTag)) return false;
        if (string.IsNullOrEmpty(g.BackdropTag) || string.IsNullOrEmpty(g.BackdropServerId)) return true;

        int newRank = GetServerPriorityRank(serverId);
        int curRank = GetServerPriorityRank(g.BackdropServerId);
        if (newRank < curRank) return true;
        if (newRank > curRank) return false;

        var newKey = $"{serverId}:{itemId}";
        var curKey = $"{g.BackdropServerId}:{g.BackdropItemId}";
        return string.Compare(newKey, curKey, StringComparison.Ordinal) < 0;
    }

    private void AbsorbLoserGroup(
        DedupGroupRecord winner,
        DedupGroupRecord loser,
        List<int> deletedGroupIds,
        List<(int FromGid, int ToGid)> remappedGroups,
        List<(string Key, int GroupId, bool IsAuthoritative)> keysToSave)
    {
        deletedGroupIds.Add(loser.GroupId);
        remappedGroups.Add((loser.GroupId, winner.GroupId));
        foreach (var (itm, position) in loser.ItemIds) winner.ItemIds[itm] = position;
        foreach (var k in loser.Keys)
        {
            winner.Keys.Add(k);
            bool isAuthKey = _keyToGroup.TryGetValue(k, out var km) ? km.IsAuthoritative : true;
            _keyToGroup[k] = (winner.GroupId, isAuthKey);
            keysToSave.Add((k, winner.GroupId, isAuthKey));
        }
        winner.MaxHeight = Math.Max(winner.MaxHeight ?? 0, loser.MaxHeight ?? 0);
        winner.MaxBitrate = Math.Max(winner.MaxBitrate ?? 0, loser.MaxBitrate ?? 0);
        winner.Has4k = Math.Max(winner.Has4k, loser.Has4k);
        AppendAudioCodec(winner, loser.AudioFormats);

        if (ShouldElectImage(winner, loser.ImageServerId, loser.ImageItemId ?? string.Empty, loser.ImageTag))
        {
            winner.ImageTag = loser.ImageTag;
            winner.ImageItemId = loser.ImageItemId;
            winner.ImageServerId = loser.ImageServerId;
        }
        if (ShouldElectBackdrop(winner, loser.BackdropServerId, loser.BackdropItemId ?? string.Empty, loser.BackdropTag))
        {
            winner.BackdropTag = loser.BackdropTag;
            winner.BackdropItemId = loser.BackdropItemId;
            winner.BackdropServerId = loser.BackdropServerId;
        }
        if (string.IsNullOrEmpty(winner.TmdbKey) && !string.IsNullOrEmpty(loser.TmdbKey))
        {
            winner.TmdbKey = loser.TmdbKey;
        }
    }

    private void AttachItemToGroupRecord(
        DedupGroupRecord target,
        EmbyItem item,
        int height,
        long bitrate,
        int is4k,
        string? audio,
        int score,
        string? itemTmdbKey,
        List<CandidateKey> candidateKeys,
        List<(string Key, int GroupId, bool IsAuthoritative)> keysToSave,
        List<(string ServerId, string ItemId, int GroupId)> itemAssignments,
        List<DedupGroupRecord> dirtyGroups)
    {
        target.ItemIds[(item.ServerId ?? string.Empty, item.Id)] = PositionOf(item);
        target.VersionCount = target.ItemIds.Count;
        target.MaxHeight = Math.Max(target.MaxHeight ?? 0, height);
        target.MaxBitrate = Math.Max(target.MaxBitrate ?? 0, bitrate);
        target.Has4k = Math.Max(target.Has4k, is4k);
        AppendAudioCodec(target, audio);

        if (ShouldElectImage(target, item.ServerId, item.Id, item.ImageTag))
        {
            target.ImageTag = item.ImageTag;
            target.ImageItemId = item.Id;
            target.ImageServerId = item.ServerId;
        }
        if (ShouldElectBackdrop(target, item.ServerId, item.Id, item.BackdropTag))
        {
            target.BackdropTag = item.BackdropTag;
            target.BackdropItemId = item.Id;
            target.BackdropServerId = item.ServerId;
        }
        if (string.IsNullOrEmpty(target.TmdbKey) && !string.IsNullOrEmpty(itemTmdbKey))
        {
            target.TmdbKey = itemTmdbKey;
        }

        if (score > target.PrimaryScore)
        {
            UpdatePrimary(target, item, score);
        }

        ClaimKeys(target, candidateKeys, keysToSave);
        RecordEvidence(target, item, keysToSave);

        item.DedupGroupId = target.GroupId.ToString();
        itemAssignments.Add((item.ServerId ?? string.Empty, item.Id, target.GroupId));
        target.IsDirty = true;
        if (!dirtyGroups.Contains(target)) dirtyGroups.Add(target);
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

    internal static void UpdatePrimary(DedupGroupRecord g, EmbyItem item, int score)
    {
        g.PrimaryItemId = item.Id;
        g.PrimaryServerId = item.ServerId ?? string.Empty;
        g.PrimaryScore = score;
        g.Name = item.Name;
        g.SortName = item.SortName;
        g.Year = item.ProductionYear ?? g.Year;
        g.Overview = item.Overview ?? g.Overview;
        g.CommunityRating = item.CommunityRating ?? g.CommunityRating;
    }
}
