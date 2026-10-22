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
}
