using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

    public TmdbStats GetStats() => _db.GetTmdbEnrichmentStats();

    public TmdbEnrichmentService(DatabaseService db, HttpClient? httpClient = null)
    {
        _db = db;
        _http = (httpClient ?? new HttpClient { BaseAddress = new Uri(TmdbApiBase), Timeout = TimeSpan.FromSeconds(20) }).Identify();
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _authState = LoadState();
    }

    public string? CustomApiKey => _authState.CustomApiKey;
    public bool HasBundledKey => !string.IsNullOrEmpty(BundledApiKey);

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
        return ProviderKey.TryParseTmdbKey(providerKey, out var key) && key.Kind is KeyKind.Movie or KeyKind.Series
            ? await FetchTitleAsync(key, ct) : null;
    }
    // Measured live (2026-10-28): 4 fetchers ran at 10.5 req/s (about 380 ms a request, write included) with no 429s, so
    // concurrency, not the budget, was the limit; 8 let the 20 req/s token bucket be the limit, as §10.6 intends.
    private const int Fetchers = 8;
    private const int TransportFailuresToStop = 10; // TMDB unreachable: stop the pass; the next one resumes
    private readonly SemaphoreSlim _pass = new(1, 1);

    // §10.6, D12: called when a sync completes (and from Settings). Everything still eligible (§10.2) is fetched:
    // titles one request per group key, episodes one /season request per season (§10.3, M3). Results are written as
    // each unit completes, so a pass stopped by cancellation (playback, a new sync) or network loss loses nothing
    // done, and the next pass picks up what is still missing. One pass at a time; a second waits for the first.
    // ponytail: the eligible set is held in memory (~30 MB at 370K episodes); page it if libraries grow tenfold.
    public async Task RunAsync(IProgress<EnrichProgress> progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(GetActiveApiKey()))
        {
            NocturneLogger.Info("TmdbEnrichmentService", "TMDB enrichment skipped: No TMDB API key configured.");
            progress.Report(new EnrichProgress(0, 0, 0, 0, "No TMDB key: enrichment is off until one is set.", "finished"));
            return;
        }
        try { await _pass.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            progress.Report(new EnrichProgress(0, 0, 0, 0, "", "paused"));
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int processed = 0, enriched = 0, notFound = 0, transportFailures = 0, total = 0;
        try
        {
            // dedup_groups.tmdb_key in TMDB's form (ProviderKey.TmdbKey): a title, or an episode by show and position.
            var titles = new HashSet<ProviderKey>();
            var seasons = new Dictionary<string, List<ProviderKey>>(); // by TmdbPath: one request per season
            foreach (var g in _db.GetGroupsNeedingTmdbArt(int.MaxValue))
            {
                if (!ProviderKey.TryParseTmdbKey(g.TmdbKey, out var key)) continue;
                if (key.Kind != KeyKind.SeasonEpisode) titles.Add(key);
                else if (seasons.TryGetValue(key.TmdbPath!, out var eps)) eps.Add(key);
                else seasons[key.TmdbPath!] = new() { key };
            }
            total = titles.Count + seasons.Count;
            if (total == 0)
            {
                progress.Report(new EnrichProgress(0, 0, 0, 0, "All artwork up to date.", "finished"));
                return;
            }
            NocturneLogger.Info("TmdbEnrichmentService", $"TMDB enrichment: {titles.Count} title(s), {seasons.Count} season(s) to fetch; " +
                (IsReadAccessToken(GetActiveApiKey()) ? "auth: v4 Read Access Token (Authorization: Bearer)." : "auth: v3 API key (api_key query parameter)."));

            async Task Unit(string label, Func<CancellationToken, Task<(int Found, int Missing)?>> fetch, CancellationToken token)
            {
                try
                {
                    if (await fetch(token) is { } r) { Interlocked.Add(ref enriched, r.Found); Interlocked.Add(ref notFound, r.Missing); }
                    Interlocked.Exchange(ref transportFailures, 0);
                }
                catch (HttpRequestException) { Fail(); }
                catch (TaskCanceledException) when (!token.IsCancellationRequested) { Fail(); } // HttpClient timeout
                progress.Report(new EnrichProgress(total, Interlocked.Increment(ref processed), enriched, notFound, label));
            }
            void Fail()
            {
                if (Interlocked.Increment(ref transportFailures) == TransportFailuresToStop) stop.Cancel();
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = Fetchers, CancellationToken = stop.Token };
            await Parallel.ForEachAsync(titles, options, (key, token) =>
                new ValueTask(Unit(key.TmdbKey!, async t => await FetchTitleAsync(key, t) is { } m ? (m.NotFound ? (0, 1) : (1, 0)) : null, token)));
            await Parallel.ForEachAsync(seasons, options, (season, token) =>
                new ValueTask(Unit(season.Key, t => FetchSeasonAsync(season.Key, season.Value, t), token)));
        }
        catch (OperationCanceledException)
        {
            var unreachable = transportFailures >= TransportFailuresToStop;
            NocturneLogger.Info("TmdbEnrichmentService", unreachable
                ? $"TMDB unreachable; enrichment stopped after {processed}/{total}. The next pass resumes."
                : $"TMDB enrichment paused after {processed}/{total}. The next pass resumes.");
            progress.Report(new EnrichProgress(total, processed, enriched, notFound, "", unreachable ? "unreachable" : "paused"));
            return;
        }
        finally
        {
            _pass.Release();
        }
        NocturneLogger.Info("TmdbEnrichmentService", $"Enrichment finished. Processed {processed}/{total} (Enriched: {enriched}, Not Found: {notFound}).");
        progress.Report(new EnrichProgress(total, processed, enriched, notFound, "", "finished"));
    }

    // One title: 404 records not_found (§10.6: never asked again on every pass); any other failure leaves it for the next.
    private async Task<TmdbMetadata?> FetchTitleAsync(ProviderKey key, CancellationToken ct)
    {
        var providerKey = key.TmdbKey!;
        var (status, dto) = await GetJsonAsync<TmdbDetailDto>(key.TmdbPath!, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        TmdbMetadata? meta = status == HttpStatusCode.NotFound ? new TmdbMetadata { ProviderKey = providerKey, FetchedUtc = now, NotFound = true }
            : dto == null ? null
            : new TmdbMetadata
            {
                ProviderKey = providerKey, PosterPath = dto.PosterPath, BackdropPath = dto.BackdropPath,
                Overview = dto.Overview, VoteAverage = dto.VoteAverage, FetchedUtc = now
            };
        if (meta != null) _db.UpsertTmdbMetadata(meta);
        return meta;
    }

    // M3: one /season call covers every episode of the season. An episode TMDB's season does not list, or a season it
    // does not know, is not_found; all of the season's rows are written in one transaction.
    private async Task<(int Found, int Missing)?> FetchSeasonAsync(string seasonPath, List<ProviderKey> episodes, CancellationToken ct)
    {
        var (status, dto) = await GetJsonAsync<TmdbSeasonDto>(seasonPath, ct);
        if (status != HttpStatusCode.NotFound && dto == null) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var byNumber = (dto?.Episodes ?? new()).GroupBy(e => e.EpisodeNumber).ToDictionary(g => g.Key, g => g.First());
        var rows = episodes.Select(e => byNumber.TryGetValue(e.EpisodeNumber, out var ep)
            ? new TmdbMetadata { ProviderKey = e.TmdbKey!, StillPath = ep.StillPath, Overview = ep.Overview, VoteAverage = ep.VoteAverage, FetchedUtc = now }
            : new TmdbMetadata { ProviderKey = e.TmdbKey!, FetchedUtc = now, NotFound = true }).ToList();
        _db.UpsertTmdbMetadataBatch(rows);
        var found = rows.Count(r => !r.NotFound);
        return (found, rows.Count - found);
    }

    // One GET under the shared token bucket. 429 backs off (Retry-After, else exponentially) and retries, up to 4 tries.
    private async Task<(HttpStatusCode Status, T? Body)> GetJsonAsync<T>(string endpoint, CancellationToken ct) where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            await WaitForRateLimitAsync(ct);
            using var req = CreateRequest(endpoint);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == (HttpStatusCode)429 && attempt < 3)
            {
                await HandleRateLimitBackoffAsync(resp, attempt, ct);
                continue;
            }
            if (!resp.IsSuccessStatusCode) return (resp.StatusCode, null);
            return (resp.StatusCode, await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct));
        }
    }

    private HttpRequestMessage CreateRequest(string endpoint)
    {
        var apiKey = GetActiveApiKey();
        var uri = $"/3{endpoint}";

        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (IsReadAccessToken(apiKey))
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

    // A v4 Read Access Token is a JWT (header.payload.signature) and goes in Authorization: Bearer; a v3 API key is 32
    // hex characters and goes in the api_key query parameter.
    internal static bool IsReadAccessToken(string key) => key.Split('.').Length == 3;

    private async Task WaitForRateLimitAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _rateLock.WaitAsync(ct);
            try
            {
                var now = DateTime.UtcNow;
                var elapsedSec = (now - _lastTokenRefill).TotalSeconds;
                _availableTokens = Math.Min(_bucketCapacity, _availableTokens + (elapsedSec * _bucketCapacity)); // capacity = rate per second
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

    private async Task HandleRateLimitBackoffAsync(HttpResponseMessage resp, int attempt, CancellationToken ct)
    {
        int waitSeconds = 2 << attempt; // 2, 4, 8 s
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
