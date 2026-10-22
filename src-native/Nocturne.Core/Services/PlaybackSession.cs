using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public static class FeatureFlags
{
    public const string UnsharedRenumberedGroupsKey = "dedup_unshared_renumbered_groups";

    // Sibling cascade: a completion / resume / Remove-from-CW on one version is also written to every other
    // version in its dedup group (locally and, via sibling_write_queue, on the other servers). Engaged only once
    // the groups were built by the conflict-guarded engine (a rebuild, or a cache populated from empty): groups
    // left by the earlier engine are over-merged, and one completion would rewrite unrelated titles. It also stays
    // off while the last measurement found an episode group in which one server holds two items under different
    // (season, episode) sharing no episode id: a season/episode over-merge the conflict guard cannot see.
    public static bool SiblingCascade(DatabaseService db) =>
        db.GetMigrationState(ConcurrentDedupIndex.EngineMarkerKey) == ConcurrentDedupIndex.EngineVersion
        && db.GetMigrationState(UnsharedRenumberedGroupsKey) == "0";

    // Run after every sync and rebuild, inside DedupEngine.WriterGate.
    public static void MeasureCascadeGate(DatabaseService db)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var count = db.CountUnsharedRenumberedGroups();
        db.SetMigrationState(UnsharedRenumberedGroupsKey, count?.ToString() ?? "unmeasured");
        NocturneLogger.Info("FeatureFlags", $"Cascade gate: {(count is { } n ? $"{n} renumbered episode group(s) without a shared episode id" : "measurement failed")} ({clock.ElapsedMilliseconds} ms); sibling cascade {(SiblingCascade(db) ? "on" : "off")}.");
    }
}

public enum PlaybackStopReason
{
    Stop,       // Stop button, Esc, window close, mpv quit / error
    EndOfFile,  // mpv reported a natural end of file
    Next,       // user skipped to the next episode
    Previous    // user went back to the previous episode
}

public sealed record PlaybackFinalizeResult(EmbyItem Item, long PositionTicks, double ProgressPercent, bool Completed);

// Owns the write-back state of the playing item: the position it reached, the client of the server it is
// streaming from, and what gets written locally and to the server when playback stops.
public sealed class PlaybackSession
{
    public const double CompletionThresholdPercent = 90.0;
    private const double MinResumeSeconds = 5;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromSeconds(10);

    private readonly DatabaseService _db;
    private readonly Func<bool> _siblingCascade;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();

    private EmbyItem? _item;
    private EmbyClient? _client;
    private string? _mediaSourceId;
    private double _positionSeconds;
    private double _durationSeconds;
    private DateTime _lastProgressReportUtc;

    public PlaybackSession(DatabaseService db) : this(db, () => FeatureFlags.SiblingCascade(db), null) { }

    public PlaybackSession(DatabaseService db, bool siblingCascade, Func<DateTime>? utcNow) : this(db, () => siblingCascade, utcNow) { }

