using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // T8 gate: owned and unowned titles come back as one list, newest first, whatever their source.
    [Fact]
    public void MergedWatchlist_HasOwnedAndUnownedTogether()
    {
        var owned = new EmbyItem { Id = "glad", ServerId = "srv1", Name = "Gladiator", Type = "Movie", ProviderIds = new() { ["Tmdb"] = "98" } };
        _db.UpsertItems(new List<EmbyItem> { owned }, "srv1");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { owned }, _db);
        _db.UpsertWatchlistEntry("srv1", "glad", "Movie", 1000, "Gladiator", 2000);
        _db.UpsertWatchlistEntry("trakt", "tmdb:movie:12345", "Movie", 5000, "Unreleased Sci-Fi", 2026, "tmdb:movie:12345");

        var cards = _db.GetMergedWatchlist();
        Assert.Equal(new[] { ("Unreleased Sci-Fi", false), ("Gladiator", true) }, cards.Select(c => (c.Title, c.IsInLibrary)));
        Assert.Equal(0, cards[0].GroupId);
    }

    // T7/T9: a pull settles Nocturne's pending ops against what Trakt lists, without undoing one still in flight.
    [Fact]
    public void ATraktPull_SettlesPendingOps_AndNeverUndoesOneInFlight()
    {
        _db.UpsertWatchlistEntry("trakt", "tmdb:movie:1", "Movie", 1, "Added, delivered", pendingOp: "add");
        _db.UpsertWatchlistEntry("trakt", "tmdb:movie:2", "Movie", 1, "Added, not yet delivered", pendingOp: "add");
        _db.UpsertWatchlistEntry("trakt", "tmdb:movie:3", "Movie", 1, "Removed, not yet delivered", pendingOp: "remove");
        _db.UpsertWatchlistEntry("trakt", "tmdb:movie:4", "Movie", 1, "Removed, delivered", pendingOp: "remove");
        _db.UpsertWatchlistEntry("trakt", "tmdb:movie:5", "Movie", 1, "Removed on trakt.tv");

        _db.ReplaceTraktWatchlist(new List<(string, string, long, string?, int?, string?)>
        {
            ("tmdb:movie:1", "Movie", 1, "Added, delivered", null, null),
            ("tmdb:movie:3", "Movie", 1, "Removed, not yet delivered", null, null), // Trakt still lists it
        });

        Assert.Equal(new[] { "Added, delivered", "Added, not yet delivered" }, _db.GetMergedWatchlist().Select(c => c.Title).OrderBy(t => t));
        _db.ReplaceTraktWatchlist(new List<(string, string, long, string?, int?, string?)>
        {
            ("tmdb:movie:1", "Movie", 1, "Added, delivered", null, null),
            ("tmdb:movie:2", "Movie", 1, "Added, not yet delivered", null, null),
        });
        // Trakt now reflects both: the add of 2 and the remove of 3 landed. Dropping 1 next is a removal on trakt.tv.
        Assert.Equal(new[] { "Added, delivered", "Added, not yet delivered" }, _db.GetMergedWatchlist().Select(c => c.Title).OrderBy(t => t));
        _db.ReplaceTraktWatchlist(new List<(string, string, long, string?, int?, string?)> { ("tmdb:movie:2", "Movie", 1, "Added, not yet delivered", null, null) });
        Assert.Equal("Added, not yet delivered", Assert.Single(_db.GetMergedWatchlist()).Title);
    }
}
