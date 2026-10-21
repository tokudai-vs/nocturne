namespace Nocturne.Core.Services;

/// <summary>
/// Sizes the next sync batch for one server (one instance per server per sync) from that server's own requests.
/// It fits request time = fixed + perItem x size over the recent full pages and sizes the next request to finish
/// within a time budget: half the HTTP timeout, or longer when the fixed cost alone approaches that. Two
/// confounds a single latency figure can't separate:
/// - a bigger batch takes longer (perItem), which a fixed 3.5 s threshold read as degradation; and
/// - every request pays a fixed cost that grows with the paging offset (Emby pages with OFFSET: ~10 s per
///   request 100k items into Valkyrie's TV library). Dividing by item count leaves that cost in, so shrinking
///   the batch raised ms/item, which shrank the batch again, down to the minimum.
/// The per-server baseline is a slow EWMA of perItem with its rise clamped, so a slowdown can't inflate it; it
/// bounds the per-item estimate the sizing uses and is logged with the current value every batch.
/// </summary>
public class AdaptiveBatchController
{
    public const int MinBatchSize = 250;
    public const int MaxBatchSize = 25000;
    public const int DefaultBatchSize = 1000;
    public const int WarmupRequests = 3;              // first requests: no adaptation yet
    public const int Window = 8;                      // recent full pages the fit uses
    public const int BaselineSeedFits = 5;            // fits averaged before the baseline's rise is clamped
    public const double BaselineAlpha = 0.05;         // slow-moving per-item baseline
    public const double CurrentAlpha = 0.3;           // fast-moving per-item current
    public const double MaxBaselineRisePerRequest = 0.01; // a slowdown can't inflate the baseline and ratchet down
    public const double MaxGrowth = 1.25, MaxShrink = 0.75; // per-request step limits
    public const long MaxProjectedRequestMs = 15000;  // half EmbyClient's 30 s request timeout
    public const long MaxRequestMs = 22500;           // three quarters of it: the budget when the fixed cost is high

    public int CurrentBatchSize { get; private set; }
    public double FixedMs { get; private set; }
    public double BaselineMsPerItem { get; private set; }
    public double CurrentMsPerItem { get; private set; }
    public bool IsWarmingUp => _samples < WarmupRequests;

    private readonly Queue<(int Size, long Ms)> _recent = new();
    private int _samples;
    private int _fits;

    public AdaptiveBatchController(int initialBatchSize = DefaultBatchSize)
    {
        CurrentBatchSize = Math.Clamp(initialBatchSize, MinBatchSize, MaxBatchSize);
    }

    /// <summary>
    /// Records one request and returns the batch size for the next. A failed request backs off to the minimum.
    /// A short page (the end of a library) is not a sample: it carries the fixed cost over a few items.
    /// </summary>
    public int RecordRequest(long latencyMs, int itemsReturned, int itemsRequested, bool wasSuccess)
    {
        if (!wasSuccess)
        {
            CurrentBatchSize = MinBatchSize;
            return CurrentBatchSize;
        }
        if (itemsReturned <= 0 || itemsReturned < itemsRequested) return CurrentBatchSize;

        _recent.Enqueue((itemsReturned, latencyMs));
        if (_recent.Count > Window) _recent.Dequeue();
        _samples++;

        var fit = Fit();
        if (fit is { } f)
        {
            FixedMs = f.FixedMs;
            if (++_fits <= BaselineSeedFits)
            {
                // Seeded from the mean of the first fits: one noisy early slope (clamped to 0.01) held the
                // rise-clamped baseline near zero for the rest of the sync.
                BaselineMsPerItem += (f.PerItem - BaselineMsPerItem) / _fits;
                CurrentMsPerItem = BaselineMsPerItem;
            }
            else
            {
                CurrentMsPerItem += CurrentAlpha * (f.PerItem - CurrentMsPerItem);
                var towardSample = BaselineMsPerItem + BaselineAlpha * (f.PerItem - BaselineMsPerItem);
                BaselineMsPerItem = Math.Min(towardSample, BaselineMsPerItem * (1 + MaxBaselineRisePerRequest));
            }
        }
        if (_samples <= WarmupRequests) return CurrentBatchSize;

        // The budget stretches with the fixed cost: Valkyrie 300k+ items into its TV library took 13-20 s per
        // request at any size, and a fixed 15 s budget, with every request over it shrinking the batch, sized it
        // to the minimum: 250 items in ~19 s (12 items/s) for two hours. There the per-item time is what
        // amortises the fixed cost, and a slow request is the fixed cost's noise, which no size fixes. Only a
        // request near the timeout shrinks the batch outright; otherwise the fit sizes it.
        var budget = Math.Clamp(FixedMs * 1.25, MaxProjectedRequestMs, MaxRequestMs);
        double next;
        if (latencyMs > MaxRequestMs && CurrentBatchSize > MinBatchSize)
            next = CurrentBatchSize * MaxShrink;                       // near the timeout
        else if (fit == null)
            next = CurrentBatchSize * MaxGrowth;                       // no size spread to fit yet: probe larger
        else
        {
            // Largest size whose predicted time fits the budget, approached in bounded steps. A server that gets
            // slower per item raises the slope and so shrinks the target; a noisy low slope can't drop the
            // estimate below half this server's baseline.
            var perItem = Math.Max(CurrentMsPerItem, BaselineMsPerItem * 0.5);
            var target = (budget - FixedMs) / Math.Max(perItem, 0.01);
            next = Math.Clamp(target, CurrentBatchSize * MaxShrink, CurrentBatchSize * MaxGrowth);
        }
        // Not rounded to a step: at the minimum, x1.25 (312) would round back to 250 and the probe would stall.
        CurrentBatchSize = Math.Clamp((int)next, MinBatchSize, MaxBatchSize);
        return CurrentBatchSize;
    }

    // Least squares over the window, when the sizes spread enough (>5%) to separate the fixed and per-item terms.
    private (double FixedMs, double PerItem)? Fit()
    {
        var n = _recent.Count;
        if (n < 3) return null;
        double meanS = _recent.Average(r => r.Size), meanT = _recent.Average(r => (double)r.Ms);
        double varS = _recent.Sum(r => (r.Size - meanS) * (r.Size - meanS));
        if (varS <= 0 || Math.Sqrt(varS / n) <= 0.05 * meanS) return null;
        double cov = _recent.Sum(r => (r.Size - meanS) * (r.Ms - meanT));
        double slope = Math.Max(cov / varS, 0.01);
        return (Math.Clamp(meanT - slope * meanS, 0, MaxRequestMs), slope);
    }

    public void Reset()
    {
        CurrentBatchSize = DefaultBatchSize;
        FixedMs = 0;
        BaselineMsPerItem = 0;
        CurrentMsPerItem = 0;
        _recent.Clear();
        _samples = 0;
        _fits = 0;
    }
}