    private PlaybackSession(DatabaseService db, Func<bool> siblingCascade, Func<DateTime>? utcNow)
    {
        _db = db;
        _siblingCascade = siblingCascade;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public EmbyItem? Item { get { lock (_gate) return _item; } }
    public double PositionSeconds { get { lock (_gate) return _positionSeconds; } }

    public void Begin(EmbyItem item, EmbyClient client, string? mediaSourceId, long startTicks)
    {
        lock (_gate)
        {
            _item = item;
            _client = client;
            _mediaSourceId = mediaSourceId;
            _positionSeconds = startTicks / 10_000_000.0;
            _durationSeconds = 0;
            _lastProgressReportUtc = _utcNow();
        }
    }

    public void OnDuration(double seconds)
    {
        lock (_gate) { if (_item != null) _durationSeconds = seconds; }
    }

    // Records the position; every ProgressReportInterval it also reports it to the streaming server.
    // Returns the report task when one was sent.
    public Task<bool>? OnPosition(double seconds)
    {
        lock (_gate)
        {
            if (_item == null) return null;
            _positionSeconds = seconds;
            if (_utcNow() - _lastProgressReportUtc < ProgressReportInterval) return null;
        }
        return ReportProgressAsync(isPaused: false);
    }

    public Task<bool> ReportProgressAsync(bool isPaused)
    {
        EmbyItem item; EmbyClient client; string? mediaSourceId; long ticks;
        lock (_gate)
        {
            if (_item == null || _client == null) return Task.FromResult(false);
            item = _item; client = _client; mediaSourceId = _mediaSourceId;
            ticks = ToTicks(_positionSeconds);
            _lastProgressReportUtc = _utcNow();
        }
        return client.ReportPlaybackProgressAsync(item.Id, mediaSourceId ?? item.Id, ticks, isPaused);
    }

    // Whether stopping now for this reason would count as watched.
    public bool WouldComplete(PlaybackStopReason reason)
    {
        lock (_gate)
        {
            if (_item == null) return false;
            return Measure(_item, _positionSeconds, _durationSeconds, reason).Completed;
        }
    }

    public async Task<PlaybackFinalizeResult?> FinalizeAsync(PlaybackStopReason reason)
    {
        EmbyItem item; EmbyClient client; string? mediaSourceId; double position; double duration;
        lock (_gate)
        {
            if (_item == null || _client == null) return null;
            item = _item; client = _client; mediaSourceId = _mediaSourceId;
            position = _positionSeconds; duration = _durationSeconds;
            _item = null; _client = null; _mediaSourceId = null;
        }

        var (positionTicks, runtimeTicks, progress, completed) = Measure(item, position, duration, reason);
        var serverId = item.ServerId ?? string.Empty;
        NocturneLogger.Info("PlaybackSession", $"Finalize {item.Id} on '{serverId}': reason={reason} position={positionTicks} progress={progress:F1}% completed={completed}");

        if (!await client.ReportPlaybackStoppedAsync(item.Id, mediaSourceId ?? item.Id, positionTicks))
            NocturneLogger.Warn("PlaybackSession", $"Stop report for {item.Id} was not accepted by '{serverId}'");

        var groupId = long.TryParse(item.DedupGroupId, out var gid) && gid > 0 ? gid : 0;
        var cascade = groupId > 0 && _siblingCascade();

        if (completed)
        {
            if (!await client.MarkPlayedAsync(item.Id))
                NocturneLogger.Warn("PlaybackSession", $"MarkPlayed for {item.Id} was not accepted by '{serverId}'");

            if (item.UserData != null)
            {
                item.UserData.Played = true;
                item.UserData.PlaybackPositionTicks = 0;
            }

            _db.MarkLocalItemPlayed(serverId, item.Id);
            if (cascade && groupId > 0)
            {
                _db.MarkLocalGroupPlayed(groupId);
                EnqueueOtherServerSiblings(groupId, item.ServerId, 0, markPlayed: true);
            }

            _db.RemoveFromContinueWatchingCache(item.Id);
            _db.ClearResumeProgress(item.Id, item.ServerId);
        }
        else if (position > MinResumeSeconds)
        {
            item.UserData ??= new UserItemData();
            item.UserData.PlaybackPositionTicks = positionTicks;
            item.UserData.Played = false;

            _db.UpdateLocalPlaybackProgress(serverId, item.Id, positionTicks, false, runtimeTicks > 0 ? runtimeTicks : null);
            _db.UpdatePlaybackProgress(item.Id, serverId, positionTicks);
            if (cascade && groupId > 0)
                EnqueueOtherServerSiblings(groupId, item.ServerId, positionTicks, markPlayed: false);
        }

        return new PlaybackFinalizeResult(item, positionTicks, progress, completed);
    }

    // Sends queued sibling writes. A row is removed only when its server accepted the write; failures stay
    // queued with an attempt count. clientFor returns null when the server is no longer configured.
    public async Task DrainSiblingWritesAsync(Func<string, EmbyClient?> clientFor)
    {
        foreach (var write in _db.GetPendingSiblingWrites(limit: 50))
        {
            var client = clientFor(write.ServerId);
            if (client == null)
            {
                NocturneLogger.Warn("PlaybackSession", $"Dropping sibling write for {write.ItemId}: server {write.ServerId} is no longer configured");
                _db.RemoveSiblingWrite(write.QueueId);
                continue;
            }

            bool ok;
            try
            {
                ok = write.MarkPlayed
                    ? await client.MarkPlayedAsync(write.ItemId)
                    : write.PositionTicks.HasValue && await client.SetPlaybackPositionAsync(write.ItemId, write.PositionTicks.Value);
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("PlaybackSession", $"Sibling write for {write.ItemId} on {write.ServerId} threw: {ex.Message}");
                ok = false;
            }

            if (ok) _db.RemoveSiblingWrite(write.QueueId);
            else _db.RecordSiblingWriteFailure(write.QueueId);
        }
    }

    private void EnqueueOtherServerSiblings(long groupId, string? serverId, long positionTicks, bool markPlayed)
    {
        foreach (var sib in _db.GetGroupVersions(groupId.ToString()))
        {
            if (!string.IsNullOrEmpty(sib.ServerId) && sib.ServerId != serverId)
                _db.EnqueueSiblingPlaybackWrite(sib.ServerId, sib.Id, positionTicks, markPlayed);
        }
    }

    private static (long PositionTicks, long RuntimeTicks, double Progress, bool Completed) Measure(
        EmbyItem item, double positionSeconds, double durationSeconds, PlaybackStopReason reason)
    {
        var positionTicks = ToTicks(positionSeconds);
        var runtimeTicks = item.RunTimeTicks is > 0 ? item.RunTimeTicks.Value : ToTicks(durationSeconds);
        var progress = runtimeTicks > 0 ? Math.Clamp(positionTicks * 100.0 / runtimeTicks, 0, 100) : 0;
        // Completion is decided by how far playback got, never by which path stopped it. The one exception is
        // a real end of file on an item whose length is unknown.
        var completed = progress >= CompletionThresholdPercent || (reason == PlaybackStopReason.EndOfFile && runtimeTicks <= 0);
        return (positionTicks, runtimeTicks, progress, completed);
    }

    private static long ToTicks(double seconds) => (long)(Math.Max(0, seconds) * 10_000_000.0);
}
