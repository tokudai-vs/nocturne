using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;
using Xunit;

namespace Nocturne.Tests;

public class TraktScrobbleTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseService _db;
    private readonly TraktAuthService _auth;
    private readonly TraktScrobbleService _scrobbler;

    public TraktScrobbleTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"trakt_scrobble_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseService(_tempDbPath);

        // Put auth in Connected state by setting an active token
        var tokBytes = CredentialProtection.Protect(System.Text.Encoding.UTF8.GetBytes("test_token"));
        _db.SetIntegrationState("trakt", "mode", "Custom");
        _db.SetIntegrationState("trakt", "custom_client_id", "test_client_id");
        _db.SetIntegrationState("trakt", "access_token_dpapi", Convert.ToHexString(tokBytes));
        _db.SetIntegrationState("trakt", "client_id_used", "test_client_id");
        _db.SetIntegrationState("trakt", "expires_utc", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600).ToString());

        _auth = new TraktAuthService(_db);
        _scrobbler = new TraktScrobbleService(_db, _auth);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }

    [Fact]
    public void BuildScrobblePayload_Movie_IncludesAuthoritativeIds()
    {
        var group = new DedupGroupRecord
        {
            GroupId = 101,
            ItemType = "Movie",
            PrimaryItemId = "mov1",
            PrimaryServerId = "srv1",
            VersionCount = 1,
            Name = "Inception",
            SortName = "Inception",
            Year = 2010,
            Overview = "Dreams",
            CommunityRating = 8.8f,
            MaxHeight = 1080,
            MaxBitrate = 8000000,
            IsProvisional = 0,
            ImageItemId = "mov1",
            ImageServerId = "srv1",
            TmdbKey = "tmdb:movie:27205"
        };

        _db.SaveDedupBatch(
            new List<DedupGroupRecord> { group },
            new List<int>(),
            new List<(int, int)>(),
            new List<(string, int, bool)>
            {
                ("tmdb:movie:27205", 101, true),
                ("imdb:movie:tt1375666", 101, true) // the dedup index's form (ProviderKey); this read "imdb:tt1375666", which nothing writes
            },
            new List<(string, string, int)> { ("srv1", "mov1", 101) }
        );

        var payloadJson = _scrobbler.BuildScrobblePayload(group, 45.5);
        Assert.NotNull(payloadJson);

        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        Assert.Equal(45.5, root.GetProperty("progress").GetDouble());
        var movie = root.GetProperty("movie");
        Assert.Equal("Inception", movie.GetProperty("title").GetString());
        Assert.Equal(2010, movie.GetProperty("year").GetInt32());
        var ids = movie.GetProperty("ids");
        Assert.Equal(27205, ids.GetProperty("tmdb").GetInt32());
        Assert.Equal("tt1375666", ids.GetProperty("imdb").GetString());
    }

    [Fact]
    public void BuildScrobblePayload_Episode_IncludesShowAndEpisodeIds()
    {
        var group = new DedupGroupRecord
        {
            GroupId = 202,
            ItemType = "Episode",
            PrimaryItemId = "ep1",
            PrimaryServerId = "srv1",
            VersionCount = 1,
            Name = "Pilot",
            SortName = "Pilot",
            Year = 2008,
            Overview = "Chemistry teacher turns cook",
            CommunityRating = 9.0f,
            MaxHeight = 1080,
            MaxBitrate = 8000000,
            IsProvisional = 0,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            SeriesGroupId = 50,
            ImageItemId = "ep1",
            ImageServerId = "srv1"
        };

        _db.SaveDedupBatch(
            new List<DedupGroupRecord> { group },
            new List<int>(),
            new List<(int, int)>(),
            new List<(string, int, bool)>
            {
                ("ep:tvdb:81189:s01e01", 202, true)
            },
            new List<(string, string, int)> { ("srv1", "ep1", 202) }
        );

        var payloadJson = _scrobbler.BuildScrobblePayload(group, 85.0);
        Assert.NotNull(payloadJson);

        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        Assert.Equal(85.0, root.GetProperty("progress").GetDouble());
        var episode = root.GetProperty("episode");
        Assert.Equal(1, episode.GetProperty("season").GetInt32());
        Assert.Equal(1, episode.GetProperty("number").GetInt32());

        var show = root.GetProperty("show");
        var showIds = show.GetProperty("ids");
        Assert.Equal(81189, showIds.GetProperty("tvdb").GetInt32());
    }

    [Fact]
    public void ProvisionalGroup_IsSkippedByScrobbler()
    {
        var group = new DedupGroupRecord
        {
            GroupId = 303,
            ItemType = "Movie",
            PrimaryItemId = "prov1",
            PrimaryServerId = "srv1",
            VersionCount = 1,
            Name = "Unknown Movie",
            SortName = "Unknown Movie",
            Year = 2024,
            IsProvisional = 1 // Provisional!
        };

        _db.SaveDedupBatch(
            new List<DedupGroupRecord> { group },
            new List<int>(),
            new List<(int, int)>(),
            new List<(string, int, bool)> { ("prov:movie:unknown", 303, false) },
            new List<(string, string, int)> { ("srv1", "prov1", 303) }
        );

        _scrobbler.Start(303, 10.0);

        // Queue should be empty and skip count incremented
        Assert.Equal(0, _db.GetTraktQueueCount());
        Assert.Equal(1, _scrobbler.SkippedProvisionalCount);
    }

    // #24 (T5): playing a title in Nocturne is a local change, even when its last change came from a Trakt pull; the
    // scrobble used to read that stored origin and stay silent, so re-watching anything Trakt had marked never scrobbled.
    // D7 is enforced on changes whose origin is Trakt (OriginTests), not on what a row last held.
    [Fact]
    public void ReWatchingATitleTraktMarked_StillScrobbles()
    {
        var group = new DedupGroupRecord
        {
            GroupId = 404,
            ItemType = "Movie",
            PrimaryItemId = "trakt_mov",
            PrimaryServerId = "srv1",
            VersionCount = 1,
            Name = "Interstellar",
            SortName = "Interstellar",
            Year = 2014,
            CommunityRating = 8.7f,
            MaxHeight = 1080,
            MaxBitrate = 8000000,
            IsProvisional = 0,
            TmdbKey = "tmdb:movie:157336"
        };

        _db.SaveDedupBatch(
            new List<DedupGroupRecord> { group },
            new List<int>(),
            new List<(int, int)>(),
            new List<(string, int, bool)> { ("tmdb:movie:157336", 404, true) },
            new List<(string, string, int)> { ("srv1", "trakt_mov", 404) }
        );

        // User data origin is marked as 'trakt' (e.g. pulled from Trakt watched history)
        _db.UpdateUserDataState("srv1", "trakt_mov", positionTicks: 0, runtimeTicks: null, played: true, lastPlayedUtc: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), origin: "trakt");

        _scrobbler.Start(404, 50.0);

        Assert.Equal(1, _db.GetTraktQueueCount());
    }
}
