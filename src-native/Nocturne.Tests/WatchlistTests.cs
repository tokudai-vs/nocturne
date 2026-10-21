using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class WatchlistTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseService _db;

    public WatchlistTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"watchlist_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseService(_tempDbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }

    [Fact]
    public void MergedWatchlist_RollsUpByGroup_AndAppliesPendingRemoveFilter()
    {
        // 1. Create a dedup group
        var group = new DedupGroupRecord
        {
            GroupId = 601,
            ItemType = "Movie",
            PrimaryItemId = "mov_gladiator",
            PrimaryServerId = "srv1",
            VersionCount = 1,
            Name = "Gladiator",
            SortName = "Gladiator",
            Year = 2000,
            CommunityRating = 8.5f,
            MaxHeight = 1080,
            MaxBitrate = 8000000,
            IsProvisional = 0,
            ImageItemId = "mov_gladiator",
            ImageServerId = "srv1",
            TmdbKey = "tmdb:movie:98"
        };

        _db.UpsertItems(new List<EmbyItem>
        {
            new() { Id = "mov_gladiator", Name = "Gladiator", Type = "Movie", ProductionYear = 2000, ServerId = "srv1" }
        }, "srv1");

        _db.SaveDedupBatch(
            new List<DedupGroupRecord> { group },
            new List<int>(),
            new List<(int, int)>(),
            new List<(string, int, bool)> { ("tmdb:movie:98", 601, true) },
            new List<(string, string, int)> { ("srv1", "mov_gladiator", 601) }
        );

        // 2. Add an Emby favorite entry
        _db.UpsertWatchlistEntry("srv1", "mov_gladiator", "Movie", 1000, "Gladiator", 2000, "tmdb:movie:98");

        // 3. Add a Trakt watchlist entry for the same item
        _db.UpsertWatchlistEntry("trakt", "tmdb:movie:98", "Movie", 2000, "Gladiator", 2000, "tmdb:movie:98");

        // Merged query should union both into a single distinct group row
        var cards = _db.GetMergedWatchlist();
        Assert.Single(cards);
        Assert.Equal("Gladiator", cards[0].Title);
        Assert.True(cards[0].IsInLibrary);
        Assert.Equal(601, cards[0].GroupId);

        // 4. Mark pending_op = 'remove' on one entry
        _db.SetWatchlistPendingOp("srv1", "mov_gladiator", "remove");
        _db.SetWatchlistPendingOp("trakt", "tmdb:movie:98", "remove");

        // Merged query MUST filter out rows with pending_op = 'remove' (§9.2, D10)
        var filteredCards = _db.GetMergedWatchlist();
        Assert.Empty(filteredCards);
    }

    [Fact]
    public void MergedWatchlist_IncludesUnownedItems()
    {
        // Unowned Trakt watchlist entry (group_id = NULL)
        _db.UpsertWatchlistEntry("trakt", "tmdb:12345", "Movie", 5000, "Unreleased Sci-Fi", 2026, "tmdb:movie:12345");

        var cards = _db.GetMergedWatchlist();
        Assert.Single(cards);
        Assert.Equal("Unreleased Sci-Fi", cards[0].Title);
        Assert.False(cards[0].IsInLibrary);
        Assert.Equal(0, cards[0].GroupId);
    }
}
