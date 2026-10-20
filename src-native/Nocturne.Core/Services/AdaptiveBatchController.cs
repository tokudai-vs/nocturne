namespace Nocturne.Core.Services;

/// <summary>
/// Latency-guided adaptive batch controller that dynamically scales fetch batch size
/// based on server response times and transient error states, protecting servers while maximizing throughput.
/// </summary>
public class AdaptiveBatchController
{
    public const int MinBatchSize = 250;
    public const int MaxBatchSize = 2500;
    public const int DefaultBatchSize = 1000;
    public const int StepSize = 250;
    public const long FastLatencyThresholdMs = 1200;
    public const long SlowLatencyThresholdMs = 2800;

    public int CurrentBatchSize { get; private set; }

    public AdaptiveBatchController(int initialBatchSize = DefaultBatchSize)
    {
        CurrentBatchSize = Math.Clamp(initialBatchSize, MinBatchSize, MaxBatchSize);
    }

    /// <summary>
    /// Records the latency and success status of the last request and adjusts batch size accordingly.
    /// Fast responses (&lt;1200ms) increase batch size by 250 up to 2500.
    /// Slow responses (&gt;2800ms) decrease batch size by 250 down to 250 to relieve server pressure.
    /// Transient errors immediately back off to MinBatchSize (250).
    /// </summary>
    public int RecordLatency(long latencyMs, bool wasSuccess)
    {
        if (!wasSuccess)
        {
            // Immediate backoff to minimal batch size on network error or HTTP failure
            CurrentBatchSize = MinBatchSize;
            return CurrentBatchSize;
        }

        if (latencyMs < FastLatencyThresholdMs)
        {
            // Fast server roundtrip: safely scale up to increase throughput
            CurrentBatchSize = Math.Min(MaxBatchSize, CurrentBatchSize + StepSize);
        }
        else if (latencyMs > SlowLatencyThresholdMs)
        {
            // High server latency: throttle down to alleviate pressure on server
            CurrentBatchSize = Math.Max(MinBatchSize, CurrentBatchSize - StepSize);
        }
        // Healthy zone (1200ms - 2800ms): keep current batch size

        return CurrentBatchSize;
    }

    public void Reset()
    {
        CurrentBatchSize = DefaultBatchSize;
    }
}
