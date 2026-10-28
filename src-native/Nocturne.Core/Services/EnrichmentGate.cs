namespace Nocturne.Core.Services;

// D12: TMDB enrichment runs when a sync completes or when asked from Settings, never alongside a sync or during
// playback. A pass stopped by playback resumes when it ends; one stopped by a sync when the sync ends. A pass picks up
// whatever is still missing, so stopping one loses nothing done. One gate for every trigger (#30: the Settings run
// used to ignore playback).
public sealed class EnrichmentGate
{
    private readonly ITmdbEnrichmentService _tmdb;
    private readonly object _lock = new();
    private CancellationTokenSource? _stop;
    private Task _pass = Task.CompletedTask;
    private bool _playing, _syncing, _deferred;
    private IProgress<EnrichProgress> _progress = new Progress<EnrichProgress>();

    public EnrichmentGate(ITmdbEnrichmentService tmdb) => _tmdb = tmdb;

    public bool IsDeferred { get { lock (_lock) return _deferred; } }

    // What a deferred pass waits for, for Settings: "playback", "the sync", or null when nothing is waiting.
    public string? WaitingFor
    {
        get { lock (_lock) return !_deferred ? null : _playing && _syncing ? "playback and the sync" : _playing ? "playback" : _syncing ? "the sync" : null; }
    }

    // Where every pass reports (automatic ones too), until another progress is given to RunAsync.
    public void ReportTo(IProgress<EnrichProgress> progress)
    {
        lock (_lock) _progress = progress;
    }

    // Starts a pass now, or once playback and any sync have ended; returns the pass (already complete when deferred).
    // A progress given here keeps receiving the passes that follow, resumed ones included.
    public Task RunAsync(IProgress<EnrichProgress>? progress = null)
    {
        lock (_lock)
        {
            if (progress != null) _progress = progress;
            _deferred = _playing || _syncing;
            if (_deferred) return Task.CompletedTask;
            _stop?.Cancel();
            _stop = new CancellationTokenSource();
            var (token, report) = (_stop.Token, _progress);
            return _pass = Task.Run(() => _tmdb.RunAsync(report, token)); // off the caller: SyncCompleted fires inside the dedup writer gate
        }
    }

    public void SetPlaying(bool playing) => Set(ref _playing, playing);
    public void SetSyncing(bool syncing) => Set(ref _syncing, syncing);

    private void Set(ref bool flag, bool value)
    {
        lock (_lock)
        {
            if (flag == value) return;
            flag = value;
            if (value)
            {
                if (_pass.IsCompleted) return;
                _stop?.Cancel();
                _deferred = true;
                return;
            }
            if (!_deferred) return;
        }
        RunAsync(); // defers again while the other one still holds
    }
}
