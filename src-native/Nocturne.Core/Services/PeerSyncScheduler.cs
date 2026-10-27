using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

// The background cycle between Nocturne and its peers: on app start, right after Trakt Connect, then every 15
// minutes; never during playback and never twice at once (spec §8.1/§8.3). Each cycle:
//   1. drains Nocturne's own outbound writes (Emby favourites, the Trakt queue), so an add made offline reaches both
//      peers on reconnect (§9.3, the T9 gate) and the pull that follows sees it;
//   2. with Trakt connected, pulls watched state and the watchlist;
//   3. drains the sibling queue, so a watch marked on trakt.tv reaches every server in the same cycle (the T6 gate).
public sealed class PeerSyncScheduler : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly ITraktSyncService _sync;
    private readonly ITraktAuthService _auth;
    private readonly Func<bool> _isPlaying;
    private readonly Func<Task> _drainOutbound;
    private readonly Func<Task<int>> _drainSiblingPage; // returns the page size; 0 when the queue is drained
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    public PeerSyncScheduler(ITraktSyncService sync, ITraktAuthService auth, Func<bool> isPlaying, Func<Task> drainOutbound, Func<Task<int>> drainSiblingPage)
    {
        _sync = sync;
        _auth = auth;
        _isPlaying = isPlaying;
        _drainOutbound = drainOutbound;
        _drainSiblingPage = drainSiblingPage;
    }

    public void Start()
    {
        _auth.StateChanged += OnStateChanged;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do await CycleAsync();
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) { }
    }

    private void OnStateChanged(object? sender, TraktConnectionState state)
    {
        if (state == TraktConnectionState.Connected) _ = CycleAsync();
    }

    // False when skipped: playing, or a cycle already running. A skipped tick waits for the next one.
    public async Task<bool> CycleAsync()
    {
        if (_isPlaying() || !_running.Wait(0)) return false;
        try
        {
            await _drainOutbound();
            if (_auth.State == TraktConnectionState.Connected) await _sync.PullWatchedAsync(_stop.Token);
            while (!_isPlaying() && !_stop.IsCancellationRequested && await _drainSiblingPage() > 0) { }
            return true;
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("PeerSyncScheduler", "Peer sync cycle failed", ex);
            return false;
        }
        finally
        {
            _running.Release();
        }
    }

    public void Dispose()
    {
        _auth.StateChanged -= OnStateChanged;
        _stop.Cancel();
    }
}
