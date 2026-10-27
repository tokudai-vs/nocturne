using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class TraktWatchlistMovieItem
{
    [JsonPropertyName("movie")]
    public TraktMovieObj? Movie { get; set; }

    [JsonPropertyName("listed_at")]
    public DateTime ListedAt { get; set; }
}

public class TraktWatchlistShowItem
{
    [JsonPropertyName("show")]
    public TraktShowObj? Show { get; set; }

    [JsonPropertyName("listed_at")]
    public DateTime ListedAt { get; set; }
}

public class TraktMovieObj
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("ids")]
    public TraktIds? Ids { get; set; }
}

public class TraktShowObj
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("ids")]
    public TraktIds? Ids { get; set; }
}

public class TraktIds
{
    [JsonPropertyName("trakt")]
    public int? Trakt { get; set; }

    [JsonPropertyName("tmdb")]
    public int? Tmdb { get; set; }

    [JsonPropertyName("imdb")]
    public string? Imdb { get; set; }
}

public class TraktUserStatsResponse
{
    [JsonPropertyName("movies")]
    public TraktMoviesStats? Movies { get; set; }

    [JsonPropertyName("episodes")]
    public TraktEpisodesStats? Episodes { get; set; }

    [JsonPropertyName("shows")]
    public TraktShowsStats? Shows { get; set; }
}

public class TraktMoviesStats
{
    [JsonPropertyName("watched")]
    public int Watched { get; set; }

    [JsonPropertyName("minutes")]
    public int Minutes { get; set; }
}

public class TraktEpisodesStats
{
    [JsonPropertyName("watched")]
    public int Watched { get; set; }

    [JsonPropertyName("minutes")]
    public int Minutes { get; set; }
}

public class TraktShowsStats
{
    [JsonPropertyName("watched")]
    public int Watched { get; set; }
}

// Trakt reads the new auth stack does not cover yet (lifetime stats, watchlist pull, watchlist toggle). I6: the
// legacy stack's own device-code auth (plaintext tokens in settings, a hard-coded client ID and a client secret)
// and its scrobble calls are gone. Authentication is TraktAuthService's, and scrobbling is the queue path's
// (TraktScrobbleService + TraktQueueDrainWorker) only, so a playback is scrobbled once, not twice.
public class TraktService
{
    private readonly HttpClient _http;
    private readonly ITraktAuthService _auth;
    private const string TraktApiUrl = "https://api.trakt.tv";

    public TraktService(ITraktAuthService auth, HttpClient? httpClient = null)
    {
        _auth = auth;
        _http = (httpClient ?? new HttpClient { BaseAddress = new Uri(TraktApiUrl), Timeout = TimeSpan.FromSeconds(15) }).Identify();
    }

    // Was "a legacy access token exists in settings", which stayed false once the new auth connected, so the
    // watchlist pull and the sidebar shelf never lit up.
    public bool IsConnected => _auth.State == TraktConnectionState.Connected;

    private void ConfigureHeaders()
    {
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("trakt-api-version", "2");
        _http.DefaultRequestHeaders.Add("trakt-api-key", _auth.GetActiveClientId());
        var token = _auth.GetAccessToken();
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<TraktUserStats?> GetUserStatsAsync()
    {
        if (!IsConnected) return null;
        try
        {
            ConfigureHeaders();
            var resp = await _http.GetAsync("/users/me/stats");
            if (resp.IsSuccessStatusCode)
            {
                var stats = await resp.Content.ReadFromJsonAsync<TraktUserStatsResponse>();
                if (stats != null)
                {
                    var totalMins = (stats.Movies?.Minutes ?? 0) + (stats.Episodes?.Minutes ?? 0);
                    return new TraktUserStats
                    {
                        MoviesWatched = stats.Movies?.Watched ?? 0,
                        EpisodesWatched = stats.Episodes?.Watched ?? 0,
                        TotalHours = totalMins / 60,
                        DistinctShows = stats.Shows?.Watched ?? 0
                    };
                }
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("Trakt", $"Failed to fetch user stats: {ex.Message}");
        }
        return null;
    }

    public async Task<List<TraktWatchlistItem>> GetWatchlistAsync()
    {
        var items = new List<TraktWatchlistItem>();
        if (!IsConnected) return items;

        try
        {
            ConfigureHeaders();
            // Fetch Movies
            var movieResp = await _http.GetAsync("/sync/watchlist/movies");
            if (movieResp.IsSuccessStatusCode)
            {
                var movies = await movieResp.Content.ReadFromJsonAsync<List<TraktWatchlistMovieItem>>();
                if (movies != null)
                {
                    foreach (var m in movies)
                    {
                        if (m.Movie != null)
                        {
                            items.Add(new TraktWatchlistItem
                            {
                                Id = "trakt_m_" + (m.Movie.Ids?.Trakt?.ToString() ?? Guid.NewGuid().ToString("N")),
                                Title = m.Movie.Title,
                                Year = m.Movie.Year,
                                Type = "Movie",
                                TmdbId = m.Movie.Ids?.Tmdb?.ToString(),
                                ImdbId = m.Movie.Ids?.Imdb,
                                AddedAt = m.ListedAt
                            });
                        }
                    }
                }
            }

            // Fetch Shows
            var showResp = await _http.GetAsync("/sync/watchlist/shows");
            if (showResp.IsSuccessStatusCode)
            {
                var shows = await showResp.Content.ReadFromJsonAsync<List<TraktWatchlistShowItem>>();
                if (shows != null)
                {
                    foreach (var s in shows)
                    {
                        if (s.Show != null)
                        {
                            items.Add(new TraktWatchlistItem
                            {
                                Id = "trakt_s_" + (s.Show.Ids?.Trakt?.ToString() ?? Guid.NewGuid().ToString("N")),
                                Title = s.Show.Title,
                                Year = s.Show.Year,
                                Type = "Series",
                                TmdbId = s.Show.Ids?.Tmdb?.ToString(),
                                ImdbId = s.Show.Ids?.Imdb,
                                AddedAt = s.ListedAt
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("Trakt", "Watchlist fetch error", ex);
        }

        return items;
    }

    public async Task<bool> ToggleWatchlistAsync(EmbyItem item, bool addToWatchlist)
    {
        if (!IsConnected) return false;
        try
        {
            ConfigureHeaders();
            var endpoint = addToWatchlist ? "/sync/watchlist" : "/sync/watchlist/remove";
            object payload;
            if (item.Type == "Movie")
            {
                payload = new
                {
                    movies = new[] { new { title = item.Name, year = item.ProductionYear, ids = new { imdb = item.ImdbId } } }
                };
            }
            else
            {
                payload = new
                {
                    shows = new[] { new { title = item.Name, year = item.ProductionYear, ids = new { imdb = item.ImdbId } } }
                };
            }

            var resp = await _http.PostAsJsonAsync(endpoint, payload);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("Trakt", $"Toggle watchlist error: {ex.Message}");
            return false;
        }
    }
}
