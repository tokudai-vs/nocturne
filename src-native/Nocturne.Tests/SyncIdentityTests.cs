using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// #20 item 2: the plan's Phase 3 gate. A sync and a rebuild of the same items must produce the identical partition.
public class SyncIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");

    public SyncIdentityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static EmbyItem Series(string id, string tvdb) =>
        new() { Id = id, Name = "Bag Show", Type = "Series", ProviderIds = new() { ["Tvdb"] = tvdb } };

    private static EmbyItem Episode(string id, string seriesId) =>
        new() { Id = id, Name = "... And a Bag of Chips", Type = "Episode", SeriesId = seriesId, SeriesName = "Bag Show",
                ParentIndexNumber = 6, IndexNumber = 8, ProviderIds = new() { ["Tvdb"] = "100" } };

    [Fact]
    public async Task Sync_GroupsLikeARebuild_WhenEpisodesArriveBeforeTheirSeries()
    {
        var fake = new FakeEmbyServer();
        // The servers disagree on the series' Tvdb id, so with series ids known the episodes conflict. Season rows
        // (not deduplicated) push each Series row into a later page than its episode, as in the live library.
        List<EmbyItem> Library(string episode, string series, string tvdb) =>
            new[] { Episode(episode, series) }
                .Concat(Enumerable.Range(0, AdaptiveBatchController.DefaultBatchSize + 1).Select(i => new EmbyItem { Id = $"{series}-season{i}", Name = $"Season {i}", Type = "Season" }))
                .Append(Series(series, tvdb)).ToList();
        fake.Libraries["raptor.test"] = Library("er", "sr", "111");
        fake.Libraries["valkyrie.test"] = Library("ev", "sv", "222");

        var dbPath = Path.Combine(_dir, "a.db");
        using var db = new DatabaseService(dbPath);
        var settings = new SettingsService(Path.Combine(_dir, SettingsService.SettingsFileName));
        settings.Update(s =>
        {
            s.Servers.Add(new ServerConfig { Id = "raptor", Name = "Raptor", Url = "http://raptor.test", AccessToken = "t", UserId = "u" });
            s.Servers.Add(new ServerConfig { Id = "valkyrie", Name = "Valkyrie", Url = "http://valkyrie.test", AccessToken = "t", UserId = "u" });
        });
        EmbyClient ClientFor(ServerConfig s)
        {
            var c = new EmbyClient(fake);
            c.Configure(s.Url, s.AccessToken, s.UserId);
            return c;
        }
        var sync = new SyncEngine(new EmbyClient(), db, new DedupEngine(), new TraktService(new TraktAuthService(db)), settings, ClientFor);

        await sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var afterSync = PartitionOracle.Read(dbPath);
        new DedupEngine().RebuildIndex(db);
        var afterRebuild = PartitionOracle.Read(dbPath);

        Assert.Equal(4, afterSync.Sum(g => g.Members.Count));
        var divergence = PartitionOracle.Compare(afterSync, afterRebuild);
        Assert.True(divergence.Identical, $"sync-only groups: {string.Join("; ", divergence.OnlyInA.Select(g => string.Join(",", g.Members.Select(m => m.ItemId))))}");
        Assert.DoesNotContain(afterSync, g => g.Members.Any(m => m.ItemId == "er") && g.Members.Any(m => m.ItemId == "ev"));
    }

    // #20 item 3: an untagged episode is written into a held group of its own, so it shows during the sync, and is
    // placed at the end (90 Day Diaries: Valkyrie's untagged S03E08 pages before its tagged copy, numbered S03E06).
    [Fact]
    public async Task UntaggedEpisode_IsShownDuringTheSync_AndPlacedAtTheEnd()
    {
        EmbyItem Ep(string id, string series, int s, int e, string? tvdb) =>
            new() { Id = id, Name = id, Type = "Episode", SeriesId = series, SeriesName = "90 Day Diaries", ParentIndexNumber = s, IndexNumber = e,
                    ProviderIds = tvdb == null ? new() : new() { ["Tvdb"] = tvdb } };
        var fillers = Enumerable.Range(0, AdaptiveBatchController.DefaultBatchSize + 1).Select(i => new EmbyItem { Id = $"season{i}", Name = $"Season {i}", Type = "Season" });
        var fake = new FakeEmbyServer();
        fake.Libraries["raptor.test"] = new() { Ep("r", "rs", 3, 8, "9057492"), Series("rs", "393180") };
        fake.Libraries["valkyrie.test"] = new[] { Ep("vu", "vs", 3, 8, null) }.Concat(fillers).Concat(new[] { Ep("vt", "vs", 3, 6, "9057492"), Series("vs", "393180") }).ToList();

        var dbPath = Path.Combine(_dir, "a.db");
        using var db = new DatabaseService(dbPath);
        var settings = new SettingsService(Path.Combine(_dir, SettingsService.SettingsFileName));
        settings.Update(s =>
        {
            s.Servers.Add(new ServerConfig { Id = "raptor", Name = "Raptor", Url = "http://raptor.test", AccessToken = "t", UserId = "u" });
            s.Servers.Add(new ServerConfig { Id = "valkyrie", Name = "Valkyrie", Url = "http://valkyrie.test", AccessToken = "t", UserId = "u" });
        });
        EmbyClient ClientFor(ServerConfig s) { var c = new EmbyClient(fake); c.Configure(s.Url, s.AccessToken, s.UserId); return c; }
        var sync = new SyncEngine(new EmbyClient(), db, new DedupEngine(), new TraktService(new TraktAuthService(db)), settings, ClientFor);

        object? GroupOf(string id)
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT group_id FROM cached_items WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar();
        }
        var seenWhileSyncing = new List<object?>();
        sync.ProgressReported += r =>
        {
            // null: not written yet (the series pre-pass comes first); DBNull would be a written row without a group.
            if (r.State == SyncState.Writing && GroupOf("vu") is { } g) lock (seenWhileSyncing) seenWhileSyncing.Add(g);
        };

        await sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.NotEmpty(seenWhileSyncing);
        Assert.All(seenWhileSyncing, g => Assert.True(g is long, "the untagged episode had no group during the sync"));
        var afterSync = PartitionOracle.Read(dbPath);
        Assert.DoesNotContain(afterSync, g => g.Members.Any(m => m.ItemId == "vu") && g.Members.Count > 1);
        Assert.Contains(afterSync, g => g.Members.Select(m => m.ItemId).OrderBy(x => x).SequenceEqual(new[] { "r", "vt" }));
        Assert.Empty(db.GetItemsToPlace()); // no held group left
        new DedupEngine().RebuildIndex(db);
        Assert.True(PartitionOracle.Compare(afterSync, PartitionOracle.Read(dbPath)).Identical);
    }
}
