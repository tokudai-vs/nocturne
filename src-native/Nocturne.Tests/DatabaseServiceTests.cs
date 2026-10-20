using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class DatabaseServiceTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseService _db;

    public DatabaseServiceTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseService(_tempDbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
        }
        catch { }
    }

    [Fact]
    public void SaveAndGetCachedContinueWatching_PersistsAndRestoresInOrder()
    {
        var items = new List<EmbyItem>
        {
            new() { Id = "ep1", Name = "Episode One", ServerId = "srvA", Type = "Episode" },
            new() { Id = "mov1", Name = "The Movie", ServerId = "srvB", Type = "Movie" }
        };

        _db.SaveContinueWatchingCache(items);

        var restored = _db.GetCachedContinueWatching();
        Assert.Equal(2, restored.Count);
        Assert.Equal("ep1", restored[0].Id);
        Assert.Equal("Episode One", restored[0].Name);
        Assert.Equal("srvA", restored[0].ServerId);
        Assert.Equal("mov1", restored[1].Id);
        Assert.Equal("The Movie", restored[1].Name);
        Assert.Equal("srvB", restored[1].ServerId);
    }

    [Fact]
    public void RemoveFromContinueWatchingCache_DeletesSpecificItem()
    {
        var items = new List<EmbyItem>
        {
            new() { Id = "item1", Name = "Item One", ServerId = "srv1" },
            new() { Id = "item2", Name = "Item Two", ServerId = "srv1" }
        };

        _db.SaveContinueWatchingCache(items);
        _db.RemoveFromContinueWatchingCache("item1");

        var restored = _db.GetCachedContinueWatching();
        Assert.Single(restored);
        Assert.Equal("item2", restored[0].Id);
    }

    [Fact]
    public void UpsertItems_WithCheckpoint_AtomicallyPersistsDataAndWatermark()
    {
        var items = new List<EmbyItem>
        {
            new() { Id = "mov_01", Name = "Movie Alpha", ServerId = "srvX", Type = "Movie" },
            new() { Id = "mov_02", Name = "Movie Beta", ServerId = "srvX", Type = "Movie" }
        };

        // Atomically insert items and commit watermark at 500 / 1000 items
        _db.UpsertItems(
            items,
            serverId: "srvX",
            parentId: "lib_movies",
            checkpointStartIndex: 500,
            checkpointTotalItems: 1000,
            checkpointIsCompleted: false);

        // Verify items were persisted
        var cached = _db.GetCachedItems(serverId: "srvX");
        Assert.Equal(2, cached.Count);

        // Verify checkpoint watermark was atomically saved in the same transaction
        var cp = _db.GetSyncCheckpoint("srvX", "lib_movies");
        Assert.NotNull(cp);
        Assert.Equal(500, cp.StartIndex);
        Assert.Equal(1000, cp.TotalItems);
        Assert.False(cp.IsCompleted);

        // Advance watermark to completion
        _db.UpsertItems(
            new List<EmbyItem> { new() { Id = "mov_03", Name = "Movie Gamma", ServerId = "srvX", Type = "Movie" } },
            serverId: "srvX",
            parentId: "lib_movies",
            checkpointStartIndex: 1000,
            checkpointTotalItems: 1000,
            checkpointIsCompleted: true);

        var cpCompleted = _db.GetSyncCheckpoint("srvX", "lib_movies");
        Assert.NotNull(cpCompleted);
        Assert.Equal(1000, cpCompleted.StartIndex);
        Assert.True(cpCompleted.IsCompleted);

        // Clear checkpoints
        _db.ClearSyncCheckpoints("srvX");
        var cpAfterReset = _db.GetSyncCheckpoint("srvX", "lib_movies");
        Assert.Null(cpAfterReset);
    }

    [Fact]
    public void SaveAndGetMediaStreamPreference_PersistsAndRestoresTracks()
    {
        _db.SaveMediaStreamPreference("series:show_123", audioTrackId: 2, audioLang: "jpn", subtitleTrackId: 1, subtitleLang: "eng");

        var pref = _db.GetMediaStreamPreference("series:show_123");
        Assert.NotNull(pref);
        Assert.Equal(2, pref.Value.AudioTrackId);
        Assert.Equal("jpn", pref.Value.AudioLanguage);
        Assert.Equal(1, pref.Value.SubtitleTrackId);
        Assert.Equal("eng", pref.Value.SubtitleLanguage);

        // Update with subtitles off
        _db.SaveMediaStreamPreference("series:show_123", audioTrackId: null, audioLang: null, subtitleTrackId: 0, subtitleLang: "off");
        var updated = _db.GetMediaStreamPreference("series:show_123");
        Assert.NotNull(updated);
        Assert.Equal(2, updated.Value.AudioTrackId); // Preserved
        Assert.Equal(0, updated.Value.SubtitleTrackId); // Updated to off
        Assert.Equal("off", updated.Value.SubtitleLanguage);
    }

    [Fact]
    public void UpdatePlaybackProgress_UpdatesContinueWatchingAndCachedItems()
    {
        var item = new EmbyItem
        {
            Id = "ep_456",
            Name = "Episode 456",
            ServerId = "srv_1",
            UserData = new UserItemData { PlaybackPositionTicks = 0, Played = false }
        };

        _db.SaveContinueWatchingCache(new List<EmbyItem> { item });

        // Update progress to 15 seconds (150,000,000 ticks)
        _db.UpdatePlaybackProgress("ep_456", "srv_1", 150_000_000);

        var restored = _db.GetCachedContinueWatching();
        Assert.Single(restored);
        Assert.NotNull(restored[0].UserData);
        Assert.Equal(150_000_000, restored[0].UserData!.PlaybackPositionTicks);
        Assert.False(restored[0].UserData!.Played);
    }
}
