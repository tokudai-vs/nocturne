using Nocturne.Core.Models;

namespace Nocturne.Core.Services;

// Spec §3.3 (A0), the one place an image URL is chosen for a card, hero or detail page:
//   1. the version the dedup group elected (DatabaseService.AttachGroupArtwork), from that version's own server
//   2. TMDB's image for the group
//   3. nothing: the caller draws the placeholder
// Never the active server: building URLs from it was the audit bug (AUDIT-2026-07), masked by the dedup fallback.
// An item whose group was not found (a season, a box set, an uncached item) shows its own image from its own server.
public static class Artwork
{
    public const string TmdbImageBase = "https://image.tmdb.org/t/p/";

    public static string? Poster(EmbyItem item, IReadOnlyList<ServerConfig> servers, int width)
    {
        var (server, itemId, tag) = item.ArtworkAttached
            ? (item.ImageServerId, item.ImageItemId, item.ElectedImageTag)
            : (item.ServerId, item.Id, item.ImageTag);
        return Url(servers, server, itemId, "Primary", tag, width)
               ?? Tmdb(item.TmdbPosterPath, IsEpisode(item) ? "w300" : width <= 342 ? "w342" : "w780"); // §10.7: stills come in w300
    }

    public static string? Backdrop(EmbyItem item, IReadOnlyList<ServerConfig> servers, int width)
    {
        var (server, itemId, tag) = item.ArtworkAttached
            ? (item.ArtworkBackdropServerId, item.ArtworkBackdropItemId, item.ElectedBackdropTag)
            : (item.ServerId, item.Id, item.BackdropTag);
        return Url(servers, server, itemId, "Backdrop", tag, width)
               ?? Tmdb(item.TmdbBackdropPath, width <= 780 ? "w780" : "w1280");
    }

    // Attaches each item's group artwork and sets the URL a card binds to (MediaCard reads PrimaryImageUrl).
    public static void Apply(IList<EmbyItem> items, DatabaseService db, IReadOnlyList<ServerConfig> servers, int width = 320)
    {
        db.AttachGroupArtwork(items);
        foreach (var item in items) item.PrimaryImageUrl = Poster(item, servers, width);
    }

    private static string? Url(IReadOnlyList<ServerConfig> servers, string? serverId, string? itemId, string type, string? tag, int width)
    {
        if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(serverId)) return null;
        var server = servers.FirstOrDefault(s => s.Id == serverId);
        return server == null || string.IsNullOrEmpty(server.Url) ? null : EmbyClient.ImageUrl(server.Url, server.AccessToken, itemId, type, width, tag);
    }

    private static bool IsEpisode(EmbyItem item) => string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase);

    private static string? Tmdb(string? path, string size) => string.IsNullOrEmpty(path) ? null : $"{TmdbImageBase}{size}{path}";
}
