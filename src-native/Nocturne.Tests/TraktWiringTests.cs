using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;
using Xunit;

namespace Nocturne.Tests;

// Stage I: the custom client ID path end to end, token refresh, the retired legacy stack, the watched-state gate.
public class TraktWiringTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
    private readonly DatabaseService _db;

    public TraktWiringTests() => _db = new DatabaseService(_dbPath);

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private sealed class Trakt : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? ApiKey, string? Bearer, string Body, string Agent, string? Version)> Requests = new();
        public Func<string, HttpResponseMessage?> Route { get; set; } = _ => null;
        public Func<Uri, HttpResponseMessage?>? RouteUri { get; set; } // #42: page, limit and extended are in the query
        public readonly List<string> Uris = new(); // path and query

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(ct) : "";
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path,
                request.Headers.TryGetValues("trakt-api-key", out var k) ? k.First() : null,
                request.Headers.Authorization?.Parameter, body, request.Headers.UserAgent.ToString(),
                request.Headers.TryGetValues("trakt-api-version", out var v) ? v.First() : null));
            Uris.Add(request.RequestUri.PathAndQuery);
            return RouteUri?.Invoke(request.RequestUri) ?? Route(path) ?? Json(HttpStatusCode.OK, "[]");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient Client(Trakt handler) => new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };

    private void SeedConnected(string clientId, long expiresInSec)
    {
        _db.SetIntegrationState("trakt", "mode", "Custom");
        _db.SetIntegrationState("trakt", "custom_client_id", clientId);
        _db.SetIntegrationState("trakt", "client_id_used", clientId);
        _db.SetIntegrationState("trakt", "access_token_dpapi", Convert.ToHexString(CredentialProtection.ProtectString("tok_old")));
        _db.SetIntegrationState("trakt", "refresh_token_dpapi", Convert.ToHexString(CredentialProtection.ProtectString("ref_old")));
        _db.SetIntegrationState("trakt", "expires_utc", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresInSec).ToString());
    }

    // I1
    [Fact]
    public async Task WithNoBundledClientId_OnlyACustomOneCanConnect()
    {
        var auth = new TraktAuthService(_db, Client(new Trakt()));
        Assert.Equal("", auth.GetActiveClientId());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.RequestDeviceCodeAsync(CancellationToken.None));
        Assert.Contains("Client ID", ex.Message);
    }

    // I2 + I6
    [Fact]
    public async Task CustomClientId_ConnectsEndToEnd_AndTheReadStackUsesIt()
    {
        var trakt = new Trakt
        {
            Route = path => path switch
            {
                "/oauth/device/code" => Json(HttpStatusCode.OK, "{\"device_code\":\"dc\",\"user_code\":\"ABCD1234\",\"verification_url\":\"https://trakt.tv/activate\",\"expires_in\":600,\"interval\":5}"),
                "/oauth/device/token" => Json(HttpStatusCode.OK, "{\"access_token\":\"tok_new\",\"refresh_token\":\"ref_new\",\"expires_in\":7776000}"),
                "/users/me" => Json(HttpStatusCode.OK, "{\"username\":\"viewer\",\"private\":false,\"ids\":{\"slug\":\"viewer\"}}"), // /users/me's real shape
                _ => null
            }
        };
        var auth = new TraktAuthService(_db, Client(trakt));

        auth.SetCustomClientId("  custom-client-123 ");
        var code = await auth.RequestDeviceCodeAsync(CancellationToken.None);
        var result = await auth.PollForTokenAsync(code, CancellationToken.None);

        Assert.Equal(TraktAuthStatus.Authorized, result.Status);
        Assert.Equal(TraktConnectionState.Connected, auth.State);
        Assert.Equal("viewer", auth.Username);
        var deviceRequest = trakt.Requests.Single(r => r.Path == "/oauth/device/code");
        Assert.Equal("custom-client-123", JsonDocument.Parse(deviceRequest.Body).RootElement.GetProperty("client_id").GetString());

        // The watchlist/stats reader authenticates through the same auth: connected, same client ID and token.
        var reader = new TraktService(auth, Client(trakt));
        Assert.True(reader.IsConnected);
        await reader.GetWatchlistAsync();
        var watchlist = trakt.Requests.First(r => r.Path == "/sync/watchlist/movies");
        Assert.Equal(("custom-client-123", "tok_new"), (watchlist.ApiKey, watchlist.Bearer));

        // Survives a restart.
        var reloaded = new TraktAuthService(_db, Client(trakt));
        Assert.Equal(TraktConnectionState.Connected, reloaded.State);
        Assert.Equal("custom-client-123", reloaded.GetActiveClientId());
    }

    // #33: Cloudflare in front of api.trakt.tv answers a request with no User-Agent with 403; every Trakt call identifies
    // itself and carries the API version and key, the OAuth calls included.
    [Fact]
    public async Task EveryTraktCall_CarriesAUserAgent_TheApiVersion_AndTheKey()
    {
        var trakt = new Trakt
        {
            Route = path => path switch
            {
                "/oauth/device/code" => Json(HttpStatusCode.OK, "{\"device_code\":\"dc\",\"user_code\":\"ABCD1234\",\"verification_url\":\"https://trakt.tv/activate\",\"expires_in\":600,\"interval\":5}"),
                "/oauth/device/token" => Json(HttpStatusCode.OK, "{\"access_token\":\"tok\",\"refresh_token\":\"ref\",\"expires_in\":3600}"),
                "/users/me" => Json(HttpStatusCode.OK, "{\"username\":\"viewer\",\"private\":false,\"ids\":{\"slug\":\"viewer\"}}"), // /users/me's real shape
                _ => null
            }
        };
        var auth = new TraktAuthService(_db, Client(trakt));
        auth.SetCustomClientId("custom-client-123");

        await auth.PollForTokenAsync(await auth.RequestDeviceCodeAsync(CancellationToken.None), CancellationToken.None);
        await auth.RefreshIfNeededAsync(CancellationToken.None); // expires inside the refresh window: /oauth/token
        await new TraktSyncService(_db, auth, Client(trakt)).PullWatchedAsync(CancellationToken.None);
        await auth.DisconnectAsync(CancellationToken.None);      // /oauth/revoke

        Assert.Contains(trakt.Requests, r => r.Path == "/oauth/token");
        Assert.Contains(trakt.Requests, r => r.Path == "/oauth/revoke");
        Assert.All(trakt.Requests, r =>
        {
            Assert.StartsWith("Nocturne/", r.Agent);
            Assert.Equal("2", r.Version);
            Assert.Equal("custom-client-123", r.ApiKey);
        });
    }

    // #29: a connection made while /users/me was misread has no name; the startup refresh check fills it in once.
    [Fact]
    public async Task AConnectionWithNoName_GetsItAtTheNextStart()
    {
        SeedConnected("c1", expiresInSec: 30 * 86400);
        var trakt = new Trakt { Route = path => path == "/users/me" ? Json(HttpStatusCode.OK, "{\"username\":\"viewer\"}") : null };
        var auth = new TraktAuthService(_db, Client(trakt));
        Assert.True(string.IsNullOrEmpty(auth.Username));

        Assert.True(await auth.RefreshIfNeededAsync(CancellationToken.None));

        Assert.Equal("viewer", auth.Username);
        Assert.Equal("viewer", new TraktAuthService(_db, Client(trakt)).Username); // saved
        await auth.RefreshIfNeededAsync(CancellationToken.None);
        Assert.Single(trakt.Requests, r => r.Path == "/users/me");                // once
    }

    // I4
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData((HttpStatusCode)429)]
    [InlineData((HttpStatusCode)0)] // network failure
    public async Task TransientRefreshFailure_KeepsTheSession(HttpStatusCode status)
    {
        SeedConnected("c1", expiresInSec: 3600); // inside the 24 h refresh window
        var trakt = new Trakt
        {
            Route = path => status == 0 ? throw new HttpRequestException("No such host is known.") : Json(status, "{}")
        };
        var auth = new TraktAuthService(_db, Client(trakt));

        Assert.False(await auth.RefreshIfNeededAsync(CancellationToken.None));

        Assert.Equal(TraktConnectionState.Connected, auth.State);
        Assert.Equal("tok_old", auth.GetAccessToken());
    }

    [Fact]
    public async Task RejectedRefreshToken_EndsTheSession()
    {
        SeedConnected("c1", expiresInSec: 3600);
        var auth = new TraktAuthService(_db, Client(new Trakt { Route = _ => Json(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_grant\"}") }));

        Assert.False(await auth.RefreshIfNeededAsync(CancellationToken.None));

        Assert.Equal(TraktConnectionState.NeedsReconnection, auth.State);
        Assert.Null(auth.GetAccessToken());
    }

    // Trakt pull marks a whole dedup group only when the sibling cascade is engaged.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PulledWatchedState_MarksTheWholeGroup_OnlyWithTheCascadeOn(bool cascadeOn)
    {
        ArrivalOnTwoServers(cascadeOn);

        SeedConnected("c1", expiresInSec: 30 * 86400);
        var trakt = new Trakt
        {
            Route = path => path switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, "{\"movies\":{\"watched_at\":\"2026-10-21T05:00:00.000Z\"},\"episodes\":{\"watched_at\":\"2026-10-21T05:00:00.000Z\"}}"),
                "/sync/watched/movies" => Json(HttpStatusCode.OK, "[{\"plays\":1,\"last_watched_at\":\"2026-10-21T05:00:00.000Z\",\"movie\":{\"title\":\"Arrival\",\"year\":2016,\"ids\":{\"trakt\":1,\"tmdb\":329865}}}]"),
                _ => null
            }
        };
        var auth = new TraktAuthService(_db, Client(trakt));

        await new TraktSyncService(_db, auth, Client(trakt)).PullWatchedAsync(CancellationToken.None);

        Assert.True(_db.GetUserData("s1", "arr_a")?.Played);
        Assert.Equal(cascadeOn, _db.GetUserData("s2", "arr_b")?.Played == true);
    }

    // #37: the cascade is decided per group. A pull marks every version of a group whose versions agree on the
    // episode, and only the primary of one whose servers number it differently (a shared id may be a shifted one).
    [Fact]
    public async Task PulledWatchedState_CascadesPerGroup()
    {
        EmbyItem Episode(string srv, string name, int number, string tvdb) => new()
        {
            Id = $"{name}_{srv}", ServerId = srv, Name = name, Type = "Episode", SeriesId = $"bb_{srv}", SeriesName = "Breaking Bad",
            ParentIndexNumber = 1, IndexNumber = number, ProviderIds = new() { ["Tvdb"] = tvdb }
        };
        var items = new List<EmbyItem>();
        foreach (var srv in new[] { "s1", "s2" })
        {
            items.Add(new EmbyItem { Id = $"bb_{srv}", ServerId = srv, Name = "Breaking Bad", Type = "Series", ProviderIds = new() { ["Tvdb"] = "81189" } });
            items.Add(Episode(srv, "pilot", 1, "349232"));
        }
        items.Add(Episode("s1", "cat", 2, "349235"));
        items.Add(Episode("s2", "cat", 3, "349235")); // s2 numbers the same id one later
        foreach (var item in items) _db.UpsertItem(item, item.ServerId!);
        var index = new ConcurrentDedupIndex();
        index.SetServerPriority(new List<string> { "s1", "s2" });
        index.ProcessBatch(items, _db);
        _db.SetMigrationState(ConcurrentDedupIndex.EngineMarkerKey, ConcurrentDedupIndex.EngineVersion);

        SeedConnected("c1", expiresInSec: 30 * 86400);
        var trakt = new Trakt
        {
            Route = path => path switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, "{\"movies\":{\"watched_at\":\"2026-10-21T05:00:00.000Z\"},\"episodes\":{\"watched_at\":\"2026-10-21T05:00:00.000Z\"}}"),
                "/sync/watched/shows" => Json(HttpStatusCode.OK, "[{\"show\":{\"title\":\"Breaking Bad\",\"ids\":{\"tvdb\":81189}},\"seasons\":[{\"number\":1,\"episodes\":[" +
                    "{\"number\":1,\"last_watched_at\":\"2026-10-20T00:00:00.000Z\"},{\"number\":2,\"last_watched_at\":\"2026-10-20T00:00:00.000Z\"}]}]}]"),
                _ => null
            }
        };
        var auth = new TraktAuthService(_db, Client(trakt));

        await new TraktSyncService(_db, auth, Client(trakt)).PullWatchedAsync(CancellationToken.None);

        Assert.True(_db.GetUserData("s1", "pilot_s1")?.Played);
        Assert.True(_db.GetUserData("s2", "pilot_s2")?.Played);
        Assert.True(_db.GetUserData("s1", "cat_s1")?.Played);
        Assert.NotEqual(true, _db.GetUserData("s2", "cat_s2")?.Played);
    }

    // #42: Trakt pages /sync/watched since 2026-07-03, and shows carry seasons only with extended=progress.
    private const string BothChanged = "{\"movies\":{\"watched_at\":\"2026-10-21T05:00:00.000Z\"},\"episodes\":{\"watched_at\":\"2026-10-21T05:00:00.000Z\"}}";

    private static HttpResponseMessage Page(string json, int pages, int total)
    {
        var r = Json(HttpStatusCode.OK, json);
        r.Headers.Add("X-Pagination-Page-Count", pages.ToString());
        r.Headers.Add("X-Pagination-Item-Count", total.ToString());
        return r;
    }

    private static string WatchedMovie(string title, int tmdb) =>
        $"[{{\"plays\":1,\"last_watched_at\":\"2026-10-20T00:00:00.000Z\",\"movie\":{{\"title\":\"{title}\",\"ids\":{{\"tmdb\":{tmdb}}}}}}}]";

    private static string WatchedShow(string title, int tvdb, bool seasons) =>
        $"[{{\"show\":{{\"title\":\"{title}\",\"ids\":{{\"tvdb\":{tvdb}}}}}" +
        (seasons ? ",\"seasons\":[{\"number\":1,\"episodes\":[{\"number\":1,\"plays\":1,\"last_watched_at\":\"2026-10-20T00:00:00.000Z\"}]}]" : "") + "}]";

    private void Library(params EmbyItem[] items)
    {
        foreach (var item in items) _db.UpsertItem(item, item.ServerId!);
        var index = new ConcurrentDedupIndex();
        index.SetServerPriority(new List<string> { "s1", "s2" });
        index.ProcessBatch(items.ToList(), _db);
        _db.SetMigrationState(ConcurrentDedupIndex.EngineMarkerKey, ConcurrentDedupIndex.EngineVersion);
    }

    private static EmbyItem Film(string id, string name, string tmdb) =>
        new() { Id = id, ServerId = "s1", Name = name, Type = "Movie", ProviderIds = new() { ["Tmdb"] = tmdb } };

    private static EmbyItem[] Pilot(string show, string tvdb) => new[]
    {
        new EmbyItem { Id = $"{show}_series", ServerId = "s1", Name = show, Type = "Series", ProviderIds = new() { ["Tvdb"] = tvdb } },
        new EmbyItem { Id = $"{show}_101", ServerId = "s1", Name = "Pilot", Type = "Episode", SeriesId = $"{show}_series", SeriesName = show, ParentIndexNumber = 1, IndexNumber = 1 },
    };

    [Fact]
    public async Task WatchedMovies_AreReadPageByPage()
    {
        Library(Film("arr", "Arrival", "329865"), Film("dune", "Dune", "438631"));
        SeedConnected("c1", expiresInSec: 30 * 86400);
        var trakt = new Trakt
        {
            RouteUri = uri => uri.AbsolutePath switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, BothChanged),
                "/sync/watched/movies" when uri.Query.Contains("page=1&") => Page(WatchedMovie("Arrival", 329865), pages: 2, total: 2),
                "/sync/watched/movies" when uri.Query.Contains("page=2&") => Page(WatchedMovie("Dune", 438631), pages: 2, total: 2),
                _ => null
            }
        };

        await new TraktSyncService(_db, new TraktAuthService(_db, Client(trakt)), Client(trakt)).PullWatchedAsync(CancellationToken.None);

        Assert.True(_db.GetUserData("s1", "arr")?.Played);
        Assert.True(_db.GetUserData("s1", "dune")?.Played); // page 2: a single request used to stop at Trakt's first 100
        Assert.Contains(trakt.Uris, u => u.StartsWith("/sync/watched/movies?page=1&limit=250"));
    }

    [Fact]
    public async Task WatchedShows_AskForSeasons_AndAreReadPageByPage()
    {
        Library(Pilot("bb", "81189").Concat(Pilot("dm", "292174")).ToArray());
        SeedConnected("c1", expiresInSec: 30 * 86400);
        var trakt = new Trakt
        {
            RouteUri = uri => uri.AbsolutePath switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, BothChanged),
                // Trakt's default since July 2026: shows without seasons
                "/sync/watched/shows" when !uri.Query.Contains("extended=progress") => Page(WatchedShow("Breaking Bad", 81189, seasons: false), 1, 1),
                "/sync/watched/shows" when uri.Query.Contains("page=1&") => Page(WatchedShow("Breaking Bad", 81189, seasons: true), pages: 2, total: 2),
                "/sync/watched/shows" when uri.Query.Contains("page=2&") => Page(WatchedShow("Dark Matter", 292174, seasons: true), pages: 2, total: 2),
                _ => null
            }
        };

        await new TraktSyncService(_db, new TraktAuthService(_db, Client(trakt)), Client(trakt)).PullWatchedAsync(CancellationToken.None);

        Assert.True(_db.GetUserData("s1", "bb_101")?.Played);
        Assert.True(_db.GetUserData("s1", "dm_101")?.Played);
        Assert.Contains(trakt.Uris, u => u.StartsWith("/sync/watched/shows?extended=progress&page=1&limit=100"));
    }

    // Pulls before the paginated format advanced the activity markers on partial lists: the first pull after it
    // fetches everything once, then only changes again.
    [Fact]
    public async Task AfterThePullFormatChange_OnePullFetchesEverything_ThenOnlyChanges()
    {
        Library(Film("arr", "Arrival", "329865"));
        SeedConnected("c1", expiresInSec: 30 * 86400);
        _db.SetIntegrationState("trakt", "last_movies_watched_at", "2026-10-21T05:00:00.000Z"); // as an old pull left them
        _db.SetIntegrationState("trakt", "last_episodes_watched_at", "2026-10-21T05:00:00.000Z");
        var trakt = new Trakt
        {
            RouteUri = uri => uri.AbsolutePath switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, BothChanged),
                "/sync/watched/movies" => Page(WatchedMovie("Arrival", 329865), 1, 1),
                _ => null
            }
        };
        var sync = new TraktSyncService(_db, new TraktAuthService(_db, Client(trakt)), Client(trakt));
        int WatchedRequests() => trakt.Uris.Count(u => u.StartsWith("/sync/watched/"));

        await sync.PullWatchedAsync(CancellationToken.None);
        Assert.Equal(2, WatchedRequests());
        Assert.True(_db.GetUserData("s1", "arr")?.Played);

        await sync.PullWatchedAsync(CancellationToken.None);
        Assert.Equal(2, WatchedRequests());
    }

    private void ArrivalOnTwoServers(bool cascadeOn)
    {
        var a = new EmbyItem { Id = "arr_a", ServerId = "s1", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } } };
        var b = new EmbyItem { Id = "arr_b", ServerId = "s2", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } } };
        _db.UpsertItem(a, "s1");
        _db.UpsertItem(b, "s2");
        var index = new ConcurrentDedupIndex();
        index.SetServerPriority(new List<string> { "s1", "s2" });
        index.ProcessBatch(new List<EmbyItem> { a, b }, _db);
        if (cascadeOn)
        {
            _db.SetMigrationState(ConcurrentDedupIndex.EngineMarkerKey, ConcurrentDedupIndex.EngineVersion);
            FeatureFlags.MeasureCascadeGate(_db);
        }
    }

    private const string Activities = "{\"movies\":{\"watched_at\":\"2026-10-21T05:00:00.000Z\"},\"episodes\":{\"watched_at\":\"2026-10-01T00:00:00.000Z\"},\"watchlist\":{\"updated_at\":\"2026-10-01T00:00:00.000Z\"}}";
    private const string ArrivalWatched = "[{\"plays\":1,\"last_watched_at\":\"2026-10-21T05:00:00.000Z\",\"movie\":{\"title\":\"Arrival\",\"year\":2016,\"ids\":{\"trakt\":1,\"tmdb\":329865}}}]";

    private sealed class Emby : HttpMessageHandler
    {
        public readonly List<string> Posts = new(); // host + path and query
        public readonly List<string> Deletes = new();
        public bool Offline;
        public Action? OnSend;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("No such host is known.");
            OnSend?.Invoke();
            var at = request.RequestUri!.Host + request.RequestUri.PathAndQuery;
            lock (Posts)
            {
                if (request.Method == HttpMethod.Post) Posts.Add(at);
                else if (request.Method == HttpMethod.Delete) Deletes.Add(at);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    // The app's cycle, every queue real: Emby favourites and the Trakt queue out, the Trakt pull, the sibling queue.
    private PeerSyncScheduler Cycle(TraktAuthService auth, Trakt trakt, Func<string, EmbyClient?> clientFor, Func<bool>? playing = null)
    {
        var watchlist = new WatchlistService(_db, auth);
        var traktQueue = new TraktQueueDrainWorker(_db, auth, Client(trakt));
        return new PeerSyncScheduler(new TraktSyncService(_db, auth, Client(trakt)), auth, playing ?? (() => false),
            async () => { await watchlist.DrainFavouritesAsync(clientFor); await traktQueue.DrainAsync(CancellationToken.None); },
            () => new PlaybackSession(_db).DrainSiblingWritesAsync(clientFor, freshOnly: true));
    }

    // T6 gate (the loop test): a watch marked on trakt.tv lands in Nocturne and on both servers within one cycle,
    // with Trakt's timestamp (D8), and nothing goes back to Trakt: no queued event, and the next cycle is one request.
    [Fact]
    public async Task AWatchMarkedOnTrakt_ReachesBothServersInOneCycle_AndNothingGoesBack()
    {
        ArrivalOnTwoServers(cascadeOn: true);
        SeedConnected("c1", expiresInSec: 30 * 86400);
        var trakt = new Trakt
        {
            Route = path => path switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, Activities),
                "/sync/watched/movies" => Json(HttpStatusCode.OK, ArrivalWatched),
                _ => null
            }
        };
        var auth = new TraktAuthService(_db, Client(trakt));
        var emby = new Emby();
        EmbyClient ClientFor(string sid) { var c = new EmbyClient(emby); c.Configure($"http://{sid}.test", "tok", "u"); return c; }
        var playing = false;
        using var cycle = Cycle(auth, trakt, ClientFor, () => playing);

        Assert.True(await cycle.CycleAsync());

        var watchedAt = new DateTime(2026, 10, 21, 5, 0, 0, DateTimeKind.Utc);
        foreach (var (srv, id) in new[] { ("s1", "arr_a"), ("s2", "arr_b") })
        {
            Assert.True(_db.GetUserData(srv, id)!.Played);
            Assert.Equal(watchedAt, _db.GetUserData(srv, id)!.LastPlayedDate);
            Assert.Equal(Origin.Trakt, _db.GetUserDataOrigin(srv, id));
        }
        Assert.Equal(new[] { "s1.test/emby/Users/u/PlayedItems/arr_a?DatePlayed=20261021050000", "s2.test/emby/Users/u/PlayedItems/arr_b?DatePlayed=20261021050000" },
            emby.Posts.OrderBy(p => p));
        Assert.Empty(_db.GetPendingSiblingWrites(10));
        Assert.Equal(0, _db.GetTraktQueueCount());
        Assert.DoesNotContain(trakt.Requests, r => r.Method != "GET");

        trakt.Requests.Clear();
        Assert.True(await cycle.CycleAsync());
        Assert.Equal("/sync/last_activities", Assert.Single(trakt.Requests).Path);
        Assert.Equal(2, emby.Posts.Count);

        playing = true; // §8.3: paused during playback
        trakt.Requests.Clear();
        Assert.False(await cycle.CycleAsync());
        Assert.Empty(trakt.Requests);
    }

    // A failed watched GET leaves the stored timestamp, so the next cycle fetches it again instead of losing it.
    [Fact]
    public async Task AFailedWatchedPull_IsRetriedNextCycle()
    {
        ArrivalOnTwoServers(cascadeOn: true);
        SeedConnected("c1", expiresInSec: 30 * 86400);
        var down = true;
        var trakt = new Trakt
        {
            Route = path => path switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, Activities),
                "/sync/watched/movies" => down ? Json(HttpStatusCode.BadGateway, "{}") : Json(HttpStatusCode.OK, ArrivalWatched),
                _ => null
            }
        };
        var auth = new TraktAuthService(_db, Client(trakt));
        var sync = new TraktSyncService(_db, auth, Client(trakt));

        await sync.PullWatchedAsync(CancellationToken.None);
        Assert.Null(_db.GetIntegrationState("trakt", "last_movies_watched_at"));
        Assert.Null(_db.GetIntegrationState("trakt", "last_pull_utc"));
        Assert.NotNull(_db.GetIntegrationState("trakt", "last_episodes_watched_at")); // the parts that worked advance

        down = false;
        await sync.PullWatchedAsync(CancellationToken.None);
        Assert.Equal("2026-10-21T05:00:00.000Z", _db.GetIntegrationState("trakt", "last_movies_watched_at"));
        Assert.True(_db.GetUserData("s2", "arr_b")!.Played);
    }

    // T7 gate: a title favourited on Valkyrie and on the Trakt watchlist is one row, and neither pull writes to any peer.
    // Each source removes only its own entry. The library knows Dark by its TVDB id only; Trakt lists TMDB's first.
    [Fact]
    public async Task AFavouriteAndATraktWatchlistEntry_AreOneRow_AndNeitherPeerIsWritten()
    {
        var arrival = new EmbyItem { Id = "arr", ServerId = "valkyrie", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } }, UserData = new() { IsFavorite = true } };
        var dark = new EmbyItem { Id = "dark", ServerId = "valkyrie", Name = "Dark", Type = "Series", ProviderIds = new() { { "Tvdb", "334824" } }, UserData = new() };
        _db.UpsertItems(new List<EmbyItem> { arrival, dark }, "valkyrie");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { arrival, dark }, _db);
        SeedConnected("c1", expiresInSec: 30 * 86400);
        var updated = "2026-10-21T05:00:00.000Z";
        var showsDown = false;
        var movies = "[{\"listed_at\":\"2026-10-20T00:00:00.000Z\",\"movie\":{\"title\":\"Arrival\",\"year\":2016,\"ids\":{\"trakt\":1,\"tmdb\":329865}}}]";
        var trakt = new Trakt
        {
            Route = path => path switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, $"{{\"watchlist\":{{\"updated_at\":\"{updated}\"}}}}"),
                "/sync/watchlist/movies" => Json(HttpStatusCode.OK, movies),
                "/sync/watchlist/shows" => showsDown ? Json(HttpStatusCode.BadGateway, "{}") : Json(HttpStatusCode.OK, "[{\"listed_at\":\"2026-10-19T00:00:00.000Z\",\"show\":{\"title\":\"Dark\",\"year\":2017,\"ids\":{\"trakt\":2,\"tvdb\":334824,\"tmdb\":70523}}}]"),
                _ => null
            }
        };
        var auth = new TraktAuthService(_db, Client(trakt));
        var sync = new TraktSyncService(_db, auth, Client(trakt));

        await sync.PullWatchedAsync(CancellationToken.None);

        var cards = _db.GetMergedWatchlist();
        Assert.Equal(new[] { "Arrival", "Dark" }, cards.Select(c => c.Title).OrderBy(t => t));
        Assert.All(cards, c => Assert.True(c.IsInLibrary));
        Assert.Empty(_db.GetPendingSiblingWrites(10));
        Assert.Equal(0, _db.GetTraktQueueCount());
        Assert.DoesNotContain(trakt.Requests, r => r.Method != "GET");

        (updated, movies, showsDown) = ("2026-10-22T05:00:00.000Z", "[]", true); // removed on trakt.tv
        await sync.PullWatchedAsync(CancellationToken.None); // half a snapshot replaces nothing
        Assert.Contains(_db.GetMergedWatchlist(), c => c.Title == "Dark");
        showsDown = false;
        await sync.PullWatchedAsync(CancellationToken.None); // still Valkyrie's favourite
        Assert.Equal(new[] { "Arrival", "Dark" }, _db.GetMergedWatchlist().Select(c => c.Title).OrderBy(t => t));

        arrival.UserData.IsFavorite = false; // unfavourited on Valkyrie: gone
        _db.UpsertItems(new List<EmbyItem> { arrival }, "valkyrie");
        Assert.Equal("Dark", Assert.Single(_db.GetMergedWatchlist()).Title);
    }

    private EmbyClient EmbyFor(Emby emby, string sid) { var c = new EmbyClient(emby); c.Configure($"http://{sid}.test", "tok", "u"); return c; }

    // T9 gate, add: from a poster while offline, the title is on the watchlist at once; both peers get it on reconnect.
    [Fact]
    public async Task AnAddMadeOffline_ShowsAtOnce_AndReachesBothPeersOnReconnect()
    {
        ArrivalOnTwoServers(cascadeOn: true);
        var gid = _db.GetGroupIdForDedupKey("tmdb:movie:329865")!.Value;
        SeedConnected("c1", expiresInSec: 30 * 86400);
        var offline = true;
        var listed = false;
        HttpResponseMessage? Route(string path)
        {
            if (offline) throw new HttpRequestException("No such host is known.");
            if (path == "/sync/watchlist") listed = true;
            return path switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, $"{{\"watchlist\":{{\"updated_at\":\"{(listed ? "2026-10-22" : "2026-10-21")}T00:00:00.000Z\"}}}}"),
                "/sync/watchlist/movies" => Json(HttpStatusCode.OK, listed ? "[{\"listed_at\":\"2026-10-22T00:00:00.000Z\",\"movie\":{\"title\":\"Arrival\",\"ids\":{\"tmdb\":329865}}}]" : "[]"),
                "/sync/watchlist" => Json(HttpStatusCode.Created, "{}"),
                _ => null
            };
        }
        var trakt = new Trakt { Route = Route };
        var auth = new TraktAuthService(_db, Client(trakt));
        var emby = new Emby { Offline = true };
        using var cycle = Cycle(auth, trakt, sid => EmbyFor(emby, sid));

        await new WatchlistService(_db, auth).AddAsync(gid, CancellationToken.None);

        var card = new List<EmbyItem> { new() { Id = "arr_a", ServerId = "s1", Name = "Arrival", Type = "Movie" } };
        _db.AttachGroupArtwork(card);
        Assert.True(card[0].IsInWatchlist);                                   // every poster of it, at once
        Assert.Equal("Arrival", Assert.Single(_db.GetMergedWatchlist()).Title);

        Assert.True(await cycle.CycleAsync());                                // offline: nothing delivered, nothing lost
        Assert.Equal(2, _db.GetPendingFavouriteWrites().Count);
        Assert.Equal(1, _db.GetTraktQueueCount());
        Assert.Single(_db.GetMergedWatchlist());

        (offline, emby.Offline) = (false, false);                             // reconnect
        Assert.True(await cycle.CycleAsync());
        Assert.Equal(new[] { "s1.test/emby/Users/u/FavoriteItems/arr_a", "s2.test/emby/Users/u/FavoriteItems/arr_b" }, emby.Posts.OrderBy(p => p));
        var sent = JsonDocument.Parse(trakt.Requests.Last(r => r.Path == "/sync/watchlist").Body).RootElement.GetProperty("movies")[0];
        Assert.Equal(329865, sent.GetProperty("ids").GetProperty("tmdb").GetInt32());
        Assert.False(sent.TryGetProperty("year", out _));                     // no year known: none sent (it was serialized as {})
        Assert.Empty(_db.GetPendingFavouriteWrites());
        Assert.Equal(0, _db.GetTraktQueueCount());
        Assert.True(_db.IsInWatchlist(gid));                                  // the pull settled Trakt's entry, still listed
    }

    // T9 gate, remove: gone at once, and neither a Trakt pull nor a server sync landing before the remove is delivered
    // brings it back; once delivered, both peers have dropped it and nothing is left pending.
    [Fact]
    public async Task ARemove_NeverFlickersBack_WhenAPullLandsMidFlight()
    {
        ArrivalOnTwoServers(cascadeOn: true);
        var gid = _db.GetGroupIdForDedupKey("tmdb:movie:329865")!.Value;
        var onValkyrie = new EmbyItem { Id = "arr_a", ServerId = "s1", Name = "Arrival", Type = "Movie", ProviderIds = new() { { "Tmdb", "329865" } }, UserData = new() { IsFavorite = true } };
        _db.UpsertItems(new List<EmbyItem> { onValkyrie }, "s1");                // a favourite on s1
        SeedConnected("c1", expiresInSec: 30 * 86400);
        var (removed, version) = (false, 1);
        HttpResponseMessage? Route(string path)
        {
            if (path == "/sync/watchlist/remove") (removed, version) = (true, version + 1);
            return path switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, $"{{\"watchlist\":{{\"updated_at\":\"2026-10-2{version}T00:00:00.000Z\"}}}}"),
                "/sync/watchlist/movies" => Json(HttpStatusCode.OK, removed ? "[]" : "[{\"listed_at\":\"2026-10-20T00:00:00.000Z\",\"movie\":{\"title\":\"Arrival\",\"ids\":{\"tmdb\":329865}}}]"),
                "/sync/watchlist/remove" => Json(HttpStatusCode.OK, "{}"),
                _ => null
            };
        }
        var trakt = new Trakt { Route = Route };
        var auth = new TraktAuthService(_db, Client(trakt));
        var sync = new TraktSyncService(_db, auth, Client(trakt));
        await sync.PullWatchedAsync(CancellationToken.None);                  // and on the Trakt watchlist
        Assert.Single(_db.GetMergedWatchlist());

        await new WatchlistService(_db, auth).RemoveAsync(gid, CancellationToken.None);
        Assert.Empty(_db.GetMergedWatchlist());

        version++;                                                            // a pull lands mid-flight: Trakt still lists it
        await sync.PullWatchedAsync(CancellationToken.None);
        _db.UpsertItems(new List<EmbyItem> { onValkyrie }, "s1");                // and so does s1's sync
        Assert.Empty(_db.GetMergedWatchlist());
        Assert.False(_db.IsInWatchlist(gid));

        var emby = new Emby();
        using var cycle = Cycle(auth, trakt, sid => EmbyFor(emby, sid));
        Assert.True(await cycle.CycleAsync());                                // delivered, then pulled
        Assert.Equal(new[] { "s1.test/emby/Users/u/FavoriteItems/arr_a", "s2.test/emby/Users/u/FavoriteItems/arr_b" }, emby.Deletes.OrderBy(p => p));
        Assert.Contains(trakt.Requests, r => r.Path == "/sync/watchlist/remove");
        onValkyrie.UserData.IsFavorite = false;
        _db.UpsertItems(new List<EmbyItem> { onValkyrie }, "s1");
        Assert.Empty(_db.GetMergedWatchlist());
        Assert.Empty(_db.GetPendingFavouriteWrites());
        Assert.Equal(0, _db.GetTraktQueueCount());
    }

    // A toggle made while its favourite write is on the wire stays pending: the delivered add does not settle a remove.
    [Fact]
    public async Task AToggleMadeWhileAFavouriteIsInFlight_IsNotLost()
    {
        ArrivalOnTwoServers(cascadeOn: true);
        var gid = _db.GetGroupIdForDedupKey("tmdb:movie:329865")!.Value;
        var watchlist = new WatchlistService(_db, new TraktAuthService(_db));
        await watchlist.AddAsync(gid, CancellationToken.None);
        var emby = new Emby();
        emby.OnSend = () => { emby.OnSend = null; watchlist.RemoveAsync(gid, CancellationToken.None).Wait(); };

        await watchlist.DrainFavouritesAsync(sid => EmbyFor(emby, sid));

        Assert.False(_db.IsInWatchlist(gid));
        Assert.Contains(_db.GetPendingFavouriteWrites(), w => w.Op == "remove");
    }

    // A title outside the library is removed through Trakt too; deleting it locally only, the next pull brought it back.
    [Fact]
    public async Task RemovingATitleOutsideTheLibrary_GoesToTrakt_AndStaysGone()
    {
        SeedConnected("c1", expiresInSec: 30 * 86400);
        var trakt = new Trakt
        {
            Route = path => path switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, "{\"watchlist\":{\"updated_at\":\"2026-10-21T00:00:00.000Z\"}}"),
                "/sync/watchlist/shows" => Json(HttpStatusCode.OK, "[{\"listed_at\":\"2026-10-20T00:00:00.000Z\",\"show\":{\"title\":\"Severance\",\"year\":2022,\"ids\":{\"tmdb\":95396}}}]"),
                _ => null
            }
        };
        var auth = new TraktAuthService(_db, Client(trakt));
        await new TraktSyncService(_db, auth, Client(trakt)).PullWatchedAsync(CancellationToken.None);
        var card = Assert.Single(_db.GetMergedWatchlist());
        Assert.Equal(("Severance", false, "tmdb:series:95396"), (card.Title, card.IsInLibrary, card.Ref));

        await new WatchlistService(_db, auth).RemoveUnownedAsync(card.Ref, CancellationToken.None);
        _db.ReplaceTraktWatchlist(new List<(string, string, long, string?, int?, string?)> { (card.Ref, "Series", 1, "Severance", 2022, null) }); // a pull mid-flight

        Assert.Empty(_db.GetMergedWatchlist());
        var queued = Assert.Single(_db.GetPendingTraktEvents(10));
        Assert.Equal("watchlist_remove", queued.Action);
        Assert.Equal(95396, JsonDocument.Parse(queued.Payload!).RootElement.GetProperty("shows")[0].GetProperty("ids").GetProperty("tmdb").GetInt32());
    }

    // §8.2 step 3: a later local play, finished or not, is newer than Trakt's watch and is kept.
    [Fact]
    public async Task ALaterPartialPlay_IsNotOverwrittenByAnOlderTraktWatch()
    {
        ArrivalOnTwoServers(cascadeOn: true);
        SeedConnected("c1", expiresInSec: 30 * 86400);
        var later = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        _db.UpdateUserDataState("s1", "arr_a", positionTicks: 600 * 10_000_000L, runtimeTicks: null, played: false, lastPlayedUtc: later, origin: Origin.Local);
        var trakt = new Trakt
        {
            Route = path => path switch
            {
                "/sync/last_activities" => Json(HttpStatusCode.OK, Activities),
                "/sync/watched/movies" => Json(HttpStatusCode.OK, ArrivalWatched),
                _ => null
            }
        };
        var auth = new TraktAuthService(_db, Client(trakt));

        await new TraktSyncService(_db, auth, Client(trakt)).PullWatchedAsync(CancellationToken.None);

        Assert.False(_db.GetUserData("s1", "arr_a")!.Played);
        Assert.Equal(Origin.Local, _db.GetUserDataOrigin("s1", "arr_a"));
        Assert.True(_db.GetUserData("s2", "arr_b")!.Played);
        Assert.Equal(new[] { "s2" }, _db.GetPendingSiblingWrites(10).Select(w => w.ServerId));
    }
}
