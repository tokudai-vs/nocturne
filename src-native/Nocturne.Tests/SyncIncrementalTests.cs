using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// #20 item 5: a completed library is fetched incrementally (MinDateLastSavedForUser), not skipped on an equal item
// count and re-fetched in full on any other.
public class SyncIncrementalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");

    public SyncIncrementalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static EmbyItem Ep(string id, int e) =>
        new() { Id = id, Name = $"Episode {e}", Type = "Episode", SeriesId = "s", SeriesName = "Show", ParentIndexNumber = 1, IndexNumber = e, ProviderIds = new() { ["Tvdb"] = (1000 + e).ToString() } };

    [Fact]
    public async Task CompletedLibrary_FetchesOnlyWhatChanged_IncludingChangesThatKeepTheCount()
    {
        var fake = new FakeEmbyServer();
        var library = new List<EmbyItem> { new() { Id = "s", Name = "Show", Type = "Series", ProviderIds = new() { ["Tvdb"] = "1" } } };
        library.AddRange(Enumerable.Range(1, 300).Select(e => Ep($"e{e}", e)));
        fake.Libraries["valkyrie.test"] = library;
        foreach (var item in library) fake.Saved[item.Id] = DateTime.UtcNow.AddDays(-10);

        using var db = new DatabaseService(Path.Combine(_dir, "a.db"));
        var settings = new SettingsService(Path.Combine(_dir, SettingsService.SettingsFileName));
        settings.Update(s => s.Servers.Add(new ServerConfig { Id = "valkyrie", Name = "Valkyrie", Url = "http://valkyrie.test", AccessToken = "t", UserId = "u" }));
        EmbyClient ClientFor(ServerConfig s) { var c = new EmbyClient(fake); c.Configure(s.Url, s.AccessToken, s.UserId); return c; }
        var sync = new SyncEngine(new EmbyClient(), db, new DedupEngine(), new TraktService(new TraktAuthService(db)), settings, ClientFor);
        int Served() { lock (fake.Requests) return fake.Requests.Sum(r => r.Returned); }
        bool AnyUnfilteredPage() { lock (fake.Requests) return fake.Requests.Any(r => !r.Query.Contains("MinDateLastSavedForUser") && !r.Query.Contains("Limit=1&")); }

        await sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(301, db.GetCachedItemsCount());

        // One new episode: the count changes. Only the new episode is fetched, not the library.
        lock (fake.Requests) fake.Requests.Clear();
        library.Add(Ep("e301", 301));
        fake.Saved["e301"] = DateTime.UtcNow;
        await sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(302, db.GetCachedItemsCount());
        Assert.False(AnyUnfilteredPage());
        Assert.True(Served() <= 2, $"served {Served()} items for one new episode"); // the probe's one item and the episode

        // A change that keeps the count (renamed episode): the count check never fetched it.
        lock (fake.Requests) fake.Requests.Clear();
        library[5].Name = "Renamed";
        fake.Saved[library[5].Id] = DateTime.UtcNow;
        await sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("Renamed", db.GetCachedItem(library[5].Id, "valkyrie")!.Name);
        Assert.False(AnyUnfilteredPage());
    }
}
