using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// #21 item 1: an item deleted on the server appears in no incremental pass; the sweep finds it by listing ids.
public class DeletionSweepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
    private readonly FakeEmbyServer _fake = new();
    private readonly string _dbPath;
    private readonly DatabaseService _db;
    private readonly SyncEngine _sync;

    public DeletionSweepTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "a.db");
        _db = new DatabaseService(_dbPath);
        var settings = new SettingsService(Path.Combine(_dir, SettingsService.SettingsFileName));
        settings.Update(s =>
        {
            s.Servers.Add(new ServerConfig { Id = "raptor", Name = "Raptor", Url = "http://raptor.test", AccessToken = "t", UserId = "u" });
            s.Servers.Add(new ServerConfig { Id = "valkyrie", Name = "Valkyrie", Url = "http://valkyrie.test", AccessToken = "t", UserId = "u" });
            s.PreferredServerPriority = new() { "raptor", "valkyrie" };
        });
        EmbyClient ClientFor(ServerConfig s) { var c = new EmbyClient(_fake); c.Configure(s.Url, s.AccessToken, s.UserId); return c; }
        _sync = new SyncEngine(new EmbyClient(), _db, new DedupEngine(), new TraktService(new TraktAuthService(_db)), settings, ClientFor);

        // Raptor's copy of "Shared" scores higher (backdrop, overview) and is the poster's server by priority.
        _fake.Libraries["raptor.test"] = new List<EmbyItem>
        {
            Movie("r-shared", "1", "rposter", "rbackdrop", "Raptor overview"),
            Movie("r-alone", "2", "aposter", null, null),
        }.Concat(Enumerable.Range(10, 20).Select(i => Movie($"r{i}", i.ToString(), null, null, null))).ToList();
        _fake.Libraries["valkyrie.test"] = new() { Movie("v-shared", "1", "vposter", null, null) };
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static EmbyItem Movie(string id, string tmdb, string? poster, string? backdrop, string? overview) => new()
    {
        Id = id, Name = "Movie " + tmdb, Type = "Movie", ProductionYear = 2000, Overview = overview,
        ProviderIds = new() { ["Tmdb"] = tmdb },
        ImageTags = poster == null ? null : new() { ["Primary"] = poster },
        BackdropImageTags = backdrop == null ? null : new() { backdrop },
    };

    private long Count(string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private void Delete(string host, string id) => _fake.Libraries[host].RemoveAll(i => i.Id == id);

    [Fact]
    public async Task Sweep_RemovesDeletedItems_ReelectsTheSurvivor_AndDropsAnEmptyGroup()
    {
        await _sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var shared = _db.GetDedupGroup(Count("SELECT group_id FROM cached_items WHERE id = 'v-shared'"))!;
        var alone = Count("SELECT group_id FROM cached_items WHERE id = 'r-alone'");
        Assert.Equal(("r-shared", "rposter", "rbackdrop", 2), (shared.PrimaryItemId, shared.ImageTag, shared.BackdropTag, shared.VersionCount));
        _db.UpdateUserDataState("raptor", "r-shared", 100, 1000, false, 1);
        _db.EnqueueSiblingWrite("raptor", "r-shared", 100, false, Origin.Local);
        _db.UpsertWatchlistEntry("raptor", "r-shared", "Movie", 1);
        _db.UpsertWatchlistEntry("raptor", "r-alone", "Movie", 1);
        _db.EnqueueTraktEvent(alone, "scrobble", 50, null, Origin.Local);
        _db.EnqueueTraktEvent(shared.GroupId, "scrobble", 50, null, Origin.Local);

        Delete("raptor.test", "r-shared");
        Delete("raptor.test", "r-alone");
        var result = await _sync.RunDeletionSweepAsync();

        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.Skipped);
        Assert.Null(_db.GetCachedItem("r-shared", "raptor"));
        var after = _db.GetDedupGroup(shared.GroupId)!;
        Assert.Equal(("v-shared", "valkyrie", "Movie 1", 1), (after.PrimaryItemId, after.PrimaryServerId, after.Name, after.VersionCount));
        Assert.Equal(("vposter", "v-shared", "valkyrie"), (after.ImageTag, after.ImageItemId, after.ImageServerId));
        Assert.Null(after.BackdropTag);
        Assert.Null(_db.GetDedupGroup(alone));
        Assert.Equal(0, Count($"SELECT COUNT(*) FROM dedup_keys WHERE group_id = {alone}"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM dedup_keys WHERE key = 'item:raptor:r-shared'"));
        Assert.Equal(0, Count($"SELECT COUNT(*) FROM trakt_queue WHERE group_id = {alone}"));
        Assert.Equal(1, Count($"SELECT COUNT(*) FROM trakt_queue WHERE group_id = {shared.GroupId}")); // the group lives on
        Assert.Equal(0, Count("SELECT COUNT(*) FROM user_data WHERE item_id = 'r-shared'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM sibling_write_queue WHERE item_id = 'r-shared'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM watchlist_entries WHERE source = 'raptor'"));
        Assert.Equal(20, Count("SELECT COUNT(*) FROM cached_items WHERE server_id = 'raptor'"));
    }

    [Fact]
    public async Task Sweep_KeepsAnItemTheListingMissed_WhenTheServerStillHasIt()
    {
        await _sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));
        _fake.Unlisted.Add("r-alone"); // a paging shift: absent from the listing, there when asked by id

        var result = await _sync.RunDeletionSweepAsync();

        Assert.Equal(0, result.Removed);
        Assert.NotNull(_db.GetCachedItem("r-alone", "raptor"));
    }

    [Fact]
    public async Task Sweep_LeavesALibraryAlone_WhenItsListingFails_OrMostOfItIsMissing()
    {
        await _sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Delete("raptor.test", "r-alone");
        _fake.Fail = uri => uri.Host == "raptor.test" && uri.Query.Contains("StartIndex=0") && uri.Query.Contains("Limit=50000");

        var failed = await _sync.RunDeletionSweepAsync();

        Assert.Equal((0, 1), (failed.Removed, failed.Skipped));
        Assert.NotNull(_db.GetCachedItem("r-alone", "raptor"));

        // An empty listing (an unmounted share) reads as everything deleted.
        _fake.Fail = null;
        _fake.Libraries["raptor.test"].Clear();
        var empty = await _sync.RunDeletionSweepAsync();

        Assert.Equal((0, 1), (empty.Removed, empty.Skipped));
        Assert.Equal(22, Count("SELECT COUNT(*) FROM cached_items WHERE server_id = 'raptor'"));
    }

    [Fact]
    public async Task Sync_SweepsWeekly()
    {
        await _sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(_db.GetMigrationState(SyncEngine.DeletionSweepKey));
        Delete("raptor.test", "r-alone");

        await _sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(_db.GetCachedItem("r-alone", "raptor")); // swept a moment ago: not due

        _db.SetMigrationState(SyncEngine.DeletionSweepKey, DateTime.UtcNow.AddDays(-8).ToString("o"));
        await _sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Null(_db.GetCachedItem("r-alone", "raptor"));
    }
}
