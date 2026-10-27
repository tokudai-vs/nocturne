using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;
using Xunit;

namespace Nocturne.Tests;

// #28: one type produces and parses every provider key. Three silent no-ops came from one side writing a key string
// the other side never read (Trakt watchlist shows, TMDB episode keys, an imdb key form nothing wrote).
public class ProviderKeyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
    private readonly DatabaseService _db;

    public ProviderKeyTests() => _db = new DatabaseService(_dbPath);

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static ProviderKey RandomKey(Random rng)
    {
        var provider = ProviderKey.Providers[rng.Next(3)];
        var id = provider == Provider.Imdb ? $"tt{rng.Next(1, 99_999_999):D7}" : rng.Next(1, int.MaxValue).ToString();
        return (rng.Next(4) switch
        {
            0 => ProviderKey.Movie(provider, id),
            1 => ProviderKey.Series(provider, id),
            2 => ProviderKey.Episode(provider, id),
            _ => ProviderKey.SeasonEpisode(provider, id, rng.Next(0, 150), rng.Next(0, 2500)), // 3- and 4-digit episodes
        })!.Value;
    }

    // Round trip over every form: identity form always; TMDB's form wherever TMDB can look the key up.
    [Fact]
    public void EveryKey_RoundTrips_ThroughBothForms()
    {
        var rng = new Random(28);
        for (var i = 0; i < 5000; i++)
        {
            var key = RandomKey(rng);
            Assert.True(ProviderKey.TryParse(key.ToString(), out var back), key.ToString());
            Assert.Equal(key, back);
            Assert.StartsWith(key.Slot + ":", key.ToString());

            var tmdbForm = key.Provider == Provider.Tmdb && key.Kind != KeyKind.Episode;
            Assert.Equal(tmdbForm, key.TmdbKey != null);
            if (!tmdbForm) continue;
            Assert.True(ProviderKey.TryParseTmdbKey(key.TmdbKey, out var fromTmdb), key.TmdbKey);
            Assert.Equal(key, fromTmdb);
            Assert.NotNull(key.TmdbPath);
        }
    }

    [Theory]
    [InlineData("tmdb:tv:1396")]                 // TMDB's form is not an identity key...
    [InlineData("tmdb:tv:1396:s01e01")]
    [InlineData("imdb:tt1375666")]               // the form TraktScrobbleTests used; nothing writes it
    [InlineData("tmdb:movie:../tv/222031")]      // malformed ids never become keys
    [InlineData("tmdb:movie:tt1375666")]
    [InlineData("imdb:movie:1375666")]
    [InlineData("ep:tvdb:81189:s1")]
    [InlineData("ep:series:abc123:s01e01")]      // fuzzy keys are not provider keys
    [InlineData("ep:name:breaking bad:s01e01")]
    [InlineData("name:movie:arrival:2016")]
    [InlineData("item:srv:id")]
    [InlineData("")]
    public void NonKeys_DoNotParse(string s) => Assert.False(ProviderKey.TryParse(s, out _));

    [Theory]
    [InlineData("tmdb:series:1396")]             // ...and an identity key is not TMDB's form
    [InlineData("ep:tmdb:1396:s01e01")]
    [InlineData("tmdb:ep:62085")]
    [InlineData("tvdb:movie:123")]
    public void NonTmdbKeys_DoNotParseAsTmdbKeys(string s) => Assert.False(ProviderKey.TryParseTmdbKey(s, out _));

    // The SQL prefilter (DatabaseService's conflict pairs) keeps every provider key and drops the fuzzy ones.
    [Fact]
    public void TheSqlPrefilter_AgreesWithTryParse()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        using (var create = c.CreateCommand()) { create.CommandText = "CREATE TABLE k (key TEXT);"; create.ExecuteNonQuery(); }
        var rng = new Random(7);
        var keys = Enumerable.Range(0, 500).Select(_ => RandomKey(rng).ToString())
            .Concat(new[] { "ep:series:abc:s01e01", "ep:name:x:s01e01", "name:movie:x:2016", "item:a:b" }).ToList();
        foreach (var k in keys)
        {
            using var insert = c.CreateCommand();
            insert.CommandText = "INSERT INTO k VALUES ($k);";
            insert.Parameters.AddWithValue("$k", k);
            insert.ExecuteNonQuery();
        }
        using var select = c.CreateCommand();
        select.CommandText = $"SELECT key FROM k WHERE {ProviderKey.SqlIsProviderKey("key")};";
        var kept = new HashSet<string>();
        using (var r = select.ExecuteReader()) while (r.Read()) kept.Add(r.GetString(0));

        Assert.Equal(keys.Where(k => ProviderKey.TryParse(k, out _)).ToHashSet(), kept);
    }

    private sealed class Http : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Requests = new();
        public Func<string, HttpResponseMessage?> Route { get; set; } = _ => null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return Route(request.RequestUri!.AbsolutePath) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Each consumer reads what the producer (the dedup index) writes, through its own code path: the scrobble payload,
    // the Trakt watched pull and watchlist pull, the watchlist write, TMDB enrichment and the artwork it feeds.
    [Fact]
    public async Task EveryConsumer_ReadsWhatTheDedupIndexWrites()
    {
        var movie = new EmbyItem { Id = "arr", ServerId = "a", Name = "Arrival", Type = "Movie", ProductionYear = 2016, ProviderIds = new() { ["Tmdb"] = "329865", ["Imdb"] = "tt2543164", ["Tvdb"] = "12345" } };
        var show = new EmbyItem { Id = "bb", ServerId = "a", Name = "Breaking Bad", Type = "Series", ProductionYear = 2008, ProviderIds = new() { ["Tmdb"] = "1396", ["Imdb"] = "tt0903747", ["Tvdb"] = "81189" } };
        var pilot = new EmbyItem
        {
            Id = "bb101", ServerId = "a", Name = "Pilot", Type = "Episode", SeriesId = "bb", SeriesName = "Breaking Bad", ParentIndexNumber = 1, IndexNumber = 1,
            ProviderIds = new() { ["Tmdb"] = "62085", ["Imdb"] = "tt0959621", ["Tvdb"] = "349232" },
            SeriesProviderIds = new() { ["Tmdb"] = "1396", ["Imdb"] = "tt0903747", ["Tvdb"] = "81189" },
        };
        foreach (var it in new[] { movie, show, pilot }) _db.UpsertItem(it, "a", "lib");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { movie, show, pilot }, _db);
        int Group(string key) => _db.GetGroupIdForDedupKey(key)!.Value;
        var (movieGroup, showGroup, pilotGroup) = (Group("tmdb:movie:329865"), Group("tvdb:series:81189"), Group("ep:tvdb:81189:s01e01"));

        // Trakt connected with a custom client id (no network: every request goes to the fakes below).
        _db.SetIntegrationState("trakt", "mode", "Custom");
        _db.SetIntegrationState("trakt", "custom_client_id", "c1");
        _db.SetIntegrationState("trakt", "client_id_used", "c1");
        _db.SetIntegrationState("trakt", "access_token_dpapi", Convert.ToHexString(CredentialProtection.ProtectString("tok")));
        _db.SetIntegrationState("trakt", "expires_utc", DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds().ToString());
        var trakt = new Http();
        HttpClient Trakt() => new(trakt) { BaseAddress = new Uri("https://api.trakt.tv") };
        var auth = new TraktAuthService(_db, Trakt());

        // 1. Scrobble payload: every id the title has, the episode by its show's ids and position.
        var scrobbler = new TraktScrobbleService(_db, auth);
        var moviePayload = JsonDocument.Parse(scrobbler.BuildScrobblePayload(_db.GetDedupGroup(movieGroup)!, 50)!).RootElement.GetProperty("movie").GetProperty("ids");
        Assert.Equal((329865, "tt2543164", 12345), (moviePayload.GetProperty("tmdb").GetInt32(), moviePayload.GetProperty("imdb").GetString(), moviePayload.GetProperty("tvdb").GetInt32()));
        var episodePayload = JsonDocument.Parse(scrobbler.BuildScrobblePayload(_db.GetDedupGroup(pilotGroup)!, 50)!).RootElement;
        var showIds = episodePayload.GetProperty("show").GetProperty("ids");
        Assert.Equal((1396, "tt0903747", 81189), (showIds.GetProperty("tmdb").GetInt32(), showIds.GetProperty("imdb").GetString(), showIds.GetProperty("tvdb").GetInt32()));
        Assert.Equal((1, 1), (episodePayload.GetProperty("episode").GetProperty("season").GetInt32(), episodePayload.GetProperty("episode").GetProperty("number").GetInt32()));
        Assert.Equal(62085, episodePayload.GetProperty("episode").GetProperty("ids").GetProperty("tmdb").GetInt32());

        // 2. Trakt pulls, each title named by one id only: the movie by IMDb, the episode by its show's TVDB id, the
        //    watchlist show by IMDb.
        trakt.Route = path => path switch
        {
            "/sync/last_activities" => Json("{\"movies\":{\"watched_at\":\"2026-10-21T00:00:00.000Z\"},\"episodes\":{\"watched_at\":\"2026-10-21T00:00:00.000Z\"},\"watchlist\":{\"updated_at\":\"2026-10-21T00:00:00.000Z\"}}"),
            "/sync/watched/movies" => Json("[{\"last_watched_at\":\"2026-10-20T00:00:00.000Z\",\"movie\":{\"title\":\"Arrival\",\"ids\":{\"imdb\":\"tt2543164\"}}}]"),
            "/sync/watched/shows" => Json("[{\"show\":{\"title\":\"Breaking Bad\",\"ids\":{\"tvdb\":81189}},\"seasons\":[{\"number\":1,\"episodes\":[{\"number\":1,\"last_watched_at\":\"2026-10-20T00:00:00.000Z\"}]}]}]"),
            "/sync/watchlist/shows" => Json("[{\"listed_at\":\"2026-10-20T00:00:00.000Z\",\"show\":{\"title\":\"Breaking Bad\",\"ids\":{\"imdb\":\"tt0903747\"}}}]"),
            _ => Json("[]"),
        };
        await new TraktSyncService(_db, auth, Trakt()).PullWatchedAsync(CancellationToken.None);
        Assert.True(_db.GetUserData("a", "arr")!.Played);
        Assert.True(_db.GetUserData("a", "bb101")!.Played);
        var listed = Assert.Single(_db.GetMergedWatchlist());
        Assert.Equal((showGroup, true), ((int)listed.GroupId, listed.IsInLibrary));

        // 3. The watchlist write: Trakt's entry is keyed by one of the group's own keys, the payload carries its ids.
        await new WatchlistService(_db, auth).AddAsync(movieGroup, CancellationToken.None);
        var added = JsonDocument.Parse(_db.GetPendingTraktEvents(10).Single(e => e.Action == "watchlist_add").Payload!).RootElement.GetProperty("movies")[0].GetProperty("ids");
        Assert.Equal((329865, "tt2543164", 12345), (added.GetProperty("tmdb").GetInt32(), added.GetProperty("imdb").GetString(), added.GetProperty("tvdb").GetInt32()));
        Assert.Contains(_db.GetAuthoritativeKeys(movieGroup), k => _db.IsInWatchlist(0, k)); // a Trakt row keyed by one of the group's own keys

        // 4. TMDB: none of the three has art on any server, so each is fetched at the path its tmdb_key names, and
        //    what is stored under that key reaches the cards.
        var tmdb = new Http
        {
            Route = path => path switch
            {
                "/3/movie/329865" => Json("{\"poster_path\":\"/arrival.jpg\"}"),
                "/3/tv/1396" => Json("{\"poster_path\":\"/bb.jpg\"}"),
                "/3/tv/1396/season/1" => Json("{\"episodes\":[{\"episode_number\":1,\"still_path\":\"/pilot.jpg\"}]}"),
                _ => null,
            }
        };
        var enrichment = new TmdbEnrichmentService(_db, new HttpClient(tmdb) { BaseAddress = new Uri("https://api.themoviedb.org") });
        enrichment.SetCustomApiKey("k");
        await enrichment.RunAsync(new Progress<EnrichProgress>(), CancellationToken.None);
        Assert.Equal(new[] { "/3/movie/329865", "/3/tv/1396", "/3/tv/1396/season/1" }, tmdb.Requests.Select(r => r.Path).OrderBy(p => p));
        var cards = new List<EmbyItem> { new() { Id = "arr", ServerId = "a", Type = "Movie" }, new() { Id = "bb", ServerId = "a", Type = "Series" }, new() { Id = "bb101", ServerId = "a", Type = "Episode" } };
        _db.AttachGroupArtwork(cards);
        Assert.Equal(new[] { "/arrival.jpg", "/bb.jpg", "/pilot.jpg" }, cards.Select(c => c.TmdbPosterPath));
    }

    // The producer no longer files an episode's own TMDB id as a movie; a group that already holds one is cleared on open.
    [Fact]
    public void AnEpisodesOwnTmdbId_IsNeverItsTmdbKey()
    {
        var orphan = new EmbyItem { Id = "e", ServerId = "a", Name = "Pilot", Type = "Episode", ParentIndexNumber = 1, IndexNumber = 1, ProviderIds = new() { ["Tmdb"] = "62085" } };
        Assert.Null(ConcurrentDedupIndex.ExtractTmdbKey(orphan));

        _db.UpsertItem(orphan, "a", "lib");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { orphan }, _db);
        _db.ExecuteSql("UPDATE dedup_groups SET tmdb_key = 'tmdb:movie:62085'; DELETE FROM migration_state WHERE key = 'drop_episode_movie_tmdb_keys';");
        _db.Dispose();
        using var reopened = new DatabaseService(_dbPath);
        Assert.Empty(reopened.GetGroupsNeedingTmdbArt(10));
        Assert.Equal("1", reopened.GetMigrationState(DatabaseService.EpisodeMovieKeyRepairKey));
    }
}
