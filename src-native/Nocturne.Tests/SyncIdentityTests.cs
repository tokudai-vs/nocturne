using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Web;
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

    // One library per server, listed in the order Emby's SortName paging returned the live library: the episode
    // before its own Series row.
    private sealed class FakeEmby : HttpMessageHandler
    {
        public readonly Dictionary<string, List<EmbyItem>> Libraries = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var items = Libraries[uri.Host];
            if (uri.AbsolutePath.EndsWith("/Views"))
                return Json(new { Items = new[] { new { Id = "lib", Name = "TV Shows", CollectionType = "tvshows", Type = "CollectionFolder" } }, TotalRecordCount = 1 });
            if (uri.AbsolutePath.EndsWith("/Items"))
            {
                var q = HttpUtility.ParseQueryString(uri.Query);
                var listed = q["IncludeItemTypes"] is { } types ? items.Where(i => types.Split(',').Contains(i.Type)).ToList() : items;
                var start = int.Parse(q["StartIndex"] ?? "0");
                var limit = int.Parse(q["Limit"] ?? "50");
                return Json(new { Items = listed.Skip(start).Take(limit), TotalRecordCount = listed.Count });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(object body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") });
    }

    private static EmbyItem Series(string id, string tvdb) =>
        new() { Id = id, Name = "Bag Show", Type = "Series", ProviderIds = new() { ["Tvdb"] = tvdb } };

    private static EmbyItem Episode(string id, string seriesId) =>
        new() { Id = id, Name = "... And a Bag of Chips", Type = "Episode", SeriesId = seriesId, SeriesName = "Bag Show",
                ParentIndexNumber = 6, IndexNumber = 8, ProviderIds = new() { ["Tvdb"] = "100" } };

    [Fact]
    public async Task Sync_GroupsLikeARebuild_WhenEpisodesArriveBeforeTheirSeries()
    {
        var fake = new FakeEmby();
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
}
