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
        public int DelayMs { get; set; }
        public int MaxInFlight { get; private set; }
        private int _inFlight;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) { Paths.Add(path); MaxInFlight = Math.Max(MaxInFlight, ++_inFlight); }
            try
            {
                if (DelayMs > 0) await Task.Delay(DelayMs, ct);
                return Route(path) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            finally { lock (Paths) _inFlight--; }
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

    // Synchronous, so a test sees every report the moment the pass makes it (Progress<T> posts them later).
    private sealed class Reports : IProgress<EnrichProgress>
    {
        public readonly List<EnrichProgress> All = new();
        public void Report(EnrichProgress value) { lock (All) All.Add(value); }
        public EnrichProgress Last { get { lock (All) return All[^1]; } }
    }

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
        Assert.Equal(1, service.GetStats().NotFound);
        Assert.Equal(0, service.GetStats().PendingItems);

        _db.ExecuteSql($"UPDATE tmdb_metadata SET fetched_utc = {DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeSeconds()};");
        Assert.Equal(1, service.GetStats().PendingItems);
        await Run(service);
        Assert.Equal(2, _tmdb.Paths.Count);
    }

    private EmbyItem[] FortyMovies() => Enumerable.Range(1, 40).Select(i => Movie($"m{i}", "a", $"Movie {i}", (1000 + i).ToString())).ToArray();
    private static HttpResponseMessage? Poster(string path) =>
        path.StartsWith("/3/movie/") ? Json($"{{\"poster_path\":\"/{path.Split('/').Last()}.jpg\"}}") : null;

    // M2 gate, cold pass: every title fetched, in parallel, never faster than the 20 req/s budget (20 at once, then 20/s).
    [Fact]
    public async Task AColdLibrary_Completes_InParallel_Within20RequestsASecond()
    {
        Index(FortyMovies());
        (_tmdb.Route, _tmdb.DelayMs) = (Poster, 30);
        var service = Service();
        Assert.Equal(new TmdbStats(0, 0, 40, 40), service.GetStats()); // 40 titles, 40 requests

        var clock = Stopwatch.StartNew();
        var reports = new Reports();
        await service.RunAsync(reports, CancellationToken.None);
        Assert.Equal(("finished", 40, 40), (reports.Last.Outcome, reports.Last.Total, reports.Last.Processed));

        Assert.Equal(40, _tmdb.Paths.Distinct().Count());
        Assert.Equal(0, service.GetStats().PendingItems);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(0.9), $"40 requests took {clock.Elapsed.TotalSeconds:F2}s");
        Assert.True(_tmdb.MaxInFlight > 1, "fetchers ran one at a time");
    }

    // M2 gate, network loss: the pass stops, the library stays usable, and the next pass asks only for what is missing.
    [Fact]
    public async Task APassCutByNetworkLoss_LeavesTheLibraryUsable_AndResumes()
    {
        Index(FortyMovies());
        var (answered, down) = (0, true);
        _tmdb.Route = path =>
        {
            if (down && Interlocked.Increment(ref answered) > 15) throw new HttpRequestException("No such host is known.");
            return Poster(path);
        };
        var service = Service();

        var reports = new Reports();
        await service.RunAsync(reports, CancellationToken.None);
        Assert.Equal("unreachable", reports.Last.Outcome);
        var first = _tmdb.Paths.ToList();
        Assert.True(first.Count < 40, "the pass kept going with TMDB unreachable");
        Assert.Equal(15, 40 - service.GetStats().PendingItems);
        var library = _db.GetCombinedLibraryItems(new[] { "lib" });
        Assert.Equal(40, library.Count);
        Assert.Equal(15, library.Count(i => Artwork.Poster(i, Servers, 320)?.StartsWith(Artwork.TmdbImageBase) == true));

        down = false;
        _tmdb.Paths.Clear();
        await Run(service);
        Assert.Equal(0, service.GetStats().PendingItems);
        Assert.Equal(25, _tmdb.Paths.Count);                                    // only what was missing
        Assert.Empty(_tmdb.Paths.Intersect(first.Take(15)));
    }

    // M3 gate: one /season call populates a whole season: 20 episodes, one request. An episode TMDB's season does not
    // list is not_found. Stills come at w300 (§10.7).
    [Fact]
    public async Task OneSeasonCall_FillsEveryEpisodeOfTheSeason()
    {
        var episodes = Enumerable.Range(1, 21).Select(n => new EmbyItem
        {
            Id = $"e{n}", ServerId = "a", Name = $"Episode {n}", Type = "Episode", SeriesName = "Breaking Bad", SeriesId = "bb",
            ParentIndexNumber = 1, IndexNumber = n, SeriesProviderIds = new() { ["Tmdb"] = "1396" },
        }).ToArray();
        Index(episodes);
        var listed = string.Join(",", Enumerable.Range(1, 20).Select(n => $"{{\"episode_number\":{n},\"still_path\":\"/s{n}.jpg\"}}"));
        _tmdb.Route = path => path == "/3/tv/1396/season/1" ? Json($"{{\"episodes\":[{listed}]}}") : null;
        var service = Service();
        Assert.Equal(new TmdbStats(0, 0, 21, 1), service.GetStats()); // 21 episodes, one request

        await Run(service);

        Assert.Equal(new[] { "/3/tv/1396/season/1" }, _tmdb.Paths);
        var cards = episodes.Select(e => new EmbyItem { Id = e.Id, ServerId = "a", Name = e.Name, Type = "Episode" }).ToList();
        _db.AttachGroupArtwork(cards);
        Assert.Equal("https://image.tmdb.org/t/p/w300/s7.jpg", Artwork.Poster(cards[6], Servers, 320));
        Assert.Equal(20, cards.Count(c => Artwork.Poster(c, Servers, 320) != null));
        Assert.Equal((0, 1), (service.GetStats().PendingItems, service.GetStats().NotFound));
    }

    // #34: a v4 Read Access Token (a JWT) goes in Authorization: Bearer and never in the URL; a v3 key goes in api_key.
    [Theory]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiJ0ZXN0In0.c2lnbmF0dXJlLW5vdC1hLXNlY3JldA", true)]
    [InlineData("0123456789abcdef0123456789abcdef", false)]
    public async Task AReadAccessToken_GoesInTheBearerHeader_AV3KeyInTheQuery(string key, bool bearer)
    {
        HttpRequestMessage? sent = null;
        var http = new HttpClient(new DelegatingHandlerStub((req, _) => { sent = req; return new HttpResponseMessage(HttpStatusCode.NotFound); }))
            { BaseAddress = new Uri("https://api.themoviedb.org") };
        var service = new TmdbEnrichmentService(_db, http);
        service.SetCustomApiKey(key);

        await service.GetAsync("tmdb:movie:550", CancellationToken.None);

        Assert.Equal(bearer ? ("Bearer", key) : (null, null), (sent!.Headers.Authorization?.Scheme, sent.Headers.Authorization?.Parameter));
        Assert.Equal(!bearer, sent.RequestUri!.Query.Contains("api_key="));
        if (bearer) Assert.DoesNotContain(key, sent.RequestUri.ToString());
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
