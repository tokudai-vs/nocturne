using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

// Spec §9.3: the watchlist's only writes are the user's own, made in Nocturne, and they go to both peers: a favourite
// on every server hosting the title and, with Trakt connected, its Trakt watchlist. Optimistic: each entry is written
// at once with its pending_op, which hides a remove immediately; DrainFavouritesAsync and the Trakt queue deliver them,
// and a pull settles them against what each source then reports (DatabaseService.ReplaceTraktWatchlist, the sync).
public class WatchlistService : IWatchlistService
{
    // A title's provider key as the dedup engine writes it: 'tmdb:movie:550', 'tvdb:series:81189'.
    private static readonly Regex TitleKey = new(@"^(tmdb|imdb|tvdb):(movie|series):(.+)$", RegexOptions.IgnoreCase);
    private static readonly string[] ProviderOrder = { "tmdb", "imdb", "tvdb" }; // as TraktSyncService picks a pulled entry's ref

    private readonly DatabaseService _db;
    private readonly ITraktAuthService _auth;

    public WatchlistService(DatabaseService db, ITraktAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    public Task<IReadOnlyList<WatchlistCard>> GetAsync(CancellationToken ct)
    {
        var list = _db.GetMergedWatchlist();
        return Task.FromResult<IReadOnlyList<WatchlistCard>>(list);
    }

    public Task AddAsync(long groupId, CancellationToken ct) => WriteAsync(groupId, "add");

    // Every hosting server gets a remove, also where the sync has not seen the favourite yet: a server without it
    // answers a no-op, and one with it cannot bring the title back.
    public Task RemoveAsync(long groupId, CancellationToken ct) => WriteAsync(groupId, "remove");

    private Task WriteAsync(long groupId, string op)
    {
        var group = _db.GetDedupGroup(groupId);
        if (group == null || !IsTitle(group.ItemType)) return Task.CompletedTask;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var (srv, id) in _db.GetGroupItemIds((int)groupId))
            _db.UpsertWatchlistEntry(srv, id, group.ItemType, now, group.Name, group.Year, group.TmdbKey, op);

        var keys = TitleKeys(groupId);
        if (op == "remove")
            foreach (var key in keys) _db.SetWatchlistPendingOp("trakt", key, "remove");
        if (_auth.State == TraktConnectionState.Connected && keys.Count > 0)
        {
            if (op == "add") _db.UpsertWatchlistEntry("trakt", keys[0], group.ItemType, now, group.Name, group.Year, group.TmdbKey, "add");
            _db.EnqueueTraktEvent(groupId, $"watchlist_{op}", 0, Payload(group.ItemType, group.Name, group.Year, keys), Origin.Local);
        }

        NocturneLogger.Info("WatchlistService", $"Watchlist {op}: group {groupId} ('{group.Name}').");
        return Task.CompletedTask;
    }

    // A title not in the library lives on Trakt only. titleKey is its ref: 'tmdb:movie:N' | 'tmdb:series:N'.
    public Task AddUnownedAsync(string titleKey, string title, int? year, string itemType, CancellationToken ct)
    {
        if (_auth.State != TraktConnectionState.Connected || !TitleKey.IsMatch(titleKey)) return Task.CompletedTask;
        _db.UpsertWatchlistEntry("trakt", titleKey, itemType, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), title, year, TmdbArtworkKey(titleKey), "add");
        _db.EnqueueTraktEvent(0, "watchlist_add", 0, Payload(itemType, title, year, new[] { titleKey }), Origin.Local);
        NocturneLogger.Info("WatchlistService", $"Watchlist add: '{title}' ({titleKey}), not in the library.");
        return Task.CompletedTask;
    }

    public Task RemoveUnownedAsync(string titleKey, CancellationToken ct)
    {
        _db.SetWatchlistPendingOp("trakt", titleKey, "remove");
        var m = TitleKey.Match(titleKey);
        if (_auth.State == TraktConnectionState.Connected && m.Success)
        {
            var itemType = m.Groups[2].Value.Equals("series", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie";
            _db.EnqueueTraktEvent(0, "watchlist_remove", 0, Payload(itemType, null, null, new[] { titleKey }), Origin.Local);
        }
        return Task.CompletedTask;
    }

    // Delivers the favourite writes to each server. A server that answers settles its row; one that cannot be reached
    // keeps it for the next drain, as a transport failure never reverts (§9.3). Drains may overlap: a favourite write
    // is idempotent, and a settle only clears the op it delivered.
    // ponytail: no revert on a definitive rejection (ToggleFavoriteAsync reports only success); add one if servers reject.
    public async Task DrainFavouritesAsync(Func<string, EmbyClient?> clientFor)
    {
        foreach (var (server, itemId, op) in _db.GetPendingFavouriteWrites())
        {
            var client = clientFor(server);
            if (client == null)
            {
                _db.RemoveWatchlistEntry(server, itemId); // the server is no longer configured
                continue;
            }
            if (await client.ToggleFavoriteAsync(itemId, op == "add")) _db.SettleWatchlistOp(server, itemId, op);
        }
    }

    private static bool IsTitle(string? itemType) =>
        string.Equals(itemType, "Movie", StringComparison.OrdinalIgnoreCase) || string.Equals(itemType, "Series", StringComparison.OrdinalIgnoreCase);

    private List<string> TitleKeys(long groupId) => _db.GetAuthoritativeKeys((int)groupId)
        .Where(k => TitleKey.IsMatch(k))
        .OrderBy(k => Array.IndexOf(ProviderOrder, k.Split(':')[0].ToLowerInvariant()))
        .ToList();

    // tmdb_metadata keys a show 'tmdb:tv:N' (§4.3).
    private static string? TmdbArtworkKey(string titleKey)
    {
        var m = TitleKey.Match(titleKey);
        if (!m.Success || !m.Groups[1].Value.Equals("tmdb", StringComparison.OrdinalIgnoreCase)) return null;
        return m.Groups[2].Value.Equals("series", StringComparison.OrdinalIgnoreCase) ? $"tmdb:tv:{m.Groups[3].Value}" : titleKey;
    }

    // /sync/watchlist[/remove] body with every id the title has (D4/D5); title and year only when known.
    private static string Payload(string itemType, string? title, int? year, IEnumerable<string> keys)
    {
        var ids = new Dictionary<string, object>();
        foreach (var key in keys)
        {
            var m = TitleKey.Match(key);
            if (!m.Success) continue;
            var provider = m.Groups[1].Value.ToLowerInvariant();
            if (ids.ContainsKey(provider)) continue;
            var id = m.Groups[3].Value;
            ids[provider] = provider != "imdb" && int.TryParse(id, out var n) ? n : id;
        }
        var entry = new Dictionary<string, object> { ["ids"] = ids };
        if (!string.IsNullOrEmpty(title)) entry["title"] = title;
        if (year is { } y) entry["year"] = y;
        var list = string.Equals(itemType, "Series", StringComparison.OrdinalIgnoreCase) ? "shows" : "movies";
        return JsonSerializer.Serialize(new Dictionary<string, object> { [list] = new[] { entry } });
    }
}
