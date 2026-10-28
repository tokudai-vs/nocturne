using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// #21 item 5: the plan's drift counter, items grouped otherwise than a rebuild of the same items would group them.
public class DedupDriftTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");

    public DedupDriftTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static EmbyItem M(string id, string srv, string tmdb) => new() { Id = id, ServerId = srv, Name = "Movie " + tmdb, Type = "Movie", ProviderIds = new() { ["Tmdb"] = tmdb } };

    [Fact]
    public void Drift_IsZeroAfterARebuild_AndCountsAnItemPlacedOtherwise()
    {
        using var db = new DatabaseService(Path.Combine(_dir, "a.db"));
        var items = new[] { M("a", "s1", "1"), M("b", "s2", "1"), M("c", "s1", "2") }
            .Concat(Enumerable.Range(0, 9).Select(i => M($"x{i}", "s1", "3"))); // one group of 9: over the cap of 8
        foreach (var it in items) db.UpsertItem(it, it.ServerId!);
        var engine = new DedupEngine();
        engine.RebuildIndex(db);

        Assert.Equal(new DedupEngine.Drift(0, 0, 1), engine.MeasureDrift(db, null, 2));
        Assert.Null(FeatureFlags.DedupAlert(db));

        // As an outage or an interleaved write could leave it: b in c's group. Both groups it touches differ: {a}, {b, c}.
        db.ExecuteSql("UPDATE cached_items SET group_id = (SELECT group_id FROM cached_items WHERE id = 'c') WHERE id = 'b';");

        Assert.Equal(new DedupEngine.Drift(3, 2, 1), engine.MeasureDrift(db, null, 2));
        Assert.Contains("3 item(s) are grouped otherwise", FeatureFlags.DedupAlert(db));
        Assert.StartsWith("Movie 3 (9)", db.GetMigrationState(DedupEngine.OverCapNamesKey));

        engine.RebuildIndex(db); // the fix the alert names; it clears the count
        Assert.Null(FeatureFlags.DedupAlert(db));
    }

    // #32: a delta page carries what changed, so a new episode arrives without its series. An episode with no ids of its
    // own matches only through its series' ids, which the incremental path takes from the cached Series row
    // (ConcurrentDedupIndex.ResolveSeriesProviderIds), so a delta groups exactly as a rebuild does. The servers name
    // the show differently, so only the series' TVDB id can link the new episode's two copies.
    [Fact]
    public void ADeltaOfEpisodesWithoutTheirSeries_GroupsAsARebuildWould()
    {
        using var db = new DatabaseService(Path.Combine(_dir, "a.db"));
        var names = new Dictionary<string, string> { ["s1"] = "Dark", ["s2"] = "Dunkel" }; // two titles name keys cannot link
        EmbyItem Show(string srv) => new() { Id = $"show-{srv}", ServerId = srv, Name = names[srv], Type = "Series", ProviderIds = new() { ["Tvdb"] = "334824" } };
        EmbyItem Ep(string srv, int n) => new()
        {
            Id = $"e{n}-{srv}", ServerId = srv, Name = $"Episode {n}", Type = "Episode",
            SeriesId = $"show-{srv}", SeriesName = names[srv], ParentIndexNumber = 1, IndexNumber = n,
        };
        // One sync as SyncEngine runs it: one index, each batch grouped then written, held episodes placed at the end.
        void Sync(params (string Server, List<EmbyItem> Items)[] batches)
        {
            var index = new ConcurrentDedupIndex();
            index.SetConnectedServerCount(2);
            index.InitializeFromDb(db);
            foreach (var (srv, items) in batches)
            {
                index.ProcessBatch(items, db, ConcurrentDedupIndex.UntaggedEpisodes.Hold);
                db.UpsertItems(items, srv, "lib");
            }
            var toPlace = db.GetItemsToPlace();
            if (toPlace.Count > 0) index.ProcessBatch(toPlace, db);
            db.DeleteEmptyHeldGroups();
            index.FlushAll(db);
        }
        List<EmbyItem> Full(string srv) => new[] { Show(srv) }.Concat(Enumerable.Range(1, 3).Select(n => Ep(srv, n))).ToList();

        Sync(("s1", Full("s1")), ("s2", Full("s2")));
        Sync(("s1", new List<EmbyItem> { Ep("s1", 4) }), ("s2", new List<EmbyItem> { Ep("s2", 4) })); // the delta: episode 4 aired, no series

        foreach (var n in Enumerable.Range(1, 4))
            Assert.Equal(db.GetCachedItem($"e{n}-s1", "s1")!.DedupGroupId, db.GetCachedItem($"e{n}-s2", "s2")!.DedupGroupId);
        Assert.Equal(new DedupEngine.Drift(0, 0, 0), new DedupEngine().MeasureDrift(db, null, 2));
    }
}
