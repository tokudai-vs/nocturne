using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

// Spec §8.1/§8.3: the watched pull runs on app start, right after Connect, then every 15 minutes; never during
// playback and never twice at once. Each pull is followed by draining the sibling queue, so a watch marked on
// trakt.tv reaches every server in the same cycle (the T6 gate).
public sealed class TraktPullScheduler : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly ITraktSyncService _sync;
    private readonly ITraktAuthService _auth;
    private readonly Func<bool> _isPlaying;
    private readonly Func<Task<int>> _drainSiblingPage; // returns the page size; 0 when the queue is drained
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    public TraktPullScheduler(ITraktSyncService sync, ITraktAuthService auth, Func<bool> isPlaying, Func<Task<int>> drainSiblingPage)
    {
        _sync = sync;
        _auth = auth;
        _isPlaying = isPlaying;
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
            do await PullAsync();
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) { }
    }

    private void OnStateChanged(object? sender, TraktConnectionState state)
    {
        if (state == TraktConnectionState.Connected) _ = PullAsync();
    }

    // False when skipped: not connected, playing, or a pull already running. A skipped tick waits for the next one.
    public async Task<bool> PullAsync()
    {
        if (_auth.State != TraktConnectionState.Connected || _isPlaying() || !_running.Wait(0)) return false;
        try
        {
            await _sync.PullWatchedAsync(_stop.Token);
            while (!_isPlaying() && !_stop.IsCancellationRequested && await _drainSiblingPage() > 0) { }
            return true;
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("TraktPullScheduler", "Trakt pull cycle failed", ex);
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
