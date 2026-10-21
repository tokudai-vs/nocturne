using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

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

    public Task AddAsync(long groupId, CancellationToken ct)
    {
        var group = _db.GetDedupGroup(groupId);
        if (group == null) return Task.CompletedTask;

        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 1. Optimistic write for Emby primary server
        _db.UpsertWatchlistEntry(
            source: group.PrimaryServerId,
            @ref: group.PrimaryItemId,
            itemType: group.ItemType,
            addedUtc: nowSec,
            title: group.Name,
            year: group.Year,
            tmdbKey: group.TmdbKey,
            pendingOp: "add"
        );

        // 2. Trakt write if Trakt connected
        if (_auth.State == TraktConnectionState.Connected)
        {
            var payload = BuildWatchlistPayload(group);
            if (!string.IsNullOrEmpty(payload))
            {
                if (!string.IsNullOrEmpty(group.TmdbKey))
                {
                    _db.UpsertWatchlistEntry(
                        source: "trakt",
                        @ref: group.TmdbKey,
                        itemType: group.ItemType,
                        addedUtc: nowSec,
                        title: group.Name,
                        year: group.Year,
                        tmdbKey: group.TmdbKey,
                        pendingOp: "add"
                    );
                }

                _db.EnqueueTraktEvent(groupId, "watchlist_add", 0, payload);
            }
        }

        NocturneLogger.Info("WatchlistService", $"Added group {groupId} ('{group.Name}') to watchlist.");
        return Task.CompletedTask;
    }

    public Task RemoveAsync(long groupId, CancellationToken ct)
    {
        var group = _db.GetDedupGroup(groupId);
        if (group == null) return Task.CompletedTask;

        // 1. Optimistic mark 'remove' on local entries (§9.3)
        _db.SetWatchlistPendingOp(group.PrimaryServerId, group.PrimaryItemId, "remove");
        if (!string.IsNullOrEmpty(group.TmdbKey))
        {
            _db.SetWatchlistPendingOp("trakt", group.TmdbKey, "remove");
        }

        // 2. Trakt queue write
        if (_auth.State == TraktConnectionState.Connected)
        {
            var payload = BuildWatchlistPayload(group);
            if (!string.IsNullOrEmpty(payload))
            {
                _db.EnqueueTraktEvent(groupId, "watchlist_remove", 0, payload);
            }
        }

        NocturneLogger.Info("WatchlistService", $"Marked group {groupId} ('{group.Name}') removed from watchlist.");
        return Task.CompletedTask;
    }

    public Task AddUnownedAsync(string tmdbKey, string title, int? year, string itemType, CancellationToken ct)
    {
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _db.UpsertWatchlistEntry(
            source: "trakt",
            @ref: tmdbKey,
            itemType: itemType,
            addedUtc: nowSec,
            title: title,
            year: year,
            tmdbKey: tmdbKey,
            pendingOp: "add"
        );

        if (_auth.State == TraktConnectionState.Connected)
        {
            var payload = BuildUnownedPayload(tmdbKey, title, year, itemType);
            if (!string.IsNullOrEmpty(payload))
            {
                _db.EnqueueTraktEvent(0, "watchlist_add", 0, payload);
            }
        }

        NocturneLogger.Info("WatchlistService", $"Added unowned title '{title}' ({tmdbKey}) to watchlist.");
        return Task.CompletedTask;
    }

    private string? BuildWatchlistPayload(DedupGroupRecord group)
    {
        bool isShow = string.Equals(group.ItemType, "Series", StringComparison.OrdinalIgnoreCase);
        var ids = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(group.TmdbKey))
        {
            var m = Regex.Match(group.TmdbKey, @"^tmdb:(?:movie|tv):(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int tmdbId))
            {
                ids["tmdb"] = tmdbId;
            }
        }

        if (ids.Count == 0) return null;

        if (isShow)
        {
            var showObj = new Dictionary<string, object>
            {
                ["shows"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["title"] = group.Name,
                        ["year"] = (object?)group.Year ?? DBNull.Value,
                        ["ids"] = ids
                    }
                }
            };
            return JsonSerializer.Serialize(showObj);
        }
        else
        {
            var movieObj = new Dictionary<string, object>
            {
                ["movies"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["title"] = group.Name,
                        ["year"] = (object?)group.Year ?? DBNull.Value,
                        ["ids"] = ids
                    }
                }
            };
            return JsonSerializer.Serialize(movieObj);
        }
    }

    private string? BuildUnownedPayload(string tmdbKey, string title, int? year, string itemType)
    {
        bool isShow = string.Equals(itemType, "Series", StringComparison.OrdinalIgnoreCase);
        var ids = new Dictionary<string, object>();

        var m = Regex.Match(tmdbKey, @"^tmdb:(?:movie|tv):(\d+)$", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int tmdbId))
        {
            ids["tmdb"] = tmdbId;
        }

        if (ids.Count == 0) return null;

        if (isShow)
        {
            var showObj = new Dictionary<string, object>
            {
                ["shows"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["title"] = title,
                        ["year"] = (object?)year ?? DBNull.Value,
                        ["ids"] = ids
                    }
                }
            };
            return JsonSerializer.Serialize(showObj);
        }
        else
        {
            var movieObj = new Dictionary<string, object>
            {
                ["movies"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["title"] = title,
                        ["year"] = (object?)year ?? DBNull.Value,
                        ["ids"] = ids
                    }
                }
            };
            return JsonSerializer.Serialize(movieObj);
        }
    }
}
