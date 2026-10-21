using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
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
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri(TraktApiBase), Timeout = TimeSpan.FromSeconds(30) };
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

            if (needMovies)
            {
                NocturneLogger.Info("TraktSyncService", "Pulling updated watched movies from Trakt...");
                await PullWatchedMoviesInternalAsync(token, clientId, ct);
                if (!string.IsNullOrEmpty(activities.Movies.WatchedAt))
                    _db.SetIntegrationState("trakt", "last_movies_watched_at", activities.Movies.WatchedAt);
            }

            if (needEpisodes)
            {
                NocturneLogger.Info("TraktSyncService", "Pulling updated watched episodes from Trakt...");
                await PullWatchedShowsInternalAsync(token, clientId, ct);
                if (!string.IsNullOrEmpty(activities.Episodes.WatchedAt))
                    _db.SetIntegrationState("trakt", "last_episodes_watched_at", activities.Episodes.WatchedAt);
            }

            if (needWatchlist)
            {
                NocturneLogger.Info("TraktSyncService", "Pulling updated watchlist from Trakt...");
                await PullWatchlistInternalAsync(token, clientId, ct);
                if (!string.IsNullOrEmpty(activities.Watchlist.UpdatedAt))
                    _db.SetIntegrationState("trakt", "last_watchlist_updated_at", activities.Watchlist.UpdatedAt);
            }

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
        return null;
    }

    private async Task PullWatchedMoviesInternalAsync(string token, string clientId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/sync/watched/movies?extended=noseasons");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("trakt-api-key", clientId);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return;

        var movies = await resp.Content.ReadFromJsonAsync<List<TraktWatchedMovieDto>>(cancellationToken: ct);
        if (movies == null) return;

        foreach (var m in movies)
        {
            if (ct.IsCancellationRequested) break;
            if (m.Movie?.Ids == null || string.IsNullOrEmpty(m.LastWatchedAt)) continue;

            long watchedAtSec = DateTimeOffset.TryParse(m.LastWatchedAt, out var dt)
                ? dt.ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            int? gid = null;
            if (m.Movie.Ids.Tmdb.HasValue)
                gid = _db.GetGroupIdForDedupKey($"tmdb:movie:{m.Movie.Ids.Tmdb.Value}");
            if (!gid.HasValue && !string.IsNullOrEmpty(m.Movie.Ids.Imdb))
                gid = _db.GetGroupIdForDedupKey($"imdb:movie:{m.Movie.Ids.Imdb}");

            if (!gid.HasValue) continue; // Not in library (§8.2 step 2)

            ApplyWatchedStateToGroup(gid.Value, watchedAtSec);
        }
    }

    private async Task PullWatchedShowsInternalAsync(string token, string clientId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/sync/watched/shows");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("trakt-api-key", clientId);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return;

        var shows = await resp.Content.ReadFromJsonAsync<List<TraktWatchedShowDto>>(cancellationToken: ct);
        if (shows == null) return;

        foreach (var show in shows)
        {
            if (ct.IsCancellationRequested) break;
            if (show.Show?.Ids == null || show.Seasons == null) continue;

            int? tvdbId = show.Show.Ids.Tvdb;
            int? tmdbId = show.Show.Ids.Tmdb;
            string? imdbId = show.Show.Ids.Imdb;

            foreach (var s in show.Seasons)
            {
                if (s.Episodes == null) continue;
                foreach (var ep in s.Episodes)
                {
                    if (string.IsNullOrEmpty(ep.LastWatchedAt)) continue;

                    long epWatchedAtSec = DateTimeOffset.TryParse(ep.LastWatchedAt, out var dt)
                        ? dt.ToUnixTimeSeconds()
                        : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    string sXee = $"s{s.Number:D2}e{ep.Number:D2}";
                    int? epGid = null;

                    if (tvdbId.HasValue)
                        epGid = _db.GetGroupIdForDedupKey($"ep:tvdb:{tvdbId.Value}:{sXee}");
                    if (!epGid.HasValue && tmdbId.HasValue)
                        epGid = _db.GetGroupIdForDedupKey($"ep:tmdb:{tmdbId.Value}:{sXee}");
                    if (!epGid.HasValue && !string.IsNullOrEmpty(imdbId))
                        epGid = _db.GetGroupIdForDedupKey($"ep:imdb:{imdbId}:{sXee}");

                    if (!epGid.HasValue) continue;

                    ApplyWatchedStateToGroup(epGid.Value, epWatchedAtSec);
                }
            }
        }
    }

    private async Task PullWatchlistInternalAsync(string token, string clientId, CancellationToken ct)
    {
        // 1. Movies watchlist
        using (var reqM = new HttpRequestMessage(HttpMethod.Get, "/sync/watchlist/movies"))
        {
            reqM.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            reqM.Headers.Add("trakt-api-key", clientId);

            using var respM = await _http.SendAsync(reqM, ct);
            if (respM.IsSuccessStatusCode)
            {
                var movies = await respM.Content.ReadFromJsonAsync<List<TraktWatchlistMovieDto>>(cancellationToken: ct);
                if (movies != null)
                {
                    foreach (var m in movies)
                    {
                        if (m.Movie?.Ids == null) continue;
                        long addedUtc = DateTimeOffset.TryParse(m.ListedAt, out var d) ? d.ToUnixTimeSeconds() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        string? tmdbKey = m.Movie.Ids.Tmdb.HasValue ? $"tmdb:movie:{m.Movie.Ids.Tmdb.Value}" : null;
                        string key = tmdbKey ?? (!string.IsNullOrEmpty(m.Movie.Ids.Imdb) ? $"imdb:movie:{m.Movie.Ids.Imdb}" : "");
                        if (string.IsNullOrEmpty(key)) continue;

                        _db.UpsertWatchlistEntry("trakt", key, "Movie", addedUtc, m.Movie.Title, m.Movie.Year, tmdbKey);
                    }
                }
            }
        }

        // 2. Shows watchlist
        using (var reqS = new HttpRequestMessage(HttpMethod.Get, "/sync/watchlist/shows"))
        {
            reqS.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            reqS.Headers.Add("trakt-api-key", clientId);

            using var respS = await _http.SendAsync(reqS, ct);
            if (respS.IsSuccessStatusCode)
            {
                var shows = await respS.Content.ReadFromJsonAsync<List<TraktWatchlistShowDto>>(cancellationToken: ct);
                if (shows != null)
                {
                    foreach (var s in shows)
                    {
                        if (s.Show?.Ids == null) continue;
                        long addedUtc = DateTimeOffset.TryParse(s.ListedAt, out var d) ? d.ToUnixTimeSeconds() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        string? tmdbKey = s.Show.Ids.Tmdb.HasValue ? $"tmdb:tv:{s.Show.Ids.Tmdb.Value}" : null;
                        string key = s.Show.Ids.Tvdb.HasValue ? $"tvdb:series:{s.Show.Ids.Tvdb.Value}" : (tmdbKey ?? "");
                        if (string.IsNullOrEmpty(key)) continue;

                        _db.UpsertWatchlistEntry("trakt", key, "Series", addedUtc, s.Show.Title, s.Show.Year, tmdbKey);
                    }
                }
            }
        }
    }

    private void ApplyWatchedStateToGroup(int groupId, long watchedAtSec)
    {
        var items = _db.GetGroupItemIds(groupId);
        foreach (var (srv, id) in items)
        {
            var existing = _db.GetUserData(srv, id);
            // Idempotence check (§8.2 step 3)
            if (existing != null && existing.Played && existing.LastPlayedDate.HasValue)
            {
                long existingSec = ((DateTimeOffset)existing.LastPlayedDate.Value).ToUnixTimeSeconds();
                if (watchedAtSec <= existingSec)
                    continue;
            }

            // Upsert user_data with origin = 'trakt' and source timestamp (D8)
            _db.UpdateUserDataState(srv, id, positionTicks: 0, runtimeTicks: null, played: true, lastPlayedUtc: watchedAtSec, origin: "trakt");

            // Enqueue touched version to sibling_write_queue (safe under D7 because origin is 'trakt')
            _db.EnqueueSiblingWrite(srv, id, positionTicks: 0, markPlayed: true);
        }
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
