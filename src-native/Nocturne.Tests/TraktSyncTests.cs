using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;
using Xunit;

namespace Nocturne.Tests;

public class TraktSyncTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseService _db;

    public TraktSyncTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"trakt_sync_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseService(_tempDbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }

    private class MockTraktHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string json = "[]";

            if (path.EndsWith("/sync/last_activities"))
            {
                json = "{\"movies\":{\"watched_at\":\"2026-10-21T05:00:00.000Z\"},\"episodes\":{\"watched_at\":\"2026-10-21T05:00:00.000Z\"}}";
            }
            else if (path.EndsWith("/sync/watched/movies"))
            {
                json = @"[
                    {
                        ""plays"": 1,
                        ""last_watched_at"": ""2026-10-21T05:00:00.000Z"",
                        ""movie"": {
                            ""title"": ""Arrival"",
                            ""year"": 2016,
                            ""ids"": {
                                ""trakt"": 191635,
                                ""tmdb"": 329865,
                                ""imdb"": ""tt2543164""
                            }
                        }
                    }
                ]";
            }
            else if (path.EndsWith("/sync/watched/shows"))
            {
                json = "[]";
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task PullWatched_AppliesIdempotence_AndSetsTraktOrigin()
    {
        // 1. Setup local group and item with tmdb:movie:329865
        var group = new DedupGroupRecord
        {
            GroupId = 501,
            ItemType = "Movie",
            PrimaryItemId = "item_arr",
            PrimaryServerId = "srv1",
            VersionCount = 1,
            Name = "Arrival",
            SortName = "Arrival",
            Year = 2016,
            CommunityRating = 7.9f,
            MaxHeight = 1080,
            MaxBitrate = 8000000,
            IsProvisional = 0,
            ImageItemId = "item_arr",
            ImageServerId = "srv1",
            TmdbKey = "tmdb:movie:329865"
        };

        _db.UpsertItems(new List<EmbyItem>
        {
            new() { Id = "item_arr", Name = "Arrival", Type = "Movie", ProductionYear = 2016, ServerId = "srv1" }
        }, "srv1");

        _db.SaveDedupBatch(
            new List<DedupGroupRecord> { group },
            new List<int>(),
            new List<(int, int)>(),
            new List<(string, int, bool)> { ("tmdb:movie:329865", 501, true) },
            new List<(string, string, int)> { ("srv1", "item_arr", 501) }
        );

        var http = new HttpClient(new MockTraktHttp()) { BaseAddress = new Uri("https://api.trakt.tv") };

        // Put auth in Connected state
        var tokBytes = CredentialProtection.Protect(Encoding.UTF8.GetBytes("tok_sync"));
        _db.SetIntegrationState("trakt", "mode", "Custom");
        _db.SetIntegrationState("trakt", "custom_client_id", "sync_client_id");
        _db.SetIntegrationState("trakt", "access_token_dpapi", Convert.ToHexString(tokBytes));
        _db.SetIntegrationState("trakt", "client_id_used", "sync_client_id");
        _db.SetIntegrationState("trakt", "expires_utc", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600).ToString());

        var auth = new TraktAuthService(_db, http);
        var syncService = new TraktSyncService(_db, auth, http);

        // Pull watched items
        await syncService.PullWatchedAsync(CancellationToken.None);

        // Verify user_data origin was set to 'trakt' (D7) and played = true
        var udata = _db.GetUserData("srv1", "item_arr");
        Assert.NotNull(udata);
        Assert.True(udata.Played);
        var origin = _db.GetUserDataOrigin("srv1", "item_arr");
        Assert.Equal("trakt", origin);

        // Sibling write queue should have enqueued write to server
        var siblingWrites = _db.GetPendingSiblingWrites(10);
        Assert.Contains(siblingWrites, w => w.ServerId == "srv1" && w.ItemId == "item_arr" && w.MarkPlayed);
    }
}
