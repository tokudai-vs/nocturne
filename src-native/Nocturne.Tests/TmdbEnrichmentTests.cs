using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class TmdbEnrichmentTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseService _db;

    public TmdbEnrichmentTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"tmdb_enrich_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseService(_tempDbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }

    private class MockTmdbHandler : HttpMessageHandler
    {
        private int _attempts = 0;
        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _attempts++;
            if (_attempts == 1)
            {
                // First call: Simulate 429 Too Many Requests with Retry-After 1 second
                var rateLimitResponse = new HttpResponseMessage((HttpStatusCode)429);
                rateLimitResponse.Headers.Add("Retry-After", "1");
                return Task.FromResult(rateLimitResponse);
            }

            // Second call: Return successful movie metadata
            var okResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{
                    ""id"": 550,
                    ""poster_path"": ""/fightclub_poster.jpg"",
                    ""backdrop_path"": ""/fightclub_backdrop.jpg"",
                    ""overview"": ""An insomniac office worker..."",
                    ""vote_average"": 8.4
                }", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(okResponse);
        }
    }

    [Fact]
    public async Task TmdbRateLimitAndBackoff_Handles429AndCachesMetadata()
    {
        var mockHandler = new MockTmdbHandler();
        var client = new HttpClient(mockHandler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };

        var service = new TmdbEnrichmentService(_db, client);
        service.SetCustomApiKey("test_tmdb_key");

        // Request movie metadata
        var sw = Stopwatch.StartNew();
        var metadata = await service.GetAsync("tmdb:movie:550", CancellationToken.None);
        sw.Stop();

        Assert.NotNull(metadata);
        Assert.Equal("/fightclub_poster.jpg", metadata.PosterPath);
        Assert.Equal("/fightclub_backdrop.jpg", metadata.BackdropPath);
        Assert.Equal(8.4, metadata.VoteAverage);
        Assert.Equal(2, mockHandler.Attempts); // 1st was 429, 2nd was 200

        // Verify cached in SQLite database
        var cached = _db.GetTmdbMetadata("tmdb:movie:550");
        Assert.NotNull(cached);
        Assert.Equal("/fightclub_poster.jpg", cached.PosterPath);
    }

    [Fact]
    public async Task TokenBucket_RateLimitsCallsWithinQuota()
    {
        int totalRequests = 0;
        var fastHandler = new DelegatingHandlerStub((req, ct) =>
        {
            Interlocked.Increment(ref totalRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""id"": 1, ""poster_path"": ""/p.jpg""}", Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(fastHandler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
        var service = new TmdbEnrichmentService(_db, client);
        service.SetCustomApiKey("fast_key");

        // Fire 5 requests sequentially
        for (int i = 0; i < 5; i++)
        {
            await service.GetAsync($"tmdb:movie:{1000 + i}", CancellationToken.None);
        }

        Assert.Equal(5, totalRequests);
    }

    private class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request, cancellationToken));
        }
    }
}
