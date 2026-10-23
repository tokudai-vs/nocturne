using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// #24 (T5), spec D7: a change is never propagated to the peer named in its own origin, enforced in one place (the
// queues ask Origin), and every user_data write records its origin.
public class OriginTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
    private readonly DatabaseService _db;

    public OriginTests() => _db = new DatabaseService(_path);

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void TheRule()
    {
        Assert.False(Origin.WritesToTrakt(Origin.Trakt));
        Assert.True(Origin.WritesToTrakt(Origin.Local));
        Assert.True(Origin.WritesToTrakt(Origin.Emby("A")));
        Assert.False(Origin.WritesTo(Origin.Emby("A"), "A"));
        Assert.True(Origin.WritesTo(Origin.Emby("A"), "B"));
        Assert.True(Origin.WritesTo(Origin.Trakt, "A"));
        Assert.True(Origin.WritesTo(Origin.Local, "A"));
    }

    // The spec's gate: origin 'trakt' produces no Trakt write; origin 'emby:A' writes to B and Trakt, but not A.
    [Fact]
    public void TheQueues_FollowTheRule()
    {
        _db.EnqueueTraktEvent(1, "stop", 100, "{}", Origin.Trakt);
        Assert.Equal(0, _db.GetTraktQueueCount());

        foreach (var server in new[] { "A", "B" }) _db.EnqueueSiblingPlaybackWrite(server, "item", 0, markPlayed: true, Origin.Emby("A"));
        _db.EnqueueTraktEvent(1, "stop", 100, "{}", Origin.Emby("A"));

        Assert.Equal(new[] { "B" }, _db.GetPendingSiblingWrites().Select(w => w.ServerId));
        Assert.Equal(1, _db.GetTraktQueueCount());

        foreach (var server in new[] { "A", "B" }) _db.EnqueueSiblingPlaybackWrite(server, "pulled", 0, markPlayed: true, Origin.Trakt);
        Assert.Equal(new[] { "A", "B" }, _db.GetPendingSiblingWrites().Where(w => w.ItemId == "pulled").Select(w => w.ServerId).OrderBy(s => s));
    }

    [Fact]
    public void EachWrite_RecordsItsOrigin()
    {
        var watched = new EmbyItem
        {
            Id = "e", ServerId = "A", Name = "E", Type = "Episode",
            UserData = new UserItemData { Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-1) },
        };
        _db.UpsertItem(watched, "A"); // a sync reading the server's state
        Assert.Equal("emby:A", _db.GetUserDataOrigin("A", "e"));

        _db.UpdateLocalPlaybackProgress("A", "e", 600 * 10_000_000L); // playback in Nocturne
        Assert.Equal("local", _db.GetUserDataOrigin("A", "e"));

        _db.UpdateUserDataState("A", "e", 0, null, played: true, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Origin.Trakt); // the pull
        Assert.Equal("trakt", _db.GetUserDataOrigin("A", "e"));

        _db.MarkLocalItemPlayed("A", "e");
        Assert.Equal("local", _db.GetUserDataOrigin("A", "e"));
    }
}
