using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class TraktScrobbleService : ITraktScrobbleService
{
    private readonly DatabaseService _db;
    private readonly ITraktAuthService _auth;
    private int _skippedProvisionalCount = 0;

    public int SkippedProvisionalCount => _skippedProvisionalCount;

    public TraktScrobbleService(DatabaseService db, ITraktAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    public void Start(long groupId, double progress) => EnqueueScrobble(groupId, "start", progress);
    public void Pause(long groupId, double progress) => EnqueueScrobble(groupId, "pause", progress);
    public void Stop(long groupId, double progress)  => EnqueueScrobble(groupId, "stop", progress);

    private void EnqueueScrobble(long groupId, string action, double progress)
    {
        // Must be authenticated or connecting to Trakt
        if (_auth.State == TraktConnectionState.Disconnected)
            return;

        var group = _db.GetDedupGroup(groupId);
        if (group == null)
            return;

        // Rule: Provisional groups cannot be scrobbled (§7.3)
        if (group.IsProvisional == 1)
        {
            _skippedProvisionalCount++;
            return;
        }

        // Rule: D7 origin skip. If last played came from Trakt, do not echo back to Trakt
        var origin = _db.GetUserDataOrigin(group.PrimaryServerId, group.PrimaryItemId);
        if (string.Equals(origin, "trakt", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var payload = BuildScrobblePayload(group, progress);
        if (payload == null)
        {
            _skippedProvisionalCount++;
            return;
        }

        _db.EnqueueTraktEvent(groupId, action, progress, payload);
        NocturneLogger.Info("TraktScrobbleService", $"Enqueued Trakt scrobble event: {action} (group: {groupId}, progress: {progress:F1}%)");
    }

    public string? BuildScrobblePayload(DedupGroupRecord group, double progress)
    {
        double clampedProgress = Math.Clamp(progress, 0.0, 100.0);
        var isEpisode = string.Equals(group.ItemType, "Episode", StringComparison.OrdinalIgnoreCase);

        // Fetch authoritative keys for this group
        var authKeys = GetAuthoritativeKeys(group.GroupId);

        if (isEpisode)
        {
            int season = group.SeasonNumber ?? 1;
            int episode = group.EpisodeNumber ?? 0;

            var showIds = new Dictionary<string, object>();
            var episodeIds = new Dictionary<string, object>();

            foreach (var key in authKeys)
            {
                // ep:tvdb:{showId}:s{s}e{e}
                var mTvdb = Regex.Match(key, @"^ep:tvdb:(\d+):s(\d+)e(\d+)$", RegexOptions.IgnoreCase);
                if (mTvdb.Success)
                {
                    if (int.TryParse(mTvdb.Groups[1].Value, out int tvdbId))
                        showIds["tvdb"] = tvdbId;
                    if (int.TryParse(mTvdb.Groups[2].Value, out int s)) season = s;
                    if (int.TryParse(mTvdb.Groups[3].Value, out int e)) episode = e;
                    continue;
                }

                // ep:tmdb:{showId}:s{s}e{e}
                var mTmdb = Regex.Match(key, @"^ep:tmdb:(\d+):s(\d+)e(\d+)$", RegexOptions.IgnoreCase);
                if (mTmdb.Success)
                {
                    if (int.TryParse(mTmdb.Groups[1].Value, out int tmdbId))
                        showIds["tmdb"] = tmdbId;
                    if (int.TryParse(mTmdb.Groups[2].Value, out int s)) season = s;
                    if (int.TryParse(mTmdb.Groups[3].Value, out int e)) episode = e;
                    continue;
                }

                // ep:imdb:{imdbId}:s{s}e{e}
                var mImdb = Regex.Match(key, @"^ep:imdb:(tt\d+):s(\d+)e(\d+)$", RegexOptions.IgnoreCase);
                if (mImdb.Success)
                {
                    showIds["imdb"] = mImdb.Groups[1].Value;
                    if (int.TryParse(mImdb.Groups[2].Value, out int s)) season = s;
                    if (int.TryParse(mImdb.Groups[3].Value, out int e)) episode = e;
                    continue;
                }

                // Direct episode keys: tmdb:ep:{id}, imdb:ep:{id}, tvdb:ep:{id}
                if (key.StartsWith("tmdb:ep:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(key.Substring(8), out int epTmdb))
                        episodeIds["tmdb"] = epTmdb;
                }
                else if (key.StartsWith("imdb:ep:", StringComparison.OrdinalIgnoreCase))
                {
                    episodeIds["imdb"] = key.Substring(8);
                }
                else if (key.StartsWith("tvdb:ep:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(key.Substring(8), out int epTvdb))
                        episodeIds["tvdb"] = epTvdb;
                }
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
            // Movie
            var movieIds = new Dictionary<string, object>();

            foreach (var key in authKeys)
            {
                if (key.StartsWith("tmdb:movie:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(key.Substring(11), out int tmdbId))
                        movieIds["tmdb"] = tmdbId;
                }
                else if (key.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(key.Substring(5), out int tmdbId))
                        movieIds["tmdb"] = tmdbId;
                }
                else if (key.StartsWith("imdb:movie:", StringComparison.OrdinalIgnoreCase))
                {
                    movieIds["imdb"] = key.Substring(11);
                }
                else if (key.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
                {
                    movieIds["imdb"] = key.Substring(5);
                }
                else if (key.StartsWith("tvdb:movie:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(key.Substring(11), out int tvdbId))
                        movieIds["tvdb"] = tvdbId;
                }
                else if (key.StartsWith("tvdb:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(key.Substring(5), out int tvdbId))
                        movieIds["tvdb"] = tvdbId;
                }
            }

            if (movieIds.Count == 0 && !string.IsNullOrEmpty(group.TmdbKey) && group.TmdbKey.StartsWith("tmdb:movie:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(group.TmdbKey.Substring(11), out int tmdbId))
                    movieIds["tmdb"] = tmdbId;
            }

            if (movieIds.Count == 0)
                return null;

            var payloadObj = new Dictionary<string, object>
            {
                ["progress"] = clampedProgress,
                ["movie"] = new Dictionary<string, object>
                {
                    ["title"] = group.Name,
                    ["year"] = (object?)group.Year ?? DBNull.Value,
                    ["ids"] = movieIds
                }
            };

            return JsonSerializer.Serialize(payloadObj);
        }
    }

    private List<string> GetAuthoritativeKeys(int groupId)
    {
        var keys = new List<string>();
        try
        {
            var (_, allKeys, _) = _db.GetDedupStateForIndex();
            foreach (var (k, gid, isAuth) in allKeys)
            {
                if (gid == groupId && isAuth)
                {
                    keys.Add(k);
                }
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("TraktScrobbleService", $"GetAuthoritativeKeys failed for group {groupId}: {ex.Message}");
        }
        return keys;
    }
}
