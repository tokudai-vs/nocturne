using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// Spec §3 (A0): a card shows its dedup group's elected artwork, from the electing version's own server.
public class ArtworkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
    private readonly DatabaseService _db;
    private static readonly List<ServerConfig> Servers = new()
    {
        new ServerConfig { Id = "a", Name = "A", Url = "http://a.test", AccessToken = "ta" },
        new ServerConfig { Id = "b", Name = "B", Url = "http://b.test", AccessToken = "tb" },
    };

    public ArtworkTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new DatabaseService(Path.Combine(_dir, "a.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // The primary (more metadata: a backdrop and an overview) has no poster; its sibling on B has one.
    private static EmbyItem Primary(bool poster = false) => new()
    {
        Id = "p", ServerId = "a", Name = "Arrival", Type = "Movie", Overview = "Linguist", ProviderIds = new() { ["Tmdb"] = "329865" },
        BackdropImageTags = new() { "bp" }, ImageTags = poster ? new() { ["Primary"] = "pp" } : null,
    };

    private static EmbyItem Sibling(bool poster = true) => new()
    {
        Id = "s", ServerId = "b", Name = "Arrival", Type = "Movie", ProviderIds = new() { ["Tmdb"] = "329865" },
        ImageTags = poster ? new() { ["Primary"] = "sp" } : null,
    };

    [Fact]
    public void APrimaryWithoutAPoster_ShowsItsSiblings_FromTheSiblingsServer()
    {
        var (p, s) = (Primary(), Sibling());
        foreach (var it in new[] { p, s }) _db.UpsertItem(it, it.ServerId!, "lib");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { p, s }, _db);

        var card = Assert.Single(_db.GetCombinedLibraryItems(new[] { "lib" })); // a library shelf shows the primary
        Assert.Equal("p", card.Id);

        var url = Artwork.Poster(card, Servers, 320);
        Assert.StartsWith("http://b.test/Items/s/Images/Primary", url);
        Assert.Contains("Tag=sp", url);
        Assert.Contains("api_key=tb", url);
        Assert.StartsWith("http://a.test/Items/p/Images/Backdrop", Artwork.Backdrop(card, Servers, 1920)); // the primary's own
    }

    [Fact]
    public void AnItemFetchedLive_GetsItsGroupsArtwork_AndNeverTheActiveServer()
    {
        var (p, s) = (Primary(), Sibling());
        foreach (var it in new[] { p, s }) _db.UpsertItem(it, it.ServerId!, "lib");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { p, s }, _db);

        var live = new List<EmbyItem> { new() { Id = "p", ServerId = "a", Name = "Arrival", Type = "Movie" } }; // as a server returns it
        Artwork.Apply(live, _db, Servers);
        Assert.StartsWith("http://b.test/Items/s/", live[0].PrimaryImageUrl);

        var unknownServer = new EmbyItem { Id = "x", ServerId = "gone", Name = "X", Type = "Movie", ImageTags = new() { ["Primary"] = "t" } };
        Assert.Null(Artwork.Poster(unknownServer, Servers, 320)); // the placeholder, not a guess at another server
    }

    // A poster gone from the version that held it: the group re-elects (here: none left), so it becomes TMDB-eligible
    // instead of keeping a stale tag.
    [Fact]
    public void WhenTheHolderLosesItsPoster_TheGroupReelects()
    {
        var (p, s) = (Primary(), Sibling());
        foreach (var it in new[] { p, s }) _db.UpsertItem(it, it.ServerId!, "lib");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { p, s }, _db);

        var again = Sibling(poster: false);
        _db.UpsertItem(again, "b", "lib");
        new ConcurrentDedupIndex().ProcessBatch(new List<EmbyItem> { again }, _db);

        var card = Assert.Single(_db.GetCombinedLibraryItems(new[] { "lib" }));
        Assert.Null(card.ElectedImageTag);
        Assert.Null(Artwork.Poster(card, Servers, 320));
    }
}
