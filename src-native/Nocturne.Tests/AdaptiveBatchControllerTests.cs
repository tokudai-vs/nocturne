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
    }

    [Fact]
    public void CustomInitialBatchSize_ClampedWithinBounds()
    {
        var tooSmall = new AdaptiveBatchController(50);
        Assert.Equal(AdaptiveBatchController.MinBatchSize, tooSmall.CurrentBatchSize);

        var tooLarge = new AdaptiveBatchController(10000);
        Assert.Equal(AdaptiveBatchController.MaxBatchSize, tooLarge.CurrentBatchSize);
    }

    [Fact]
    public void FastLatency_ScalesUpBatchSize_CappedAtMax()
    {
        var controller = new AdaptiveBatchController(1000);

        // First fast request (<1200ms)
        int next = controller.RecordLatency(latencyMs: 500, wasSuccess: true);
        Assert.Equal(1250, next);
        Assert.Equal(1250, controller.CurrentBatchSize);

        // Keep receiving fast responses until capped at MaxBatchSize (2500)
        for (int i = 0; i < 10; i++)
        {
            controller.RecordLatency(latencyMs: 600, wasSuccess: true);
        }

        Assert.Equal(AdaptiveBatchController.MaxBatchSize, controller.CurrentBatchSize);
        Assert.Equal(2500, controller.CurrentBatchSize);
    }

    [Fact]
    public void SlowLatency_ScalesDownBatchSize_FlooredAtMin()
    {
        var controller = new AdaptiveBatchController(1000);

        // First slow request (>2800ms)
        int next = controller.RecordLatency(latencyMs: 3500, wasSuccess: true);
        Assert.Equal(750, next);
        Assert.Equal(750, controller.CurrentBatchSize);

        // Keep receiving slow responses until floored at MinBatchSize (250)
        for (int i = 0; i < 10; i++)
        {
            controller.RecordLatency(latencyMs: 4000, wasSuccess: true);
        }

        Assert.Equal(AdaptiveBatchController.MinBatchSize, controller.CurrentBatchSize);
        Assert.Equal(250, controller.CurrentBatchSize);
    }

    [Fact]
    public void NormalLatency_PreservesCurrentBatchSize()
    {
        var controller = new AdaptiveBatchController(1250);

        // Response in sweet spot between 1200ms and 2800ms
        int next1 = controller.RecordLatency(latencyMs: 1500, wasSuccess: true);
        Assert.Equal(1250, next1);

        int next2 = controller.RecordLatency(latencyMs: 2500, wasSuccess: true);
        Assert.Equal(1250, next2);
    }

    [Fact]
    public void NetworkFailure_ImmediatelyBacksOffToMinBatchSize()
    {
        var controller = new AdaptiveBatchController(2500);

        int next = controller.RecordLatency(latencyMs: 300, wasSuccess: false);
        Assert.Equal(AdaptiveBatchController.MinBatchSize, next);
        Assert.Equal(250, controller.CurrentBatchSize);
    }

    [Fact]
    public void Reset_RestoresDefaultBatchSize()
    {
        var controller = new AdaptiveBatchController(2500);
        controller.RecordLatency(latencyMs: 5000, wasSuccess: false);
        Assert.Equal(AdaptiveBatchController.MinBatchSize, controller.CurrentBatchSize);

        controller.Reset();
        Assert.Equal(AdaptiveBatchController.DefaultBatchSize, controller.CurrentBatchSize);
    }
}
