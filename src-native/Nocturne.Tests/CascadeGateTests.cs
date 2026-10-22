using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// Item 2 (#19): the sibling cascade also needs the last measurement of episode groups that span more than one
// (season, episode) without sharing an episode id to be 0. Every sync and rebuild measures it.
public class CascadeGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
    private readonly DatabaseService _db;

    public CascadeGateTests()
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

    private void Plant(string id, string server, int season, int episode, string group, string? tvdb)
    {
        var ids = tvdb == null ? new Dictionary<string, string>() : new() { ["Tvdb"] = tvdb };
        _db.UpsertItem(new EmbyItem
        {
            Id = id, ServerId = server, Name = id, Type = "Episode", SeriesName = "The Yogi Bear Show",
            ParentIndexNumber = season, IndexNumber = episode, ProviderIds = ids, DedupGroupId = group
        }, server);
    }

    [Fact]
    public void WrongSeasonEpisodeMerge_KeepsTheCascadeOff_SharedIdRenumberingDoesNot()
    {
        _db.SetMigrationState(ConcurrentDedupIndex.EngineMarkerKey, ConcurrentDedupIndex.EngineVersion);
        Plant("rx", "raptor", 2, 10, "1", "91051"); // one episode, numbered differently per server
        Plant("vx", "valkyrie", 2, 4, "1", "91051");
        FeatureFlags.MeasureCascadeGate(_db);
        Assert.True(FeatureFlags.SiblingCascade(_db));

        Plant("vz", "valkyrie", 2, 10, "1", null); // an untagged different episode, joined by number
        FeatureFlags.MeasureCascadeGate(_db);

        Assert.Equal("1", _db.GetMigrationState(FeatureFlags.UnsharedRenumberedGroupsKey));
        Assert.False(FeatureFlags.SiblingCascade(_db));
    }

    [Fact]
    public void Unmeasured_KeepsTheCascadeOff()
    {
        _db.SetMigrationState(ConcurrentDedupIndex.EngineMarkerKey, ConcurrentDedupIndex.EngineVersion);
        Assert.False(FeatureFlags.SiblingCascade(_db));
    }

    [Fact]
    public void Rebuild_MeasuresTheGate()
    {
        var a = new EmbyItem { Id = "a", ServerId = "s1", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } } };
        var b = new EmbyItem { Id = "b", ServerId = "s2", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } } };
        _db.UpsertItem(a, "s1");
        _db.UpsertItem(b, "s2");

        new DedupEngine().RebuildIndex(_db);

        Assert.Equal("0", _db.GetMigrationState(FeatureFlags.UnsharedRenumberedGroupsKey));
        Assert.True(FeatureFlags.SiblingCascade(_db));
    }

    [Fact]
    public async Task Sync_MeasuresTheGate_EvenWhenItEndsPartial()
    {
        Plant("rx", "raptor", 2, 10, "1", "91051");
        Plant("vx", "valkyrie", 2, 4, "1", "91051");
        Plant("vz", "valkyrie", 2, 10, "1", null);
        var settings = new SettingsService(Path.Combine(_dir, SettingsService.SettingsFileName));
        settings.Update(s => s.Servers.Add(new ServerConfig { Id = "v1", Name = "Valkyrie", Url = "http://127.0.0.1:9", AccessToken = "t", UserId = "u" }));
        var sync = new SyncEngine(new EmbyClient(), _db, new DedupEngine(), new TraktService(new TraktAuthService(_db)), settings);

        await sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("1", _db.GetMigrationState(FeatureFlags.UnsharedRenumberedGroupsKey));
        Assert.False(FeatureFlags.SiblingCascade(_db));
    }
}
