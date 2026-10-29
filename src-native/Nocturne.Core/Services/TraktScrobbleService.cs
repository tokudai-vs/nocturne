using System;
using System.Collections.Generic;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class TraktScrobbleService : ITraktScrobbleService
{
    private readonly DatabaseService _db;
    private readonly ITraktAuthService _auth;
    private readonly SettingsService? _settings;
    private int _skippedProvisionalCount = 0;

    public int SkippedProvisionalCount => _skippedProvisionalCount;

    public TraktScrobbleService(DatabaseService db, ITraktAuthService auth, SettingsService? settings = null)
    {
        _db = db;
        _auth = auth;
        _settings = settings;
    }

    public void Start(long groupId, double progress) => EnqueueScrobble(groupId, "start", progress);
    public void Pause(long groupId, double progress) => EnqueueScrobble(groupId, "pause", progress);
    public void Stop(long groupId, double progress)  => EnqueueScrobble(groupId, "stop", progress);

    private void EnqueueScrobble(long groupId, string action, double progress)
    {
        // Must be authenticated or connecting to Trakt
        if (_auth.State == TraktConnectionState.Disconnected)
            return;

        // #43: "Auto scrobble" off in Settings: playback is not sent to Trakt.
        if (_settings?.Current.TraktAutoScrobble == false)
            return;

        var group = _db.GetDedupGroup(groupId);
        if (group == null)
            return;

        // Rule: Provisional groups cannot be scrobbled (§7.3)
        if (group.IsProvisional != 0) // fuzzy-only (1) or an untagged episode held until the sync ends (2)
        {
            _skippedProvisionalCount++;
            return;
        }

        var payload = BuildScrobblePayload(group, progress);
        if (payload == null)
        {
            _skippedProvisionalCount++;
            return;
        }

        // A scrobble is playback in Nocturne: origin local, so D7 never drops it. (It used to skip any title whose last
        // change came from a Trakt pull, which silenced re-watching anything Trakt had marked.)
        _db.EnqueueTraktEvent(groupId, action, progress, payload, Origin.Local);
        NocturneLogger.Info("TraktScrobbleService", $"Enqueued Trakt scrobble event: {action} (group: {groupId}, progress: {progress:F1}%)");
    }

    public string? BuildScrobblePayload(DedupGroupRecord group, double progress)
    {
        double clampedProgress = Math.Clamp(progress, 0.0, 100.0);
        var isEpisode = string.Equals(group.ItemType, "Episode", StringComparison.OrdinalIgnoreCase);

        // The group's authoritative keys, as the dedup index wrote them (D4: one extractor).
        var keys = _db.GetAuthoritativeKeys(group.GroupId)
            .Select(k => ProviderKey.TryParse(k, out var key) ? key : (ProviderKey?)null).OfType<ProviderKey>().ToList();

        if (isEpisode)
        {
            int season = group.SeasonNumber ?? 1;
            int episode = group.EpisodeNumber ?? 0;
            var showIds = new Dictionary<string, object>();
            var episodeIds = new Dictionary<string, object>();
            foreach (var key in keys)
            {
                if (key.Kind == KeyKind.SeasonEpisode)
                {
                    showIds[key.IdName] = key.TraktId;
                    (season, episode) = (key.SeasonNumber, key.EpisodeNumber);
                }
                else if (key.Kind == KeyKind.Episode) episodeIds[key.IdName] = key.TraktId;
            }

            if (showIds.Count == 0 && episodeIds.Count == 0)
                return null;

            var payloadObj = new Dictionary<string, object>
            {
                ["progress"] = clampedProgress,
                ["episode"] = new Dictionary<string, object>
                {
                    ["season"] = season,
                    ["number"] = episode,
                    ["ids"] = episodeIds
                }
            };

            if (showIds.Count > 0)
            {
                payloadObj["show"] = new Dictionary<string, object>
                {
                    ["title"] = group.SortName ?? group.Name,
                    ["ids"] = showIds
                };
            }

            return JsonSerializer.Serialize(payloadObj);
        }
        else
        {
            var movieIds = new Dictionary<string, object>();
            foreach (var key in keys.Where(k => k.Kind == KeyKind.Movie))
                movieIds[key.IdName] = key.TraktId;
            if (movieIds.Count == 0 && ProviderKey.TryParseTmdbKey(group.TmdbKey, out var tmdb) && tmdb.Kind == KeyKind.Movie)
                movieIds[tmdb.IdName] = tmdb.TraktId;

            if (movieIds.Count == 0)
                return null;

            var movie = new Dictionary<string, object> { ["title"] = group.Name, ["ids"] = movieIds };
            if (group.Year is { } year) movie["year"] = year; // a missing year was serialized as "year":{}
            return JsonSerializer.Serialize(new Dictionary<string, object> { ["progress"] = clampedProgress, ["movie"] = movie });
        }
    }
}
