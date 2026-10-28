using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class TraktSyncService : ITraktSyncService
{
    private const string TraktApiBase = "https://api.trakt.tv";
    private readonly DatabaseService _db;
    private readonly ITraktAuthService _auth;
    private readonly HttpClient _http;
    private DateTime? _lastPullTime;

    public DateTime? LastPullTime => _lastPullTime;

    public TraktSyncService(DatabaseService db, ITraktAuthService auth, HttpClient? httpClient = null)
    {
        _db = db;
        _auth = auth;
        _http = (httpClient ?? new HttpClient { BaseAddress = new Uri(TraktApiBase), Timeout = TimeSpan.FromSeconds(30) }).Identify();
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("trakt-api-version", "2");
    }

    public async Task PullWatchedAsync(CancellationToken ct)
    {
        if (_auth.State != TraktConnectionState.Connected)
            return;

        var token = _auth.GetAccessToken();
        var clientId = _auth.GetActiveClientId();
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(clientId))
            return;

        try
        {
            // 1. Cheap change detection (§8.1)
            var activities = await FetchLastActivitiesAsync(token, clientId, ct);
            if (activities == null) return;

            var storedMovieWatched = _db.GetIntegrationState("trakt", "last_movies_watched_at") ?? "";
            var storedEpWatched = _db.GetIntegrationState("trakt", "last_episodes_watched_at") ?? "";
            var storedWatchlistUpdated = _db.GetIntegrationState("trakt", "last_watchlist_updated_at") ?? "";

            bool needMovies = string.IsNullOrEmpty(storedMovieWatched) ||
                              string.Compare(activities.Movies.WatchedAt, storedMovieWatched, StringComparison.Ordinal) > 0;
            bool needEpisodes = string.IsNullOrEmpty(storedEpWatched) ||
                                string.Compare(activities.Episodes.WatchedAt, storedEpWatched, StringComparison.Ordinal) > 0;
            bool needWatchlist = string.IsNullOrEmpty(storedWatchlistUpdated) ||
                                 string.Compare(activities.Watchlist.UpdatedAt, storedWatchlistUpdated, StringComparison.Ordinal) > 0;

            // A stored timestamp advances only when its pull succeeded; a failed GET is retried next cycle.
            var ok = true;
            if (needMovies)
            {
                NocturneLogger.Info("TraktSyncService", "Pulling updated watched movies from Trakt...");
                if (await PullWatchedMoviesInternalAsync(token, clientId, ct)) Advance("last_movies_watched_at", activities.Movies.WatchedAt);
                else ok = false;
            }

            if (needEpisodes)
            {
                NocturneLogger.Info("TraktSyncService", "Pulling updated watched episodes from Trakt...");
                if (await PullWatchedShowsInternalAsync(token, clientId, ct)) Advance("last_episodes_watched_at", activities.Episodes.WatchedAt);
                else ok = false;
            }

            if (needWatchlist)
            {
                NocturneLogger.Info("TraktSyncService", "Pulling updated watchlist from Trakt...");
                if (await PullWatchlistInternalAsync(token, clientId, ct)) Advance("last_watchlist_updated_at", activities.Watchlist.UpdatedAt);
                else ok = false;
            }

            if (!ok) return;
            _lastPullTime = DateTime.UtcNow;
            _db.SetIntegrationState("trakt", "last_pull_utc", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("TraktSyncService", "Exception during PullWatchedAsync", ex);
        }
    }

    public async Task PullWatchlistAsync(CancellationToken ct)
    {
        if (_auth.State != TraktConnectionState.Connected)
            return;

        var token = _auth.GetAccessToken();
        var clientId = _auth.GetActiveClientId();
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(clientId))
            return;

        try
        {
            await PullWatchlistInternalAsync(token, clientId, ct);
            _lastPullTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("TraktSyncService", "Exception during PullWatchlistAsync", ex);
        }
    }

    public async Task FullReconcileAsync(IProgress<int> progress, CancellationToken ct)
    {
        if (_auth.State != TraktConnectionState.Connected)
            return;

        var token = _auth.GetAccessToken();
        var clientId = _auth.GetActiveClientId();
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(clientId))
            return;

        NocturneLogger.Info("TraktSyncService", "Starting full Trakt reconcile...");
        progress.Report(10);

        await PullWatchedMoviesInternalAsync(token, clientId, ct);
        progress.Report(45);

        await PullWatchedShowsInternalAsync(token, clientId, ct);
        progress.Report(80);

        await PullWatchlistInternalAsync(token, clientId, ct);
        progress.Report(100);

        _lastPullTime = DateTime.UtcNow;
        NocturneLogger.Info("TraktSyncService", "Full Trakt reconcile complete.");
    }

    private void Advance(string key, string value)
    {
        if (!string.IsNullOrEmpty(value)) _db.SetIntegrationState("trakt", key, value);
    }

    private async Task<TraktLastActivitiesDto?> FetchLastActivitiesAsync(string token, string clientId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/sync/last_activities");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("trakt-api-key", clientId);

        using var resp = await _http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
        {
            return await resp.Content.ReadFromJsonAsync<TraktLastActivitiesDto>(cancellationToken: ct);
        }
        Fail("last_activities", resp);
        return null;
    }

    private async Task<bool> PullWatchedMoviesInternalAsync(string token, string clientId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/sync/watched/movies?extended=noseasons");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("trakt-api-key", clientId);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Fail("watched movies", resp);

        var movies = await resp.Content.ReadFromJsonAsync<List<TraktWatchedMovieDto>>(cancellationToken: ct);
        if (movies == null) return false;
        int inLibrary = 0, marked = 0;
        _loggedMarks = 0;

        foreach (var m in movies)
        {
            if (ct.IsCancellationRequested) break;
            if (m.Movie?.Ids == null || string.IsNullOrEmpty(m.LastWatchedAt)) continue;

            long watchedAtSec = DateTimeOffset.TryParse(m.LastWatchedAt, out var dt)
                ? dt.ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var gid = GroupOf(TitleKeys(KeyKind.Movie, m.Movie.Ids));
            if (!gid.HasValue) continue; // Not in library (§8.2 step 2)

            inLibrary++;
            marked += ApplyWatchedStateToGroup(gid.Value, watchedAtSec);
        }
        NocturneLogger.Info("TraktSyncService", $"Trakt watched movies: {movies.Count} listed, {inLibrary} in the library, {marked} version(s) marked.");
        return !ct.IsCancellationRequested;
    }

    private async Task<bool> PullWatchedShowsInternalAsync(string token, string clientId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/sync/watched/shows");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("trakt-api-key", clientId);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Fail("watched shows", resp);

        var shows = await resp.Content.ReadFromJsonAsync<List<TraktWatchedShowDto>>(cancellationToken: ct);
        if (shows == null) return false;
        int inLibrary = 0, marked = 0;
        _loggedMarks = 0;

        foreach (var show in shows)
        {
            if (ct.IsCancellationRequested) break;
            if (show.Show?.Ids == null || show.Seasons == null) continue;

            foreach (var s in show.Seasons)
            {
                if (s.Episodes == null) continue;
                foreach (var ep in s.Episodes)
                {
                    if (string.IsNullOrEmpty(ep.LastWatchedAt)) continue;

                    long epWatchedAtSec = DateTimeOffset.TryParse(ep.LastWatchedAt, out var dt)
                        ? dt.ToUnixTimeSeconds()
                        : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    var ids = show.Show.Ids;
                    var epGid = GroupOf(new[]
                    {
                        ProviderKey.SeasonEpisode(Provider.Tvdb, ids.Tvdb?.ToString(), s.Number, ep.Number),
                        ProviderKey.SeasonEpisode(Provider.Tmdb, ids.Tmdb?.ToString(), s.Number, ep.Number),
                        ProviderKey.SeasonEpisode(Provider.Imdb, ids.Imdb, s.Number, ep.Number),
                    });
                    if (!epGid.HasValue) continue;

                    inLibrary++;
                    marked += ApplyWatchedStateToGroup(epGid.Value, epWatchedAtSec);
                }
            }
        }
        NocturneLogger.Info("TraktSyncService", $"Trakt watched episodes: {inLibrary} in the library, {marked} version(s) marked.");
        return !ct.IsCancellationRequested;
    }

    private async Task<bool> PullWatchlistInternalAsync(string token, string clientId, CancellationToken ct)
    {
        var pulled = new List<(string Ref, string ItemType, long AddedUtc, string? Title, int? Year, string? TmdbKey)>();

        using (var reqM = new HttpRequestMessage(HttpMethod.Get, "/sync/watchlist/movies"))
        {
            reqM.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            reqM.Headers.Add("trakt-api-key", clientId);
            using var respM = await _http.SendAsync(reqM, ct);
            if (!respM.IsSuccessStatusCode) return Fail("watchlist movies", respM);
            var movies = await respM.Content.ReadFromJsonAsync<List<TraktWatchlistMovieDto>>(cancellationToken: ct);
            if (movies == null) return false;
            foreach (var m in movies)
            {
                if (m.Movie?.Ids == null || WatchlistRef(KeyKind.Movie, m.Movie.Ids) is not { } key) continue;
                pulled.Add((key, "Movie", ListedAt(m.ListedAt), m.Movie.Title, m.Movie.Year, ProviderKey.Movie(Provider.Tmdb, m.Movie.Ids.Tmdb?.ToString())?.TmdbKey));
            }
        }

        using (var reqS = new HttpRequestMessage(HttpMethod.Get, "/sync/watchlist/shows"))
        {
            reqS.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            reqS.Headers.Add("trakt-api-key", clientId);
            using var respS = await _http.SendAsync(reqS, ct);
            if (!respS.IsSuccessStatusCode) return Fail("watchlist shows", respS);
            var shows = await respS.Content.ReadFromJsonAsync<List<TraktWatchlistShowDto>>(cancellationToken: ct);
            if (shows == null) return false;
            foreach (var s in shows)
            {
                if (s.Show?.Ids == null || WatchlistRef(KeyKind.Series, s.Show.Ids) is not { } key) continue;
                pulled.Add((key, "Series", ListedAt(s.ListedAt), s.Show.Title, s.Show.Year, ProviderKey.Series(Provider.Tmdb, s.Show.Ids.Tmdb?.ToString())?.TmdbKey));
            }
        }

        // Both lists or neither: replacing from half a snapshot would drop the other half.
        if (ct.IsCancellationRequested) return false;
        _db.ReplaceTraktWatchlist(pulled);
        return true;
    }

    // A watchlist entry's ref is the dedup key the library knows the title by, whichever of Trakt's ids that is, so
    // the rollup finds its group with one join (§9.2). A title not in the library keeps its first id.
    private string? WatchlistRef(KeyKind kind, TraktIdsDto ids)
    {
        var keys = TitleKeys(kind, ids).OfType<ProviderKey>().ToList();
        foreach (var k in keys)
            if (_db.GetGroupIdForDedupKey(k.ToString()).HasValue) return k.ToString();
        return keys.Count > 0 ? keys[0].ToString() : null;
    }

    // A movie's or show's keys from Trakt's ids, in the order the watchlist and the watched pull try them.
    private static ProviderKey?[] TitleKeys(KeyKind kind, TraktIdsDto ids)
    {
        ProviderKey? Key(Provider p, string? id) => kind == KeyKind.Series ? ProviderKey.Series(p, id) : ProviderKey.Movie(p, id);
        return new[] { Key(Provider.Tmdb, ids.Tmdb?.ToString()), Key(Provider.Imdb, ids.Imdb), Key(Provider.Tvdb, ids.Tvdb?.ToString()) };
    }

    // The group of the first key the library holds.
    private int? GroupOf(IEnumerable<ProviderKey?> keys)
    {
        foreach (var k in keys)
            if (k is { } key && _db.GetGroupIdForDedupKey(key.ToString()) is { } gid) return gid;
        return null;
    }

    private static long ListedAt(string listedAt) =>
        DateTimeOffset.TryParse(listedAt, out var d) ? d.ToUnixTimeSeconds() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static bool Fail(string what, HttpResponseMessage resp)
    {
        NocturneLogger.Warn("TraktSyncService", $"Trakt {what} pull failed: HTTP {(int)resp.StatusCode}");
        return false;
    }

    // Returns how many versions it marked. Each marking is logged (up to MaxLoggedMarks a pull): a later Emby sync may
    // overwrite the row's origin, and live verification needs to see what the pull itself did (#29).
    private const int MaxLoggedMarks = 25;
    private int _loggedMarks;

    private int ApplyWatchedStateToGroup(int groupId, long watchedAtSec)
    {
        // Group-wide marking is the sibling cascade: until the groups were built by the conflict-guarded engine
        // (FeatureFlags.SiblingCascade) only the primary version is marked, as an over-merged group would mark
        // unrelated titles watched.
        var cascade = FeatureFlags.SiblingCascade(_db);
        IEnumerable<(string ServerId, string ItemId)> items = _db.GetGroupItemIds(groupId);
        if (!cascade)
        {
            var group = _db.GetDedupGroup(groupId);
            items = group == null ? Array.Empty<(string, string)>() : new[] { (group.PrimaryServerId, group.PrimaryItemId) };
        }
        var marked = new List<(string, string)>();
        foreach (var (srv, id) in items)
        {
            var existing = _db.GetUserData(srv, id);
            // Idempotence (§8.2 step 3): anything at or after Trakt's watch, played or a later partial play, wins.
            if (existing?.LastPlayedDate != null)
            {
                long existingSec = ((DateTimeOffset)existing.LastPlayedDate.Value).ToUnixTimeSeconds();
                if (watchedAtSec <= existingSec)
                    continue;
            }

            // Upsert user_data with origin = 'trakt' and source timestamp (D8)
            _db.UpdateUserDataState(srv, id, positionTicks: 0, runtimeTicks: null, played: true, lastPlayedUtc: watchedAtSec, origin: "trakt");

            // Enqueue touched version to sibling_write_queue (safe under D7 because origin is 'trakt')
            _db.EnqueueSiblingWrite(srv, id, positionTicks: 0, markPlayed: true, Origin.Trakt);
            marked.Add((srv, id));
        }
        if (marked.Count > 0 && _loggedMarks++ < MaxLoggedMarks)
            NocturneLogger.Info("TraktSyncService", $"Trakt watch applied: group {groupId} '{_db.GetDedupGroup(groupId)?.Name}', " +
                $"watched {DateTimeOffset.FromUnixTimeSeconds(watchedAtSec):u}, cascade {(cascade ? "on" : "off")}: marked {string.Join(", ", marked.Select(m => $"{m.Item1[..Math.Min(8, m.Item1.Length)]}/{m.Item2}"))}");
        return marked.Count;
    }

    // DTOs for Trakt Sync API
    private class TraktLastActivitiesDto
    {
        [JsonPropertyName("movies")] public TraktMovieActivitiesDto Movies { get; set; } = new();
        [JsonPropertyName("episodes")] public TraktEpisodeActivitiesDto Episodes { get; set; } = new();
        [JsonPropertyName("watchlist")] public TraktWatchlistActivitiesDto Watchlist { get; set; } = new();
    }

    private class TraktMovieActivitiesDto
    {
        [JsonPropertyName("watched_at")] public string WatchedAt { get; set; } = string.Empty;
    }

    private class TraktEpisodeActivitiesDto
    {
        [JsonPropertyName("watched_at")] public string WatchedAt { get; set; } = string.Empty;
    }

    private class TraktWatchlistActivitiesDto
    {
        [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = string.Empty;
    }

    private class TraktWatchedMovieDto
    {
        [JsonPropertyName("last_watched_at")] public string LastWatchedAt { get; set; } = string.Empty;
        [JsonPropertyName("movie")] public TraktMovieItemDto? Movie { get; set; }
    }

    private class TraktMovieItemDto
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("year")] public int? Year { get; set; }
        [JsonPropertyName("ids")] public TraktIdsDto? Ids { get; set; }
    }

    private class TraktWatchedShowDto
    {
        [JsonPropertyName("show")] public TraktShowItemDto? Show { get; set; }
        [JsonPropertyName("seasons")] public List<TraktSeasonDto>? Seasons { get; set; }
    }

    private class TraktShowItemDto
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("year")] public int? Year { get; set; }
        [JsonPropertyName("ids")] public TraktIdsDto? Ids { get; set; }
    }

    private class TraktSeasonDto
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("episodes")] public List<TraktEpisodeDto>? Episodes { get; set; }
    }

    private class TraktEpisodeDto
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("last_watched_at")] public string LastWatchedAt { get; set; } = string.Empty;
    }

    private class TraktWatchlistMovieDto
    {
        [JsonPropertyName("listed_at")] public string ListedAt { get; set; } = string.Empty;
        [JsonPropertyName("movie")] public TraktMovieItemDto? Movie { get; set; }
    }

    private class TraktWatchlistShowDto
    {
        [JsonPropertyName("listed_at")] public string ListedAt { get; set; } = string.Empty;
        [JsonPropertyName("show")] public TraktShowItemDto? Show { get; set; }
    }

    private class TraktIdsDto
    {
        [JsonPropertyName("trakt")] public int? Trakt { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("tmdb")] public int? Tmdb { get; set; }
        [JsonPropertyName("imdb")] public string? Imdb { get; set; }
        [JsonPropertyName("tvdb")] public int? Tvdb { get; set; }
    }
}
