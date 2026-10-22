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
        public readonly List<(string Method, string Path, string? ApiKey, string? Bearer, string Body)> Requests = new();
        public Func<string, HttpResponseMessage?> Route { get; set; } = _ => null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(ct) : "";
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path,
                request.Headers.TryGetValues("trakt-api-key", out var k) ? k.First() : null,
                request.Headers.Authorization?.Parameter, body));
            return Route(path) ?? Json(HttpStatusCode.OK, "[]");
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
                "/users/me" => Json(HttpStatusCode.OK, "{\"user\":{\"username\":\"viewer\"}}"),
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
}
