using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class TraktQueueDrainWorker : IQueueDrainWorker
{
    private const string TraktApiBase = "https://api.trakt.tv";
    private readonly DatabaseService _db;
    private readonly ITraktAuthService _auth;
    private readonly HttpClient _http;
    private int _failedAttemptsCount = 0;

    public int PendingQueueDepth => _db.GetTraktQueueCount();
    public int FailedAttemptsCount => _failedAttemptsCount;

    public TraktQueueDrainWorker(DatabaseService db, ITraktAuthService auth, HttpClient? httpClient = null)
    {
        _db = db;
        _auth = auth;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri(TraktApiBase), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("trakt-api-version", "2");
    }

    public async Task DrainAsync(CancellationToken ct)
    {
        if (_auth.State != TraktConnectionState.Connected)
            return;

        var token = _auth.GetAccessToken();
        var clientId = _auth.GetActiveClientId();
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(clientId))
            return;

        // Proactive token refresh check (§6.3)
        var refreshed = await _auth.RefreshIfNeededAsync(ct);
        if (!refreshed && _auth.State != TraktConnectionState.Connected)
        {
            NocturneLogger.Warn("TraktQueueDrainWorker", "Token refresh failed; skipping queue drain.");
            return;
        }

        token = _auth.GetAccessToken();
        if (string.IsNullOrEmpty(token)) return;

        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var events = _db.GetPendingTraktEvents(50);

        foreach (var evt in events)
        {
            if (ct.IsCancellationRequested) break;

            // Rule: Drop start/pause rows older than 5 minutes (stale event noise, §4.4)
            if ((evt.Action == "start" || evt.Action == "pause") && (nowSec - evt.QueuedUtc > 300))
            {
                _db.RemoveTraktEvent(evt.QueueId);
                NocturneLogger.Info("TraktQueueDrainWorker", $"Dropped stale {evt.Action} event (queued {nowSec - evt.QueuedUtc}s ago).");
                continue;
            }

            bool success = await SendEventAsync(evt, token, clientId, ct);
            if (success)
            {
                _db.RemoveTraktEvent(evt.QueueId);
            }
            else
            {
                _failedAttemptsCount++;
                _db.IncrementTraktEventAttempts(evt.QueueId);
                // Back off on network error
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task<bool> SendEventAsync(TraktQueueItem evt, string token, string clientId, CancellationToken ct)
    {
        string endpoint;
        switch (evt.Action.ToLowerInvariant())
        {
            case "start":
                endpoint = "/scrobble/start";
                break;
            case "pause":
                endpoint = "/scrobble/pause";
                break;
            case "stop":
                endpoint = "/scrobble/stop";
                break;
            case "watchlist_add":
                endpoint = "/sync/watchlist";
                break;
            case "watchlist_remove":
                endpoint = "/sync/watchlist/remove";
                break;
            default:
                NocturneLogger.Warn("TraktQueueDrainWorker", $"Unknown queue action '{evt.Action}', dropping.");
                return true;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Remove("trakt-api-key");
            req.Headers.Add("trakt-api-key", clientId);

            if (!string.IsNullOrEmpty(evt.Payload))
            {
                req.Content = new StringContent(evt.Payload, Encoding.UTF8, "application/json");
            }

            using var resp = await _http.SendAsync(req, ct);

            // 200, 201: Success
            if (resp.IsSuccessStatusCode)
            {
                return true;
            }

            // 409 Conflict: Repeat scrobble rejection from Trakt is treated as SUCCESS (§7.3)
            if (resp.StatusCode == HttpStatusCode.Conflict)
            {
                NocturneLogger.Info("TraktQueueDrainWorker", $"Trakt returned 409 Conflict for {evt.Action} - treated as success.");
                return true;
            }

            // 401 Unauthorized: token expired
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                NocturneLogger.Warn("TraktQueueDrainWorker", "Trakt returned 401 Unauthorized during drain.");
                await _auth.RefreshIfNeededAsync(ct);
                return false;
            }

            var errBody = await resp.Content.ReadAsStringAsync(ct);
            NocturneLogger.Warn("TraktQueueDrainWorker", $"Trakt event {evt.Action} failed with status {(int)resp.StatusCode}: {errBody}");
            return false;
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("TraktQueueDrainWorker", $"Exception delivering {evt.Action} to Trakt", ex);
            return false;
        }
    }
}
