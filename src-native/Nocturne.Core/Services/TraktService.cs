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

public class TraktService
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private const string TraktApiUrl = "https://api.trakt.tv";
    private const string DefaultClientId = "d87a71917f30ba4974ae4360e5db70c7b39a4ce6087b7a2d1d40c0617300cbe9"; // Nocturne Trakt app ID

    public TraktService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient { BaseAddress = new Uri(TraktApiUrl), Timeout = TimeSpan.FromSeconds(15) };
        ConfigureHeaders();
    }

    public string ClientId => !string.IsNullOrWhiteSpace(_settings.Current.TraktClientIdOverride)
        ? _settings.Current.TraktClientIdOverride.Trim()
        : DefaultClientId;

    public string ClientSecret => !string.IsNullOrWhiteSpace(_settings.Current.TraktClientSecretOverride)
        ? _settings.Current.TraktClientSecretOverride.Trim()
        : string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public void ConfigureHeaders()
    {
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("trakt-api-version", "2");
        _http.DefaultRequestHeaders.Add("trakt-api-key", ClientId);

        var token = _settings.Current.TraktAccessToken;
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public bool IsConnected => !string.IsNullOrEmpty(_settings.Current.TraktAccessToken);

    public async Task<(TraktDeviceCode? Code, string? ErrorMessage)> GetDeviceCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            return (null, "Please enter your Trakt Client ID and Client Secret in Settings below.");
        }

        ConfigureHeaders();
        var payload = new { client_id = ClientId };
        try
        {
            var resp = await _http.PostAsJsonAsync("/oauth/device/code", payload);
            if (resp.IsSuccessStatusCode)
            {
                var code = await resp.Content.ReadFromJsonAsync<TraktDeviceCode>();
                return (code, null);
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                NocturneLogger.Warn("Trakt", $"Device code error ({resp.StatusCode}): {body}");
                return (null, $"Trakt rejected request ({resp.StatusCode}). Please verify your Client ID.");
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("Trakt", "Failed to get device code", ex);
            return (null, $"Connection failed: {ex.Message}");
        }
    }

    public async Task<(bool Success, bool Expired, string? ErrorMessage)> PollTokenAsync(string deviceCode)
    {
        var payload = new
        {
            code = deviceCode,
            client_id = ClientId,
            client_secret = ClientSecret
        };

        try
        {
            var resp = await _http.PostAsJsonAsync("/oauth/device/token", payload);
            if (resp.IsSuccessStatusCode)
            {
                var token = await resp.Content.ReadFromJsonAsync<TraktTokenResponse>();
                if (token != null)
                {
                    _settings.Update(s =>
                    {
                        s.TraktAccessToken = token.AccessToken;
                        s.TraktRefreshToken = token.RefreshToken;
                        s.TraktTokenExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn);
                    });
                    ConfigureHeaders();

                    // Fetch user info to populate TraktUsername
                    try
                    {
                        var userResp = await _http.GetAsync("/users/me");
                        if (userResp.IsSuccessStatusCode)
                        {
                            using var doc = await JsonDocument.ParseAsync(await userResp.Content.ReadAsStreamAsync());
                            if (doc.RootElement.TryGetProperty("username", out var uProp))
                            {
                                var username = uProp.GetString();
                                _settings.Update(s => s.TraktUsername = username);
                            }
                        }
                    }
                    catch { }

                    NocturneLogger.Info("Trakt", $"Authenticated successfully with Trakt.tv (@{_settings.Current.TraktUsername})");
                    return (true, false, null);
                }
            }
            else if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // 400 Bad Request = Pending authorization from user
                return (false, false, null);
            }
            else if ((int)resp.StatusCode == 410)
            {
                // 410 = Code expired
                return (false, true, "Device code expired. Please try connecting again.");
            }
            else if ((int)resp.StatusCode == 404 || (int)resp.StatusCode == 418 || (int)resp.StatusCode == 403)
            {
                // Denied or limit reached
                return (false, true, "Trakt rejected authorization. If Trakt reported 'no more can be added', please disconnect unused apps at trakt.tv/settings/apps.");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                NocturneLogger.Warn("Trakt", $"Poll token response {resp.StatusCode}: {body}");
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("Trakt", $"Poll token error: {ex.Message}");
        }
        return (false, false, null);
    }

    public void Disconnect()
    {
        _settings.Update(s =>
        {
            s.TraktAccessToken = null;
            s.TraktRefreshToken = null;
            s.TraktTokenExpiresAt = null;
            s.TraktUsername = null;
        });
        ConfigureHeaders();
        NocturneLogger.Info("Trakt", "Disconnected Trakt.tv account.");
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

    public async Task ScrobbleStartAsync(EmbyItem item, double progressPercent)
    {
        if (!IsConnected || !_settings.Current.TraktAutoScrobble) return;
        await SendScrobbleAsync("/scrobble/start", item, progressPercent);
    }

    public async Task ScrobblePauseAsync(EmbyItem item, double progressPercent)
    {
        if (!IsConnected || !_settings.Current.TraktAutoScrobble) return;
        // If progress is >= 80%, convert action to stop so Trakt marks as watched
        if (progressPercent >= 80.0)
        {
            await SendScrobbleAsync("/scrobble/stop", item, progressPercent);
        }
        else
        {
            await SendScrobbleAsync("/scrobble/pause", item, progressPercent);
        }
    }

    public async Task ScrobbleStopAsync(EmbyItem item, double progressPercent)
    {
        if (!IsConnected || !_settings.Current.TraktAutoScrobble) return;
        await SendScrobbleAsync("/scrobble/stop", item, progressPercent);
    }

    private async Task SendScrobbleAsync(string endpoint, EmbyItem item, double progressPercent)
    {
        try
        {
            ConfigureHeaders();
            object payload;
            if (item.Type == "Movie")
            {
                payload = new
                {
                    movie = new { title = item.Name, year = item.ProductionYear, ids = new { imdb = item.ImdbId } },
                    progress = Math.Round(progressPercent, 1)
                };
            }
            else
            {
                payload = new
                {
                    show = new { title = item.SeriesName ?? item.Name, ids = new { imdb = item.ImdbId } },
                    episode = new { season = item.ParentIndexNumber ?? 1, number = item.IndexNumber ?? 1 },
                    progress = Math.Round(progressPercent, 1)
                };
            }

            var resp = await _http.PostAsJsonAsync(endpoint, payload);
            if (resp.IsSuccessStatusCode)
            {
                NocturneLogger.Info("Trakt", $"Scrobble {endpoint} success: {item.Name} at {progressPercent:F1}%");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                NocturneLogger.Warn("Trakt", $"Scrobble {endpoint} warning (Status {resp.StatusCode}): {body}");
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("Trakt", "Scrobble network error", ex);
        }
    }
}
