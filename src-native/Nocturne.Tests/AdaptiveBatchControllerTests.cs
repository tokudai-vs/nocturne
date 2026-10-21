using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class AdaptiveBatchControllerTests
{
    [Fact]
    public void InitialBatchSize_DefaultsTo1000()
    {
        var controller = new AdaptiveBatchController();
        Assert.Equal(AdaptiveBatchController.DefaultBatchSize, controller.CurrentBatchSize);
        Assert.Equal(1000, controller.CurrentBatchSize);
        Assert.Equal(0, controller.ConsecutiveSuccesses);
    }

    [Fact]
    public void CustomInitialBatchSize_ClampedWithinBounds()
    {
        var tooSmall = new AdaptiveBatchController(50);
        Assert.Equal(AdaptiveBatchController.MinBatchSize, tooSmall.CurrentBatchSize);
        Assert.Equal(250, tooSmall.CurrentBatchSize);

        var tooLarge = new AdaptiveBatchController(50000);
        Assert.Equal(AdaptiveBatchController.MaxBatchSize, tooLarge.CurrentBatchSize);
        Assert.Equal(25000, tooLarge.CurrentBatchSize);
    }

    [Fact]
    public void SuccessUnderLatencyLimit_IncreasesBatchSizeOnlyAfter5ConsecutiveSuccesses()
    {
        var controller = new AdaptiveBatchController(1000);

        // Batches 1 to 4: batch size stays 1000, consecutive successes increment
        for (int i = 1; i <= 4; i++)
        {
            int batch = controller.RecordLatency(latencyMs: 1500, wasSuccess: true);
            Assert.Equal(1000, batch);
            Assert.Equal(i, controller.ConsecutiveSuccesses);
        }

        // 5th consecutive success: scales up by 250 to 1250, counter resets to 0
        int batch5 = controller.RecordLatency(latencyMs: 1200, wasSuccess: true);
        Assert.Equal(1250, batch5);
        Assert.Equal(1250, controller.CurrentBatchSize);
        Assert.Equal(0, controller.ConsecutiveSuccesses);

        // Next 5 successes: scales up to 1500
        for (int i = 1; i <= 4; i++)
        {
            controller.RecordLatency(latencyMs: 2000, wasSuccess: true);
        }
        int batch10 = controller.RecordLatency(latencyMs: 2200, wasSuccess: true);
        Assert.Equal(1500, batch10);
        Assert.Equal(1500, controller.CurrentBatchSize);
    }

    [Fact]
    public void SingleLatencyFailure_StepsDownImmediatelyAndResetsSuccessCounter()
    {
        var controller = new AdaptiveBatchController(1000);

        // 4 successful batches in a row
        for (int i = 0; i < 4; i++)
        {
            controller.RecordLatency(latencyMs: 1000, wasSuccess: true);
        }
        Assert.Equal(4, controller.ConsecutiveSuccesses);

        // 5th batch exceeds 3500ms limit -> steps down from 1000 to 750, resets successes to 0
        int reduced = controller.RecordLatency(latencyMs: 3800, wasSuccess: true);
        Assert.Equal(750, reduced);
        Assert.Equal(750, controller.CurrentBatchSize);
        Assert.Equal(0, controller.ConsecutiveSuccesses);
    }

    [Fact]
    public void RecoveryAfterSlowdown_RecoversBatchSizeAfter5ConsecutiveSuccesses()
    {
        var controller = new AdaptiveBatchController(500);

        // Slow down to minimum
        controller.RecordLatency(latencyMs: 4000, wasSuccess: true); // 500 -> 250
        Assert.Equal(250, controller.CurrentBatchSize);

        // Now healthy responses arrive: previously it stayed reduced, now it recovers after 5 successes!
        for (int i = 0; i < 4; i++)
        {
            controller.RecordLatency(latencyMs: 800, wasSuccess: true);
            Assert.Equal(250, controller.CurrentBatchSize);
        }

        controller.RecordLatency(latencyMs: 800, wasSuccess: true); // 5th success
        Assert.Equal(500, controller.CurrentBatchSize); // Scaled back up to 500
    }

    [Fact]
    public void NetworkFailure_ImmediatelyBacksOffToMinBatchSizeAndResetsCounter()
    {
        var controller = new AdaptiveBatchController(2500);
        for (int i = 0; i < 3; i++) controller.RecordLatency(1000, true);

        int next = controller.RecordLatency(latencyMs: 300, wasSuccess: false);
        Assert.Equal(AdaptiveBatchController.MinBatchSize, next);
        Assert.Equal(250, controller.CurrentBatchSize);
        Assert.Equal(0, controller.ConsecutiveSuccesses);
    }

    [Fact]
    public void LatencyFailure_FloorsAtMinBatchSize()
    {
        var controller = new AdaptiveBatchController(500);

        controller.RecordLatency(latencyMs: 4000, wasSuccess: true); // 500 -> 250
        Assert.Equal(250, controller.CurrentBatchSize);

        controller.RecordLatency(latencyMs: 4000, wasSuccess: true); // Floored at 250
        Assert.Equal(AdaptiveBatchController.MinBatchSize, controller.CurrentBatchSize);
        Assert.Equal(250, controller.CurrentBatchSize);
    }

    [Fact]
    public void Reset_RestoresDefaultBatchSizeAndClearsCounter()
    {
        var controller = new AdaptiveBatchController(2500);
        for (int i = 0; i < 3; i++) controller.RecordLatency(1000, true);
        controller.RecordLatency(latencyMs: 5000, wasSuccess: false);
        Assert.Equal(AdaptiveBatchController.MinBatchSize, controller.CurrentBatchSize);

        controller.Reset();
        Assert.Equal(AdaptiveBatchController.DefaultBatchSize, controller.CurrentBatchSize);
        Assert.Equal(0, controller.ConsecutiveSuccesses);
    }
}
