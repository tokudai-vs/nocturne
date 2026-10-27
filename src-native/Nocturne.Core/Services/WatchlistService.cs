using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

// Spec §9.3: the watchlist's only writes are the user's own, made in Nocturne, and they go to both peers: a favourite
// on every server hosting the title and, with Trakt connected, its Trakt watchlist. Optimistic: each entry is written
// at once with its pending_op, which hides a remove immediately; DrainFavouritesAsync and the Trakt queue deliver them,
// and a pull settles them against what each source then reports (DatabaseService.ReplaceTraktWatchlist, the sync).
public class WatchlistService : IWatchlistService
{
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
            foreach (var key in keys) _db.SetWatchlistPendingOp("trakt", key.ToString(), "remove");
        if (_auth.State == TraktConnectionState.Connected && keys.Count > 0)
        {
            if (op == "add") _db.UpsertWatchlistEntry("trakt", keys[0].ToString(), group.ItemType, now, group.Name, group.Year, group.TmdbKey, "add");
            _db.EnqueueTraktEvent(groupId, $"watchlist_{op}", 0, Payload(group.ItemType, group.Name, group.Year, keys), Origin.Local);
        }

        NocturneLogger.Info("WatchlistService", $"Watchlist {op}: group {groupId} ('{group.Name}').");
        return Task.CompletedTask;
    }

    // A title not in the library lives on Trakt only. titleKey is its ref, a movie or series ProviderKey.
    public Task AddUnownedAsync(string titleKey, string title, int? year, string itemType, CancellationToken ct)
    {
        if (_auth.State != TraktConnectionState.Connected || TitleKey(titleKey) is not { } key) return Task.CompletedTask;
        _db.UpsertWatchlistEntry("trakt", titleKey, itemType, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), title, year, key.TmdbKey, "add");
        _db.EnqueueTraktEvent(0, "watchlist_add", 0, Payload(itemType, title, year, new[] { key }), Origin.Local);
        NocturneLogger.Info("WatchlistService", $"Watchlist add: '{title}' ({titleKey}), not in the library.");
        return Task.CompletedTask;
    }

    public Task RemoveUnownedAsync(string titleKey, CancellationToken ct)
    {
        _db.SetWatchlistPendingOp("trakt", titleKey, "remove");
        if (_auth.State == TraktConnectionState.Connected && TitleKey(titleKey) is { } key)
            _db.EnqueueTraktEvent(0, "watchlist_remove", 0, Payload(key.Kind == KeyKind.Series ? "Series" : "Movie", null, null, new[] { key }), Origin.Local);
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

    // The group's movie/series keys, TMDB first (the order TraktSyncService picks a pulled entry's ref in).
    private List<ProviderKey> TitleKeys(long groupId) => _db.GetAuthoritativeKeys((int)groupId)
        .Select(TitleKey).OfType<ProviderKey>().OrderBy(k => k.Provider).ToList();

    private static ProviderKey? TitleKey(string key) =>
        ProviderKey.TryParse(key, out var k) && k.Kind is KeyKind.Movie or KeyKind.Series ? k : null;

    // /sync/watchlist[/remove] body with every id the title has (D4/D5); title and year only when known.
    private static string Payload(string itemType, string? title, int? year, IEnumerable<ProviderKey> keys)
    {
        var ids = new Dictionary<string, object>();
        foreach (var key in keys) ids.TryAdd(key.IdName, key.TraktId);
        var entry = new Dictionary<string, object> { ["ids"] = ids };
        if (!string.IsNullOrEmpty(title)) entry["title"] = title;
        if (year is { } y) entry["year"] = y;
        var list = string.Equals(itemType, "Series", StringComparison.OrdinalIgnoreCase) ? "shows" : "movies";
        return JsonSerializer.Serialize(new Dictionary<string, object> { [list] = new[] { entry } });
    }
}
