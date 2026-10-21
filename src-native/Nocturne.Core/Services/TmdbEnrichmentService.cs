using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class TmdbEnrichmentService : ITmdbEnrichmentService
{
    private const string TmdbApiBase = "https://api.themoviedb.org";
    private const string BundledApiKey = ""; // Default TMDB API Read Token / Key

    private readonly DatabaseService _db;
    private readonly HttpClient _http;
    private TmdbAuthState _authState;

    // Rate limiter: token bucket with default 20 tokens/sec (§10.6)
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private double _availableTokens = 20.0;
    private double _bucketCapacity = 20.0;
    private DateTime _lastTokenRefill = DateTime.UtcNow;

    public int EnrichedCount => _db.GetTmdbEnrichmentStats().EnrichedCount;
    public int PendingCount => _db.GetTmdbEnrichmentStats().PendingCount;
    public int NotFoundCount => _db.GetTmdbEnrichmentStats().NotFoundCount;

    public TmdbEnrichmentService(DatabaseService db, HttpClient? httpClient = null)
    {
        _db = db;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri(TmdbApiBase), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _authState = LoadState();
    }

    public string GetActiveApiKey()
    {
        if (_authState.Mode == CredentialMode.Custom && !string.IsNullOrWhiteSpace(_authState.CustomApiKey))
        {
            return _authState.CustomApiKey.Trim();
        }
        return BundledApiKey;
    }

    public void SetCustomApiKey(string? apiKey)
    {
        var trimmed = apiKey?.Trim();
        _authState.CustomApiKey = trimmed;
        _authState.Mode = !string.IsNullOrEmpty(trimmed) ? CredentialMode.Custom : CredentialMode.Bundled;
        SaveState();
    }

    public async Task<TmdbMetadata?> GetAsync(string providerKey, CancellationToken ct)
    {
        var cached = _db.GetTmdbMetadata(providerKey);
        if (cached != null) return cached;

        // If not cached, attempt single fetch if key is authoritative TMDB key
        var m = Regex.Match(providerKey, @"^tmdb:(movie|tv):(\d+)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            string type = m.Groups[1].Value.ToLowerInvariant();
            int id = int.Parse(m.Groups[2].Value);
            return await FetchTitleMetadataAsync(providerKey, type, id, ct);
        }

        return null;
    }

    public async Task RunAsync(IProgress<EnrichProgress> progress, CancellationToken ct)
    {
        var apiKey = GetActiveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            NocturneLogger.Info("TmdbEnrichmentService", "TMDB enrichment skipped: No TMDB API key configured.");
            return;
        }

        NocturneLogger.Info("TmdbEnrichmentService", "Starting TMDB fallback enrichment pass...");

        var candidates = _db.GetGroupsNeedingTmdbArt(2000);
        int total = candidates.Count;
        int processed = 0;
        int enriched = 0;
        int notFound = 0;

        if (total == 0)
        {
            progress.Report(new EnrichProgress(0, 0, 0, 0, "All artwork up to date."));
            return;
        }

        // Season-level batching cache for episodes (§10.3)
        // Key: (SeriesTmdbId, SeasonNumber) -> Json Document with all episode stills
        var seasonCache = new Dictionary<(int SeriesId, int SeasonNum), List<TmdbEpisodeDto>>();

        foreach (var item in candidates)
        {
            if (ct.IsCancellationRequested) break;

            processed++;
            string title = item.Title ?? "Unknown Title";
            progress.Report(new EnrichProgress(total, processed, enriched, notFound, title));

            try
            {
                // Check if this is an episode needing a still
                if (string.Equals(item.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
                {
                    var epMatch = Regex.Match(item.TmdbKey, @"^tmdb:tv:(\d+)$", RegexOptions.IgnoreCase);
                    if (epMatch.Success && item.SeasonNumber.HasValue && item.EpisodeNumber.HasValue)
                    {
                        int seriesId = int.Parse(epMatch.Groups[1].Value);
                        int seasonNum = item.SeasonNumber.Value;
                        int epNum = item.EpisodeNumber.Value;
                        var seasonKey = (seriesId, seasonNum);

                        if (!seasonCache.TryGetValue(seasonKey, out var epList))
                        {
                            epList = await FetchSeasonEpisodesAsync(seriesId, seasonNum, ct);
                            if (epList != null)
                            {
                                seasonCache[seasonKey] = epList;
                            }
                        }

                        if (epList != null)
                        {
                            var epDto = epList.Find(e => e.EpisodeNumber == epNum);
                            if (epDto != null && !string.IsNullOrEmpty(epDto.StillPath))
                            {
                                var meta = new TmdbMetadata
                                {
                                    ProviderKey = item.TmdbKey,
                                    StillPath = epDto.StillPath,
                                    Overview = epDto.Overview,
                                    VoteAverage = epDto.VoteAverage,
                                    FetchedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                    NotFound = false
                                };
                                _db.UpsertTmdbMetadata(meta);
                                enriched++;
                                continue;
                            }
                        }
                    }
                }

                // Movie or Series
                var m = Regex.Match(item.TmdbKey, @"^tmdb:(movie|tv):(\d+)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string type = m.Groups[1].Value.ToLowerInvariant();
                    int tmdbId = int.Parse(m.Groups[2].Value);

                    var res = await FetchTitleMetadataAsync(item.TmdbKey, type, tmdbId, ct);
                    if (res != null)
                    {
                        if (res.NotFound) notFound++;
                        else enriched++;
                    }
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("TmdbEnrichmentService", $"Failed to enrich {item.TmdbKey}: {ex.Message}");
            }
        }

        NocturneLogger.Info("TmdbEnrichmentService", $"Enrichment finished. Processed {processed}/{total} (Enriched: {enriched}, Not Found: {notFound}).");
    }

    private async Task<TmdbMetadata?> FetchTitleMetadataAsync(string providerKey, string type, int id, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await WaitForRateLimitAsync(ct);

            var endpoint = $"/{type}/{id}";
            using var req = CreateRequest(endpoint);

            using var resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                var nf = new TmdbMetadata
                {
                    ProviderKey = providerKey,
                    FetchedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    NotFound = true
                };
                _db.UpsertTmdbMetadata(nf);
                return nf;
            }

            if (resp.StatusCode == (HttpStatusCode)429)
            {
                await HandleRateLimitBackoffAsync(resp, ct);
                continue;
            }

            if (resp.IsSuccessStatusCode)
            {
                var dto = await resp.Content.ReadFromJsonAsync<TmdbDetailDto>(cancellationToken: ct);
                if (dto != null)
                {
                    var meta = new TmdbMetadata
                    {
                        ProviderKey = providerKey,
                        PosterPath = dto.PosterPath,
                        BackdropPath = dto.BackdropPath,
                        Overview = dto.Overview,
                        VoteAverage = dto.VoteAverage,
                        FetchedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        NotFound = false
                    };
                    _db.UpsertTmdbMetadata(meta);
                    return meta;
                }
            }

            break;
        }

        return null;
    }

    private async Task<List<TmdbEpisodeDto>?> FetchSeasonEpisodesAsync(int seriesId, int seasonNumber, CancellationToken ct)
    {
        await WaitForRateLimitAsync(ct);

        var endpoint = $"/tv/{seriesId}/season/{seasonNumber}";
        using var req = CreateRequest(endpoint);

        using var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode == (HttpStatusCode)429)
        {
            await HandleRateLimitBackoffAsync(resp, ct);
            return null;
        }

        if (resp.IsSuccessStatusCode)
        {
            var seasonDto = await resp.Content.ReadFromJsonAsync<TmdbSeasonDto>(cancellationToken: ct);
            return seasonDto?.Episodes;
        }

        return null;
    }

    private HttpRequestMessage CreateRequest(string endpoint)
    {
        var apiKey = GetActiveApiKey();
        var uri = $"/3{endpoint}";

        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        // If API key is a v4 Read Access Token (starts with eyJ... / JWT), use Bearer
        if (apiKey.Length > 40 && apiKey.Contains('.'))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        else
        {
            // Otherwise v3 API key query parameter
            var sep = uri.Contains('?') ? "&" : "?";
            req.RequestUri = new Uri($"{TmdbApiBase}{uri}{sep}api_key={apiKey}");
        }
        return req;
    }

    private async Task WaitForRateLimitAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _rateLock.WaitAsync(ct);
            try
            {
                var now = DateTime.UtcNow;
                var elapsedSec = (now - _lastTokenRefill).TotalSeconds;
                _availableTokens = Math.Min(_bucketCapacity, _availableTokens + (elapsedSec * 20.0));
                _lastTokenRefill = now;

                if (_availableTokens >= 1.0)
                {
                    _availableTokens -= 1.0;
                    return;
                }
            }
            finally
            {
                _rateLock.Release();
            }

            // Need to wait for tokens to refill (50ms)
            await Task.Delay(50, ct);
        }
    }

    private async Task HandleRateLimitBackoffAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        int waitSeconds = 2;
        if (resp.Headers.RetryAfter != null && resp.Headers.RetryAfter.Delta.HasValue)
        {
            waitSeconds = (int)Math.Ceiling(resp.Headers.RetryAfter.Delta.Value.TotalSeconds);
        }

        NocturneLogger.Warn("TmdbEnrichmentService", $"TMDB 429 rate limit hit. Halving token bucket and backing off for {waitSeconds}s.");

        await _rateLock.WaitAsync(ct);
        try
        {
            _bucketCapacity = 10.0; // Halve bucket for 60s (§10.6)
            _availableTokens = 0;
        }
        finally
        {
            _rateLock.Release();
        }

        await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);

        // Schedule restoring capacity after 60s
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            await _rateLock.WaitAsync();
            try
            {
                _bucketCapacity = 20.0;
            }
            finally
            {
                _rateLock.Release();
            }
        });
    }

    private TmdbAuthState LoadState()
    {
        var modeStr = _db.GetIntegrationState("tmdb", "mode");
        var encKey = _db.GetIntegrationState("tmdb", "api_key_dpapi");
        string? apiKey = null;

        if (!string.IsNullOrEmpty(encKey))
        {
            try
            {
                var bytes = Convert.FromHexString(encKey);
                apiKey = CredentialProtection.UnprotectString(bytes);
            }
            catch (Exception ex)
            {
                // Treated as no custom key (bundled mode applies); re-entering the key in Settings stores it again.
                NocturneLogger.Error("TmdbEnrichmentService", "Stored TMDB API key could not be decrypted; ignoring it", ex);
            }
        }

        return new TmdbAuthState
        {
            Mode = Enum.TryParse<CredentialMode>(modeStr, out var m) ? m : CredentialMode.Bundled,
            CustomApiKey = apiKey
        };
    }

    private void SaveState()
    {
        _db.SetIntegrationState("tmdb", "mode", _authState.Mode.ToString());
        if (!string.IsNullOrEmpty(_authState.CustomApiKey))
        {
            var enc = CredentialProtection.ProtectString(_authState.CustomApiKey);
            _db.SetIntegrationState("tmdb", "api_key_dpapi", Convert.ToHexString(enc));
        }
        else
        {
            _db.SetIntegrationState("tmdb", "api_key_dpapi", "");
        }
    }

    private class TmdbDetailDto
    {
        [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
        [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
        [JsonPropertyName("overview")] public string? Overview { get; set; }
        [JsonPropertyName("vote_average")] public double? VoteAverage { get; set; }
    }

    private class TmdbSeasonDto
    {
        [JsonPropertyName("episodes")] public List<TmdbEpisodeDto> Episodes { get; set; } = new();
    }

    private class TmdbEpisodeDto
    {
        [JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
        [JsonPropertyName("still_path")] public string? StillPath { get; set; }
        [JsonPropertyName("overview")] public string? Overview { get; set; }
        [JsonPropertyName("vote_average")] public double? VoteAverage { get; set; }
    }
}
