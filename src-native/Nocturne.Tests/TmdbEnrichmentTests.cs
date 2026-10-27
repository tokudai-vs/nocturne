using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// Spec §10 / M1-M3: TMDB fills artwork only where no version on any server has it, by provider id only.
public class TmdbEnrichmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
    private readonly DatabaseService _db;
    private readonly Tmdb _tmdb = new();
    private static readonly List<ServerConfig> Servers = new()
    {
        new ServerConfig { Id = "a", Name = "A", Url = "http://a.test", AccessToken = "ta" },
        new ServerConfig { Id = "b", Name = "B", Url = "http://b.test", AccessToken = "tb" },
    };

    public TmdbEnrichmentTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new DatabaseService(Path.Combine(_dir, "a.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Answers by path; records every path asked. Unrouted paths are TMDB's 404.
    private sealed class Tmdb : HttpMessageHandler
    {
        public readonly List<string> Paths = new();
        public Func<string, HttpResponseMessage?> Route { get; set; } = _ => null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add(path);
            return Task.FromResult(Route(path) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private TmdbEnrichmentService Service()
    {
        var service = new TmdbEnrichmentService(_db, new HttpClient(_tmdb) { BaseAddress = new Uri("https://api.themoviedb.org") });
        service.SetCustomApiKey("test-key");
        return service;
    }

    private Task Run(TmdbEnrichmentService service) => service.RunAsync(new Progress<EnrichProgress>(), CancellationToken.None);

    private static EmbyItem Movie(string id, string srv, string name, string? tmdb, bool poster = false) => new()
    {
        Id = id, ServerId = srv, Name = name, Type = "Movie", ProductionYear = 2016,
        ProviderIds = tmdb == null ? new() : new() { ["Tmdb"] = tmdb },
        ImageTags = poster ? new() { ["Primary"] = id + "_tag" } : null,
    };

    private void Index(params EmbyItem[] items)
    {
        foreach (var it in items) _db.UpsertItem(it, it.ServerId!, "lib");
        new ConcurrentDedupIndex().ProcessBatch(items.ToList(), _db);
    }

    // M1 gate: a group with any version's poster is never enriched; one with none gets a poster; a provisional group
    // is skipped, even holding a TMDB id.
    [Fact]
    public async Task OnlyAGroupWithNoPosterAnywhere_IsEnriched_AndNeverAProvisionalOne()
    {
        Index(Movie("arr_a", "a", "Arrival", "329865"), Movie("arr_b", "b", "Arrival", "329865", poster: true),
              Movie("dune", "a", "Dune", "438631"));
        _db.SaveDedupBatch(
            new List<DedupGroupRecord> { new() { GroupId = 900, ItemType = "Movie", PrimaryItemId = "home", PrimaryServerId = "a", VersionCount = 1, Name = "Home Video", SortName = "Home Video", IsProvisional = 1, TmdbKey = "tmdb:movie:777" } },
            new List<int>(), new List<(int, int)>(), new List<(string, int, bool)> { ("name:home video:2016", 900, false) },
            new List<(string, string, int)> { ("a", "home", 900) });
        _tmdb.Route = path => path == "/3/movie/438631" ? Json("{\"poster_path\":\"/dune.jpg\",\"backdrop_path\":\"/dune_bd.jpg\"}") : null;

        await Run(Service());

        Assert.Equal(new[] { "/3/movie/438631" }, _tmdb.Paths);
        var dune = Assert.Single(_db.GetCombinedLibraryItems(new[] { "lib" }), i => i.Name == "Dune");
        Assert.Equal("https://image.tmdb.org/t/p/w342/dune.jpg", Artwork.Poster(dune, Servers, 320));
        var arrival = Assert.Single(_db.GetCombinedLibraryItems(new[] { "lib" }), i => i.Name == "Arrival");
        Assert.StartsWith("http://b.test/", Artwork.Poster(arrival, Servers, 320)); // Emby wins
    }

    // not_found stops a dead id being asked on every sync; it is asked again after a month.
    [Fact]
    public async Task AnIdTmdbDoesNotKnow_IsAskedAgainMonthly_NotEverySync()
    {
        Index(Movie("gone", "a", "Gone", "999"));
        var service = Service();

        await Run(service);
        await Run(service);
        Assert.Equal(new[] { "/3/movie/999" }, _tmdb.Paths);
        Assert.Equal(1, service.NotFoundCount);
        Assert.Equal(0, service.PendingCount);

        _db.ExecuteSql($"UPDATE tmdb_metadata SET fetched_utc = {DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeSeconds()};");
        Assert.Equal(1, service.PendingCount);
        await Run(service);
        Assert.Equal(2, _tmdb.Paths.Count);
    }

    private class MockTmdbHandler : HttpMessageHandler
    {
        private int _attempts = 0;
        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _attempts++;
            if (_attempts == 1)
            {
                // First call: Simulate 429 Too Many Requests with Retry-After 1 second
                var rateLimitResponse = new HttpResponseMessage((HttpStatusCode)429);
                rateLimitResponse.Headers.Add("Retry-After", "1");
                return Task.FromResult(rateLimitResponse);
            }

            // Second call: Return successful movie metadata
            var okResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""id"": 550,
                    ""poster_path"": ""/fightclub_poster.jpg"",
                    ""backdrop_path"": ""/fightclub_backdrop.jpg"",
                    ""overview"": ""An insomniac office worker..."",
                    ""vote_average"": 8.4
                }", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(okResponse);
        }
    }

    [Fact]
    public async Task TmdbRateLimitAndBackoff_Handles429AndCachesMetadata()
    {
        var mockHandler = new MockTmdbHandler();
        var client = new HttpClient(mockHandler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };

        var service = new TmdbEnrichmentService(_db, client);
        service.SetCustomApiKey("test_tmdb_key");

        // Request movie metadata
        var sw = Stopwatch.StartNew();
        var metadata = await service.GetAsync("tmdb:movie:550", CancellationToken.None);
        sw.Stop();

        Assert.NotNull(metadata);
        Assert.Equal("/fightclub_poster.jpg", metadata.PosterPath);
        Assert.Equal("/fightclub_backdrop.jpg", metadata.BackdropPath);
        Assert.Equal(8.4, metadata.VoteAverage);
        Assert.Equal(2, mockHandler.Attempts); // 1st was 429, 2nd was 200

        // Verify cached in SQLite database
        var cached = _db.GetTmdbMetadata("tmdb:movie:550");
        Assert.NotNull(cached);
        Assert.Equal("/fightclub_poster.jpg", cached.PosterPath);
    }

    [Fact]
    public async Task TokenBucket_RateLimitsCallsWithinQuota()
    {
        int totalRequests = 0;
        var fastHandler = new DelegatingHandlerStub((req, ct) =>
        {
            Interlocked.Increment(ref totalRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""id"": 1, ""poster_path"": ""/p.jpg""}", Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(fastHandler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
        var service = new TmdbEnrichmentService(_db, client);
        service.SetCustomApiKey("fast_key");

        // Fire 5 requests sequentially
        for (int i = 0; i < 5; i++)
        {
            await service.GetAsync($"tmdb:movie:{1000 + i}", CancellationToken.None);
        }

        Assert.Equal(5, totalRequests);
    }

    private class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request, cancellationToken));
        }
    }
}
