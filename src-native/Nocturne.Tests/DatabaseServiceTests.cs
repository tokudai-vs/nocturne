using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class DatabaseServiceTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseService _db;

    public DatabaseServiceTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseService(_tempDbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
        }
        catch { }
    }

    [Fact]
    public void SaveAndGetCachedContinueWatching_PersistsAndRestoresInOrder()
    {
        var items = new List<EmbyItem>
        {
            new() { Id = "ep1", Name = "Episode One", ServerId = "srvA", Type = "Episode" },
            new() { Id = "mov1", Name = "The Movie", ServerId = "srvB", Type = "Movie" }
        };

        _db.SaveContinueWatchingCache(items);

        var restored = _db.GetCachedContinueWatching();
        Assert.Equal(2, restored.Count);
        Assert.Equal("ep1", restored[0].Id);
        Assert.Equal("Episode One", restored[0].Name);
        Assert.Equal("srvA", restored[0].ServerId);
        Assert.Equal("mov1", restored[1].Id);
        Assert.Equal("The Movie", restored[1].Name);
        Assert.Equal("srvB", restored[1].ServerId);
    }

    [Fact]
    public void RemoveFromContinueWatchingCache_DeletesSpecificItem()
    {
        var items = new List<EmbyItem>
        {
            new() { Id = "item1", Name = "Item One", ServerId = "srv1" },
            new() { Id = "item2", Name = "Item Two", ServerId = "srv1" }
        };

        _db.SaveContinueWatchingCache(items);
        _db.RemoveFromContinueWatchingCache("item1");

        var restored = _db.GetCachedContinueWatching();
        Assert.Single(restored);
        Assert.Equal("item2", restored[0].Id);
    }

    [Fact]
    public void UpsertItems_WithCheckpoint_AtomicallyPersistsDataAndWatermark()
    {
        var items = new List<EmbyItem>
        {
            new() { Id = "mov_01", Name = "Movie Alpha", ServerId = "srvX", Type = "Movie" },
            new() { Id = "mov_02", Name = "Movie Beta", ServerId = "srvX", Type = "Movie" }
        };

        // Atomically insert items and commit watermark at 500 / 1000 items
        _db.UpsertItems(
            items,
            serverId: "srvX",
            parentId: "lib_movies",
            checkpointStartIndex: 500,
            checkpointTotalItems: 1000,
            checkpointIsCompleted: false);

        // Verify items were persisted
        var cached = _db.GetCachedItems(serverId: "srvX");
        Assert.Equal(2, cached.Count);

        // Verify checkpoint watermark was atomically saved in the same transaction
        var cp = _db.GetSyncCheckpoint("srvX", "lib_movies");
        Assert.NotNull(cp);
        Assert.Equal(500, cp.StartIndex);
        Assert.Equal(1000, cp.TotalItems);
        Assert.False(cp.IsCompleted);

        // Advance watermark to completion
        _db.UpsertItems(
            new List<EmbyItem> { new() { Id = "mov_03", Name = "Movie Gamma", ServerId = "srvX", Type = "Movie" } },
            serverId: "srvX",
            parentId: "lib_movies",
            checkpointStartIndex: 1000,
            checkpointTotalItems: 1000,
            checkpointIsCompleted: true);

        var cpCompleted = _db.GetSyncCheckpoint("srvX", "lib_movies");
        Assert.NotNull(cpCompleted);
        Assert.Equal(1000, cpCompleted.StartIndex);
        Assert.True(cpCompleted.IsCompleted);

        // Clear checkpoints
        _db.ClearSyncCheckpoints("srvX");
        var cpAfterReset = _db.GetSyncCheckpoint("srvX", "lib_movies");
        Assert.Null(cpAfterReset);
    }

    [Fact]
    public void SaveAndGetMediaStreamPreference_PersistsAndRestoresTracks()
    {
        _db.SaveMediaStreamPreference("series:show_123", audioTrackId: 2, audioLang: "jpn", subtitleTrackId: 1, subtitleLang: "eng");

        var pref = _db.GetMediaStreamPreference("series:show_123");
        Assert.NotNull(pref);
        Assert.Equal(2, pref.Value.AudioTrackId);
        Assert.Equal("jpn", pref.Value.AudioLanguage);
        Assert.Equal(1, pref.Value.SubtitleTrackId);
        Assert.Equal("eng", pref.Value.SubtitleLanguage);

        // Update with subtitles off
        _db.SaveMediaStreamPreference("series:show_123", audioTrackId: null, audioLang: null, subtitleTrackId: 0, subtitleLang: "off");
        var updated = _db.GetMediaStreamPreference("series:show_123");
        Assert.NotNull(updated);
        Assert.Equal(2, updated.Value.AudioTrackId); // Preserved
        Assert.Equal(0, updated.Value.SubtitleTrackId); // Updated to off
        Assert.Equal("off", updated.Value.SubtitleLanguage);
    }

    [Fact]
    public void UpdatePlaybackProgress_UpdatesContinueWatchingAndCachedItems()
    {
        var item = new EmbyItem
        {
            Id = "ep_456",
            Name = "Episode 456",
            ServerId = "srv_1",
            UserData = new UserItemData { PlaybackPositionTicks = 0, Played = false }
        };

        _db.SaveContinueWatchingCache(new List<EmbyItem> { item });

        // Update progress to 15 seconds (150,000,000 ticks)
        _db.UpdatePlaybackProgress("ep_456", "srv_1", 150_000_000);

        var restored = _db.GetCachedContinueWatching();
        Assert.Single(restored);
        Assert.NotNull(restored[0].UserData);
        Assert.Equal(150_000_000, restored[0].UserData!.PlaybackPositionTicks);
        Assert.False(restored[0].UserData!.Played);
    }

    [Fact]
    public void TechnicalBadge_FormatsAccuratelyWithoutServerInfo()
    {
        var item1 = new EmbyItem
        {
            Id = "mov1",
            Name = "Dune Part Two 2024 IMAX 2160p UHD BluRay DoVi TrueHD Atmos",
            Height = 2160,
            Width = 3840,
            AudioCodec = "TrueHD Atmos",
            Bitrate = 52_000_000,
            RunTimeTicks = 9_900_000_000L
        };

        Assert.Equal("4K IMAX Enhanced • Dolby Vision • Atmos", item1.TechnicalBadge);
        Assert.Equal("52 Mbps", item1.BitrateBadge);

        var item2 = new EmbyItem
        {
            Id = "mov2",
            Name = "Inception 2010 1080p BluRay DTS",
            Height = 1080,
            Width = 1920,
            AudioCodec = "DTS-HD",
            Bitrate = 14_500_000,
            RunTimeTicks = 8_800_000_000L
        };

        Assert.Equal("1080p BluRay • DTS", item2.TechnicalBadge);
        Assert.Equal("14.5 Mbps", item2.BitrateBadge);
    }

    // #19 item 6: a sync start no longer loads every group; a batch reads the groups its keys reach.
    [Fact]
    public void SyncStart_LoadsNoGroups_AndABatchStillFindsTheExistingGroup()
    {
        var a = new EmbyItem { Id = "a", ServerId = "s1", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } } };
        var b = new EmbyItem { Id = "b", ServerId = "s2", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } } };
        _db.UpsertItem(a, "s1");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { a }, _db);

        var index = new ConcurrentDedupIndex();
        index.InitializeFromDb(_db);
        Assert.Equal(0, index.TotalGroupsCount);

        _db.UpsertItem(b, "s2");
        index.ProcessBatch(new List<EmbyItem> { b }, _db);

        Assert.Equal(a.DedupGroupId, b.DedupGroupId);
        Assert.Equal(1, _db.CountMultiVersionGroups());
    }

    // The primary's score was never stored, so the first copy to attach in a later sync (now: a later batch)
    // replaced a better primary whatever it scored.
    [Fact]
    public void PrimaryElection_KeepsTheBetterPrimary_AcrossBatches()
    {
        var a = new EmbyItem { Id = "a", ServerId = "s1", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } },
                               BackdropImageTags = new() { "bd" }, ImageTags = new() { { "Primary", "p" } }, Overview = "Linguist." };
        var b = new EmbyItem { Id = "b", ServerId = "s2", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } },
                               ImageTags = new() { { "Primary", "p" } } };
        _db.UpsertItem(a, "s1");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { a }, _db);
        _db.UpsertItem(b, "s2");
        var nextSync = new ConcurrentDedupIndex();
        nextSync.InitializeFromDb(_db);
        nextSync.ProcessBatch(new List<EmbyItem> { b }, _db);

        Assert.Equal(a.DedupGroupId, b.DedupGroupId);
        Assert.Equal("a", _db.GetDedupGroup(long.Parse(a.DedupGroupId!))!.PrimaryItemId);
    }

    [Fact]
    public void CachedItemsChunk_PagesByRowIdWithoutGapsOrRepeats()
    {
        for (int i = 1; i <= 5; i++)
            _db.UpsertItem(new EmbyItem { Id = $"m{i}", Name = $"Movie {i}", Type = "Movie" }, "s1");
        _db.UpsertItem(new EmbyItem { Id = "box", Name = "Box", Type = "BoxSet" }, "s1");

        var seen = new List<string>();
        long after = 0;
        while (true)
        {
            var (items, last) = _db.GetCachedItemsChunk(after, 2);
            if (last == after) break;
            seen.AddRange(items.Select(i => i.Id));
            after = last;
        }

        Assert.Equal(new[] { "m1", "m2", "m3", "m4", "m5" }, seen);
    }

    // The scrobble payload read one group's keys by loading every group, key and item in the database (57 s and
    // over a gigabyte on the live library) on each start, pause and stop.
    [Fact]
    public void GetAuthoritativeKeys_ReturnsOnlyThatGroupsAuthoritativeKeys()
    {
        var a = new EmbyItem { Id = "a", ServerId = "s1", Name = "Arrival", Type = "Movie", ProductionYear = 2016, ProviderIds = new() { { "Tmdb", "329865" }, { "Imdb", "tt2543164" } } };
        var b = new EmbyItem { Id = "b", ServerId = "s1", Name = "Dune", Type = "Movie", ProviderIds = new() { { "Tmdb", "438631" } } };
        foreach (var it in new[] { a, b }) _db.UpsertItem(it, "s1");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { a, b }, _db);

        var keys = _db.GetAuthoritativeKeys(int.Parse(a.DedupGroupId!)).OrderBy(k => k).ToList();

        Assert.Equal(new[] { "imdb:movie:tt2543164", "tmdb:movie:329865" }, keys); // not the fuzzy name key, not Dune's
    }

    // #20 item 4: the swap runs on its own connection. While it holds SQLite's write lock, UI reads are served from
    // the pre-swap snapshot and UI writes are queued (not failed with SQLITE_BUSY), then applied once it commits.
    [Fact]
    public async Task DuringTheRebuildSwap_ReadsAreServed_AndWritesQueueUntilItCommits()
    {
        EmbyItem M(string id, string srv, string tmdb) => new() { Id = id, ServerId = srv, Name = "Movie " + tmdb, Type = "Movie", ProviderIds = new() { { "Tmdb", tmdb } } };
        var a = M("a", "s1", "100"); var b = M("b", "s2", "100"); var c = M("c", "s1", "200");
        foreach (var it in new[] { a, b, c }) _db.UpsertItem(it, it.ServerId!);
        var index = new ConcurrentDedupIndex();
        index.ProcessBatch(new List<EmbyItem> { c }, _db); // c -> group 1, a/b -> group 2; the rebuild flips them
        index.ProcessBatch(new List<EmbyItem> { a, b }, _db);
        var oldAb = long.Parse(a.DedupGroupId!);
        foreach (var it in new[] { a, b }) _db.UpdateLocalPlaybackProgress(it.ServerId!, it.Id, 600 * 10_000_000L);

        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _db.SwapHeldForTest = _ => { reached.Set(); release.Wait(TimeSpan.FromSeconds(30)); };
        var rebuild = Task.Run(() => new DedupEngine().RebuildIndex(_db));
        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));

        var read = Task.Run(() => _db.GetGroupVersions(oldAb.ToString()).Select(v => v.Id).OrderBy(x => x).ToList());
        Assert.True(read.Wait(TimeSpan.FromSeconds(5)), "a UI read waited for the swap");
        Assert.Equal(new[] { "a", "b" }, read.Result); // the pre-swap grouping

        var writes = Task.Run(() =>
        {
            _db.MarkLocalItemPlayed("s1", "c");
            _db.MarkLocalGroupPlayed(oldAb);
            _db.EnqueueTraktEvent(oldAb, "stop", 95, "{}");
        });
        Assert.True(writes.Wait(TimeSpan.FromSeconds(5)), "a UI write waited for the swap");
        Assert.NotEqual(true, _db.GetUserData("s1", "c")?.Played); // queued, not applied yet

        release.Set();
        await rebuild;

        Assert.True(_db.GetUserData("s1", "c")!.Played);
        Assert.True(_db.GetUserData("s1", "a")!.Played); // the group's members as they were when the write was made
        Assert.True(_db.GetUserData("s2", "b")!.Played);
        Assert.Equal(_db.GetGroupIdForDedupKey("tmdb:movie:100"), (int)Assert.Single(_db.GetPendingTraktEvents()).GroupId);
    }

    // #22 item 5: a rebuild renumbered every group from 1, so removing one early item shifted every later id and the swap
    // rewrote every row. Groups now keep their ids; evidence keys ("{key}@{group}") follow them.
    [Fact]
    public void ARepeatRebuild_KeepsGroupIds_AfterAnEarlyItemIsGone()
    {
        EmbyItem M(string id, string srv, string tmdb, string? imdb = null) => new()
        {
            Id = id, ServerId = srv, Name = "Movie " + tmdb, Type = "Movie",
            ProviderIds = imdb == null ? new() { ["Tmdb"] = tmdb } : new() { ["Tmdb"] = tmdb, ["Imdb"] = imdb },
        };
        var items = new[] { M("first", "s1", "1"), M("b", "s1", "2"), M("b2", "s2", "2"), M("c", "s1", "3"),
                            M("hack-r", "s1", "566222", "tt9358204"), M("hack-v", "s2", "566222", "tt4736550") };
        foreach (var it in items) _db.UpsertItem(it, it.ServerId!);
        var engine = new DedupEngine();
        engine.RebuildIndex(_db);
        long GroupOf(string id, string srv) => long.Parse(_db.GetCachedItem(id, srv) is { } _ ? GroupIdText(id, srv) : "0");
        var before = items.Skip(1).ToDictionary(i => i.Id, i => GroupOf(i.Id, i.ServerId!));

        _db.RemoveDeletedItems("s1", new[] { "first" });
        engine.RebuildIndex(_db);

        Assert.All(items.Skip(1), i => Assert.Equal(before[i.Id], GroupOf(i.Id, i.ServerId!)));
        Assert.Single(_db.GetConflictPairs()!); // the split pair is still found through its evidence key
    }

    private string GroupIdText(string id, string srv)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_id FROM cached_items WHERE id = $id AND server_id = $srv";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$srv", srv);
        return cmd.ExecuteScalar()!.ToString()!;
    }

    // #22 item 5: the WAL kept its high-water mark: no checkpoint truncated it, and the pooled connections were never
    // really closed, so SQLite never deleted it (217 MB left beside the live copy after a sync).
    [Fact]
    public void TheWal_IsTruncatedAtIdle_AndGoneWhenTheDatabaseCloses()
    {
        var wal = _tempDbPath + "-wal";
        for (int i = 0; i < 300; i++)
            _db.UpsertItem(new EmbyItem { Id = $"m{i}", ServerId = "s1", Name = new string('x', 2000), Type = "Movie" }, "s1");
        Assert.True(new FileInfo(wal).Length > 0);

        _db.CheckpointWal("the test");
        Assert.Equal(0, new FileInfo(wal).Length);

        _db.UpsertItem(new EmbyItem { Id = "late", ServerId = "s1", Name = "Late", Type = "Movie" }, "s1");
        _db.Dispose();
        Assert.False(File.Exists(wal));
    }

    // #21 item 4: queued writes live in memory, so closing the database mid-swap (exit while a rebuild runs) interrupts
    // the swap, which rolls back, and applies them before the connection closes. Playback progress queues too: it
    // wrote straight into the swap's write lock and failed with SQLITE_BUSY.
    [Fact]
    public async Task Dispose_DuringTheRebuildSwap_InterruptsIt_AndAppliesTheQueuedWrites()
    {
        var m = new EmbyItem { Id = "a", ServerId = "s1", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } } };
        _db.UpsertItem(m, "s1");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { m }, _db);

        using var reached = new ManualResetEventSlim();
        _db.SwapHeldForTest = swap =>
        {
            reached.Set();
            using var forever = swap.CreateCommand(); // stands in for a swap that takes minutes
            forever.CommandText = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT COUNT(*) FROM n;";
            forever.ExecuteScalar();
        };
        var rebuild = Task.Run(() => new DedupEngine().RebuildIndex(_db));
        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));
        var write = Task.Run(() => _db.UpdateLocalPlaybackProgress("s1", "a", 600 * 10_000_000L));
        Assert.True(write.Wait(TimeSpan.FromSeconds(5)), "a progress write waited for the swap");

        var dispose = Task.Run(_db.Dispose);
        Assert.True(dispose.Wait(TimeSpan.FromSeconds(20)), "closing waited for the swap");
        await Assert.ThrowsAnyAsync<Microsoft.Data.Sqlite.SqliteException>(() => rebuild);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var reopened = new DatabaseService(_tempDbPath);
        Assert.Equal(600 * 10_000_000L, reopened.GetUserData("s1", "a")!.PlaybackPositionTicks);
        Assert.NotNull(reopened.GetCachedItem("a", "s1")); // the rolled-back swap left the old state
    }

    // A wrongly grouped library: c/d in one group, a/b in another, then a moved into c/d's. The rebuild regroups them,
    // and since #22 item 5 keeps ids where it can, a/b keep the lower of their previous ids and c/d need a new one.
    private (long OldAb, long OldCd) SplitWrongly()
    {
        EmbyItem M(string id, string srv, string tmdb) => new() { Id = id, ServerId = srv, Name = "Movie " + tmdb, Type = "Movie", ProviderIds = new() { { "Tmdb", tmdb } } };
        var a = M("a", "s1", "100"); var b = M("b", "s2", "100");
        var c = M("c", "s1", "200"); var d = M("d", "s2", "200");
        foreach (var it in new[] { a, b, c, d }) _db.UpsertItem(it, it.ServerId!);
        var index = new ConcurrentDedupIndex();
        index.ProcessBatch(new List<EmbyItem> { c, d }, _db);
        index.ProcessBatch(new List<EmbyItem> { a, b }, _db);
        var (oldAb, oldCd) = (long.Parse(a.DedupGroupId!), long.Parse(c.DedupGroupId!));
        _db.ExecuteSql($"UPDATE cached_items SET group_id = {oldCd}, dedup_group_id = '{oldCd}' WHERE id = 'a';");
        return (oldAb, oldCd);
    }

    // Item 3 (#19, audit 4.3): a group can change id on a rebuild, so a queued Trakt event kept naming its old id,
    // which then belongs to an unrelated group. It follows its old group's primary item.
    [Fact]
    public void RebuildIndex_RemapsQueuedTraktEvents_ToTheirGroupsNewId()
    {
        var (oldAb, oldCd) = SplitWrongly();
        _db.EnqueueTraktEvent(oldAb, "stop", 95, "{}"); // the a/b group's primary is a
        _db.EnqueueTraktEvent(999, "stop", 95, "{}");   // a group that no longer exists

        new DedupEngine().RebuildIndex(_db);

        long ab = _db.GetGroupIdForDedupKey("tmdb:movie:100")!.Value;
        long cd = _db.GetGroupIdForDedupKey("tmdb:movie:200")!.Value;
        Assert.Equal(oldCd, ab);        // a/b keep the lower previous id, which was c/d's
        Assert.NotEqual(oldCd, cd);     // so c/d moved to a new one
        Assert.Equal(new long[] { ab, 0 }, _db.GetPendingTraktEvents().Select(e => (long)e.GroupId));
    }

    [Fact]
    public void RebuildIndex_RemapsLegacyGroupColumn_SoSiblingLookupsStayInTheGroup()
    {
        var (_, oldCd) = SplitWrongly();

        new DedupEngine().RebuildIndex(_db);

        // c/d's text column must follow them to their new id, or they join a/b's lookups under oldCd.
        Assert.Equal(new[] { "a", "b" }, _db.GetGroupVersions(oldCd.ToString()).Select(v => v.Id).OrderBy(x => x));
        var cd = _db.GetGroupIdForDedupKey("tmdb:movie:200")!.Value;
        Assert.Equal(new[] { "c", "d" }, _db.GetGroupVersions(cd.ToString()).Select(v => v.Id).OrderBy(x => x));
    }

    [Fact]
    public void EngineMarker_IsSetByARebuildOrAnEmptyLoad_NotByLoadingExistingGroups()
    {
        string? Marker(DatabaseService db) => db.GetMigrationState(ConcurrentDedupIndex.EngineMarkerKey);

        var a = new EmbyItem { Id = "a", ServerId = "s1", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } } };
        _db.UpsertItem(a, "s1");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { a }, _db); // stands in for groups left by the earlier engine
        new ConcurrentDedupIndex().InitializeFromDb(_db);
        Assert.Null(Marker(_db));

        new DedupEngine().RebuildIndex(_db);
        Assert.Equal(ConcurrentDedupIndex.EngineVersion, Marker(_db));

        var freshPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        try
        {
            using var fresh = new DatabaseService(freshPath);
            new ConcurrentDedupIndex().InitializeFromDb(fresh);
            Assert.Equal(ConcurrentDedupIndex.EngineVersion, Marker(fresh));
        }
        finally
        {
            try { File.Delete(freshPath); } catch { }
        }
    }

    [Fact]
    public void SiblingWriteQueue_EnqueueDrainAndRemove_WorksReliably()
    {
        _db.EnqueueSiblingPlaybackWrite("srv_b", "item_999", 500_000_000, false);
        _db.EnqueueSiblingPlaybackWrite("srv_c", "item_888", 0, true);

        var pending = _db.GetPendingSiblingWrites(limit: 10);
        Assert.Equal(2, pending.Count);

        var itemB = pending.FirstOrDefault(p => p.ServerId == "srv_b");
        Assert.Equal(500_000_000, itemB.PositionTicks);
        Assert.False(itemB.MarkPlayed);

        var itemC = pending.FirstOrDefault(p => p.ServerId == "srv_c");
        Assert.True(itemC.MarkPlayed);

        // Remove itemB
        _db.RemoveSiblingWrite(itemB.QueueId);
        var remaining = _db.GetPendingSiblingWrites(limit: 10);
        Assert.Single(remaining);
        Assert.Equal("srv_c", remaining[0].ServerId);
    }

    [Fact]
    public void GetContinueWatchingItems_RollsUpMultiQualityAndMultiServer_UsingSingleAggregate()
    {
        // Insert two versions of the same movie on different servers
        var itemA = new EmbyItem
        {
            Id = "dune_4k",
            ServerId = "server_alpha",
            Name = "Dune: Part Two",
            Type = "Movie",
            Height = 2160,
            Width = 3840,
            ProviderIds = new Dictionary<string, string> { { "Tmdb", "693134" } },
            ProductionYear = 2024,
            RunTimeTicks = 9_900_000_000L,
            UserData = new UserItemData { PlaybackPositionTicks = 3_000_000_000L, Played = false }
        };

        var itemB = new EmbyItem
        {
            Id = "dune_1080p",
            ServerId = "server_beta",
            Name = "Dune: Part Two",
            Type = "Movie",
            Height = 1080,
            Width = 1920,
            ProviderIds = new Dictionary<string, string> { { "Tmdb", "693134" } },
            ProductionYear = 2024,
            RunTimeTicks = 9_900_000_000L,
            UserData = new UserItemData { PlaybackPositionTicks = 2_000_000_000L, Played = false }
        };

        _db.UpsertItem(itemA, "server_alpha");
        _db.UpsertItem(itemB, "server_beta");

        // Run index to group them into a single dedup group
        var index = new ConcurrentDedupIndex();
        index.SetConnectedServerCount(2);
        index.SetServerPriority(new List<string> { "server_alpha", "server_beta" });
        index.ProcessBatch(new List<EmbyItem> { itemA, itemB }, _db);

        // Update playback progress on item A to position 45s (more recent)
        _db.UpdateLocalPlaybackProgress("server_alpha", "dune_4k", 450_000_000L);

        var cwItems = _db.GetContinueWatchingItems();
        Assert.Single(cwItems);
        Assert.Equal(450_000_000L, cwItems[0].UserData?.PlaybackPositionTicks);
        Assert.Equal("server_alpha", cwItems[0].ServerId);

        // Mark group played using the resolved group ID
        int gid = int.Parse(cwItems[0].DedupGroupId!);
        _db.MarkLocalGroupPlayed(gid);
        var cwAfterPlayed = _db.GetContinueWatchingItems();
        Assert.Empty(cwAfterPlayed);
    }
}
