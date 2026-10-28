using System.IO;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// #38: Nocturne-side corrections for servers nobody here can edit.
public class ItemOverrideTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
    private readonly DatabaseService _db;
    private static readonly List<string> Priority = new() { "valkyrie", "raptor" };

    public ItemOverrideTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new DatabaseService(Path.Combine(_dir, "a.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Now => DateTime.UtcNow.ToString("O");

    private void Series(string server, string tvdb, string? imdb = null)
    {
        var ids = new Dictionary<string, string> { ["Tvdb"] = tvdb };
        if (imdb != null) ids["Imdb"] = imdb;
        _db.UpsertItem(new EmbyItem { Id = $"show_{server}", ServerId = server, Name = "Obsessive Compulsive Cleaners", Type = "Series", ProviderIds = ids }, server);
    }

    private EmbyItem Episode(string id, string server, int season, int episode, string? tvdb) => new()
    {
        Id = id, ServerId = server, Name = id, Type = "Episode", SeriesId = $"show_{server}", SeriesName = "Obsessive Compulsive Cleaners",
        ParentIndexNumber = season, IndexNumber = episode, ProviderIds = tvdb == null ? new() : new() { ["Tvdb"] = tvdb }
    };

    private long? Group(string server, string id) => _db.GroupIdOfItem(server, id);

    private Dictionary<string, string>? Stored(string server, string id, string path)
    {
        var item = _db.GetCachedItem(id, server)!;
        return path == "ProviderIds" ? item.ProviderIds : item.ServerProviderIds;
    }

    // Valkyrie's S02E07 carries E08's TVDB id, so it groups with both E08s. Removing that id locally puts it with
    // Raptor's E07 at the next rebuild; deleting the override puts it back, from the stored row alone.
    private void OccSeason()
    {
        Series("valkyrie", "300000");
        Series("raptor", "300000");
        foreach (var e in new[] { Episode("v7", "valkyrie", 2, 7, "4726371"), Episode("v8", "valkyrie", 2, 8, "4726371"),
                                  Episode("r7", "raptor", 2, 7, "4726370"), Episode("r8", "raptor", 2, 8, "4726371") })
            _db.UpsertItem(e, e.ServerId!);
        new DedupEngine().RebuildIndex(_db, Priority);
    }

    [Fact]
    public void RemovingAShiftedId_RegroupsTheEpisode_AtTheNextRebuild_AndDeletingTheOverrideRestoresIt()
    {
        OccSeason();
        Assert.Equal(Group("valkyrie", "v8"), Group("valkyrie", "v7"));

        var removeTvdb = new ItemOverride("valkyrie", "v7", "Tvdb", null, Now);
        _db.SetItemOverrides(new[] { removeTvdb });

        Assert.Equal(1, _db.OverridesPending());
        Assert.Equal(Group("valkyrie", "v8"), Group("valkyrie", "v7")); // nothing moves before the rebuild
        Assert.False(Stored("valkyrie", "v7", "ProviderIds")!.ContainsKey("Tvdb"));
        Assert.Equal("4726371", Stored("valkyrie", "v7", "ServerProviderIds")!["Tvdb"]);

        new DedupEngine().RebuildIndex(_db, Priority);

        Assert.Equal(0, _db.OverridesPending());
        Assert.Equal(Group("raptor", "r7"), Group("valkyrie", "v7"));
        Assert.NotEqual(Group("valkyrie", "v8"), Group("valkyrie", "v7"));
        Assert.Equal(Group("raptor", "r8"), Group("valkyrie", "v8"));
        Assert.Equal(new DedupEngine.Drift(0, 0, 0), new DedupEngine().MeasureDrift(_db, Priority, 2)); // the oracle reads the same corrected rows

        _db.RemoveItemOverrides(new[] { removeTvdb });

        Assert.Equal(1, _db.OverridesPending());
        Assert.Equal("4726371", Stored("valkyrie", "v7", "ProviderIds")!["Tvdb"]); // restored from the row, nothing fetched
        Assert.Null(Stored("valkyrie", "v7", "ServerProviderIds"));
        new DedupEngine().RebuildIndex(_db, Priority);
        Assert.Equal(Group("valkyrie", "v8"), Group("valkyrie", "v7"));
    }

    // Several corrections wait for one rebuild.
    [Fact]
    public void Corrections_AreCounted_UntilOneRebuildAppliesThemAll()
    {
        OccSeason();
        _db.SetItemOverrides(new[] { new ItemOverride("valkyrie", "v7", "Tvdb", null, Now) });
        _db.SetItemOverrides(new[] { new ItemOverride("raptor", "r8", "Imdb", "tt1234567", Now) });
        Assert.Equal(2, _db.OverridesPending());

        new DedupEngine().RebuildIndex(_db, Priority);

        Assert.Equal(0, _db.OverridesPending());
    }

    // The server keeps sending its wrong id; every sync stores and groups the corrected one.
    [Fact]
    public void AnItemFreshFromTheServer_IsCorrectedBeforeItIsGroupedAndStored()
    {
        OccSeason();
        _db.SetItemOverrides(new[] { new ItemOverride("valkyrie", "v7", "Tvdb", null, Now) });

        var fresh = Episode("v7", "valkyrie", 2, 7, "4726371");
        new ConcurrentDedupIndex().ResolveSeriesProviderIds(new List<EmbyItem> { fresh }, _db);
        Assert.False(fresh.ProviderIds!.ContainsKey("Tvdb"));

        _db.UpsertItems(new[] { Episode("v7", "valkyrie", 2, 7, "4726371") }, "valkyrie");
        Assert.False(Stored("valkyrie", "v7", "ProviderIds")!.ContainsKey("Tvdb"));
        Assert.Equal("4726371", Stored("valkyrie", "v7", "ServerProviderIds")!["Tvdb"]);
    }

    // A truncated series id: Show Conflicting IDs finds the split, "use the long id on Valkyrie" targets Valkyrie's
    // Series row, and its episodes follow it at the next rebuild.
    [Fact]
    public void UsingTheOtherServersId_OnATruncatedSeriesId_MergesItsEpisodes()
    {
        Series("valkyrie", "30000", imdb: "tt3000000");
        Series("raptor", "300000", imdb: "tt3000000");
        _db.UpsertItem(Episode("v1", "valkyrie", 1, 1, null), "valkyrie");
        _db.UpsertItem(Episode("r1", "raptor", 1, 1, null), "raptor");
        new DedupEngine().RebuildIndex(_db, Priority);
        Assert.NotEqual(Group("raptor", "r1"), Group("valkyrie", "v1"));

        var root = Assert.Single(DatabaseService.RollUpConflicts(_db.GetConflictPairs()!));
        Assert.Equal("Series", root.ItemType);
        var (side, ids) = root.ServersA == "valkyrie" ? (root.ServersA, root.IdsA) : (root.ServersB, root.IdsB);
        Assert.Equal("30000", ids);
        var targets = _db.GetConflictTargets(root, new HashSet<string> { side }, new HashSet<string> { ids });
        Assert.Equal(new[] { ("valkyrie", "show_valkyrie") }, targets);
        // "Remove on <server>" passes no ids: both sides' match, and only the server chosen is touched.
        Assert.Equal(new[] { ("valkyrie", "show_valkyrie") }, _db.GetConflictTargets(root, new HashSet<string> { "valkyrie" }, null));

        _db.SetItemOverrides(targets.Select(t => new ItemOverride(t.ServerId, t.ItemId, "Tvdb", "300000", Now)));
        new DedupEngine().RebuildIndex(_db, Priority);

        Assert.Equal(Group("raptor", "r1"), Group("valkyrie", "v1"));
        Assert.Empty(_db.GetConflictPairs()!);
    }

    // "These are the same, cascade anyway": live, no rebuild. A version that joins later holds the group again.
    [Fact]
    public void ReleasingAHeldGroup_LetsItCascade_UntilANewVersionJoins()
    {
        _db.SetMigrationState(ConcurrentDedupIndex.EngineMarkerKey, ConcurrentDedupIndex.EngineVersion);
        void Plant(string id, string server, int episode) =>
            _db.UpsertItem(new EmbyItem { Id = id, ServerId = server, Name = id, Type = "Episode", ParentIndexNumber = 2, IndexNumber = episode,
                ProviderIds = new() { ["Tvdb"] = "91051" }, DedupGroupId = "1" }, server);
        Plant("rx", "raptor", 10);
        Plant("vx", "valkyrie", 4);
        Assert.False(FeatureFlags.SiblingCascade(_db, 1));
        Assert.Contains(_db.GetHeldGroups()!, v => v.GroupId == 1);

        _db.ReleaseCascade(1);

        Assert.True(FeatureFlags.SiblingCascade(_db, 1));
        Assert.DoesNotContain(_db.GetHeldGroups()!, v => v.GroupId == 1);
        Assert.Equal(0, _db.OverridesPending());

        Plant("vz", "valkyrie", 10);
        Assert.False(FeatureFlags.SiblingCascade(_db, 1));
    }

    [Fact]
    public void ClearCache_KeepsTheCorrections()
    {
        _db.SetItemOverrides(new[] { new ItemOverride("valkyrie", "v7", "Tvdb", null, Now) });

        _db.ClearAllCache();

        var kept = Assert.Single(_db.GetItemOverrides());
        Assert.Equal(("valkyrie", "v7", "Tvdb", (string?)null), (kept.ServerId, kept.ItemId, kept.Field, kept.Value));
    }

    [Fact]
    public void Apply_ReplacesEveryAlias_IsIdempotent_AndApplyingNoneRestoresTheServersIds()
    {
        var server = new Dictionary<string, string> { ["TheTVDB"] = "8118", ["Imdb"] = "tt0903747" };
        var set = new[] { new ItemOverride("s", "i", "Tvdb", "81189", Now) };

        var (ids, kept) = ItemOverrides.Apply(server, null, set);
        Assert.Equal(new Dictionary<string, string> { ["Tvdb"] = "81189", ["Imdb"] = "tt0903747" }, ids);
        Assert.Equal(server, kept);

        var again = ItemOverrides.Apply(ids, kept, set);
        Assert.Equal(JsonSerializer.Serialize(ids), JsonSerializer.Serialize(again.Ids));

        var restored = ItemOverrides.Apply(ids, kept, Array.Empty<ItemOverride>());
        Assert.Equal(server, restored.Ids);
        Assert.Null(restored.ServerIds);
    }
}
