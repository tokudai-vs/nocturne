namespace Nocturne.Core.Services;

/// <summary>
/// Latency-guided adaptive batch controller that dynamically scales fetch batch size
/// based on server response times and transient error states, protecting servers while maximizing throughput.
/// </summary>
public class AdaptiveBatchController
{
    public const int MinBatchSize = 250;
    public const int MaxBatchSize = 25000;
    public const int DefaultBatchSize = 1000;
    public const int StepSize = 250;
    public const long LatencyLimitMs = 3500;
    public const int ConsecutiveSuccessesNeeded = 5;

    public int CurrentBatchSize { get; private set; }
    public int ConsecutiveSuccesses { get; private set; }

    public AdaptiveBatchController(int initialBatchSize = DefaultBatchSize)
    {
        CurrentBatchSize = Math.Clamp(initialBatchSize, MinBatchSize, MaxBatchSize);
        ConsecutiveSuccesses = 0;
    }

    /// <summary>
    /// Records the latency and success status of the last request and adjusts batch size accordingly.
    /// - Transient network / HTTP error: immediate backoff to MinBatchSize (250) and resets success counter.
    /// - Latency failure (&gt;3500ms): steps down by StepSize (250) and resets success counter.
    /// - 5 consecutive successful batches under latency threshold: increases batch size by StepSize (250).
    /// </summary>
    public int RecordLatency(long latencyMs, bool wasSuccess)
    {
        if (!wasSuccess)
        {
            // Immediate backoff to minimal batch size on network error or HTTP failure
            ConsecutiveSuccesses = 0;
            CurrentBatchSize = MinBatchSize;
            return CurrentBatchSize;
        }

        if (latencyMs > LatencyLimitMs)
        {
            // Latency failure: exceeded threshold (>3500ms). Step down and reset success counter.
            ConsecutiveSuccesses = 0;
            CurrentBatchSize = Math.Max(MinBatchSize, CurrentBatchSize - StepSize);
            return CurrentBatchSize;
        }

        // Healthy response within latency limit (<= 3500ms)
        ConsecutiveSuccesses++;
        if (ConsecutiveSuccesses >= ConsecutiveSuccessesNeeded)
        {
            CurrentBatchSize = Math.Min(MaxBatchSize, CurrentBatchSize + StepSize);
            ConsecutiveSuccesses = 0;
        }

        return CurrentBatchSize;
    }

    public void Reset()
    {
        CurrentBatchSize = DefaultBatchSize;
        ConsecutiveSuccesses = 0;
    }
}
