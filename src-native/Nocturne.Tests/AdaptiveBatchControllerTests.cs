using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class AdaptiveBatchControllerTests
{
    // Feeds n full pages; latencyFor(size) models the server. Returns the slowest request it planned.
    private static long Feed(AdaptiveBatchController c, int n, Func<int, long> latencyFor)
    {
        long slowest = 0;
        for (int i = 0; i < n; i++)
        {
            var size = c.CurrentBatchSize;
            var ms = latencyFor(size);
            slowest = Math.Max(slowest, ms);
            c.RecordRequest(ms, size, size, wasSuccess: true);
        }
        return slowest;
    }

    private static Func<int, long> Server(double fixedMs, double msPerItem) => size => (long)(fixedMs + msPerItem * size);

    [Fact]
    public void InitialBatchSize_DefaultsTo1000()
    {
        var controller = new AdaptiveBatchController();
        Assert.Equal(1000, controller.CurrentBatchSize);
        Assert.True(controller.IsWarmingUp);
    }

    [Fact]
    public void CustomInitialBatchSize_ClampedWithinBounds()
    {
        Assert.Equal(250, new AdaptiveBatchController(50).CurrentBatchSize);
        Assert.Equal(25000, new AdaptiveBatchController(50000).CurrentBatchSize);
    }

    [Fact]
    public void WarmUp_DoesNotAdapt()
    {
        var controller = new AdaptiveBatchController(1000);
        Feed(controller, AdaptiveBatchController.WarmupRequests, Server(200, 0.5));
        Assert.Equal(1000, controller.CurrentBatchSize);
        Assert.False(controller.IsWarmingUp);
    }

    [Fact]
    public void SixSecondBaselineServer_StillReachesALargeBatch()
    {
        // ~6 s per request whatever the size, plus a small per-item cost. The fixed 3.5 s threshold read every
        // request as slow and held such a server at the minimum.
        var controller = new AdaptiveBatchController();
        var slowest = Feed(controller, 60, Server(6000, 0.2));

        Assert.Equal(AdaptiveBatchController.MaxBatchSize, controller.CurrentBatchSize);
        Assert.True(slowest <= AdaptiveBatchController.MaxProjectedRequestMs, $"planned a {slowest} ms request");
    }

    [Fact]
    public void LargerBatchesTakingLonger_AreNotReadAsDegradation()
    {
        // Pure per-item cost: growth continues to the size whose time reaches half the HTTP timeout.
        var controller = new AdaptiveBatchController();
        Feed(controller, 60, Server(0, 1.0));
        Assert.InRange(controller.CurrentBatchSize, 14000, 15000);
    }

    [Fact]
    public void DeepPaging_FixedCostPerRequest_GrowsTheBatchToAmortiseIt()
    {
        // Valkyrie 100k items into its TV library (2026-10-21 fresh sync): ~9.5 s fixed + 3.5 ms per item.
        // ms/item normalisation shrank this to 250 items (10.6 s each, 24 items/s); the fit sizes it near 1,570.
        var controller = new AdaptiveBatchController(AdaptiveBatchController.MinBatchSize);
        Feed(controller, 40, Server(9500, 3.5));

        Assert.InRange(controller.CurrentBatchSize, 1350, 1600);
        Assert.InRange(controller.FixedMs, 9000, 10000);
    }

    [Fact]
    public void DeepPaging_WithNoisyLatency_StaysUsefulAndUnderTheTimeout()
    {
        var random = new Random(42);
        Func<int, long> noisy = size => (long)((9500 + 3.5 * size) * (0.75 + 0.5 * random.NextDouble())); // +-25%
        var controller = new AdaptiveBatchController(AdaptiveBatchController.MinBatchSize);

        var slowest = Feed(controller, 80, noisy);

        Assert.InRange(controller.CurrentBatchSize, 800, 2600);
        Assert.True(slowest < 30000, $"a {slowest} ms request would hit the 30 s timeout");
        Assert.InRange(controller.BaselineMsPerItem, 1.0, 8.0); // true slope 3.5; a noisy first fit must not pin it
    }

    [Fact]
    public void FixedCostAboveTheBudget_GrowsTheBatch_InsteadOfCollapsing()
    {
        // Valkyrie 300k+ items into its TV library (2026-10-22): 13-20 s per request at any size. A fixed 15 s
        // budget sized this to the minimum: 250 items in ~19 s, 12 items/s, for two hours.
        var controller = new AdaptiveBatchController(AdaptiveBatchController.MinBatchSize);
        Feed(controller, 30, Server(18000, 0.5));
        var sizes = new List<int>();
        long slowest = 0;
        for (int i = 0; i < 20; i++)
        {
            sizes.Add(controller.CurrentBatchSize);
            slowest = Math.Max(slowest, Feed(controller, 1, Server(18000, 0.5)));
        }

        // Around (22.5 s - 18 s) / 0.5 ms = 9,000; a probe for size spread may plan 25 % more items.
        Assert.InRange(sizes.Average(), 7500, 11250);
        Assert.True(slowest < 25000, $"planned a {slowest} ms request");
    }

    [Fact]
    public void FixedCostAboveTheBudget_WithNoisyLatency_StaysUnderTheTimeout()
    {
        var random = new Random(7);
        Func<int, long> noisy = size => (long)(18000 * (0.75 + 0.5 * random.NextDouble()) + 0.5 * size); // fixed +-25%
        var controller = new AdaptiveBatchController(AdaptiveBatchController.MinBatchSize);
        var sizes = new List<int>();
        long slowest = 0;
        for (int i = 0; i < 80; i++)
        {
            sizes.Add(controller.CurrentBatchSize);
            slowest = Math.Max(slowest, Feed(controller, 1, noisy));
        }

        Assert.True(sizes.Skip(40).Average() >= 3000, $"averaged {sizes.Skip(40).Average():F0} items");
        Assert.True(slowest < 30000, $"a {slowest} ms request would hit the 30 s timeout");
    }

    [Fact]
    public void SlowerPerItem_ShrinksTheBatch()
    {
        var controller = new AdaptiveBatchController(4000);
        Feed(controller, 30, Server(0, 1.0));

        Feed(controller, 20, Server(0, 4.0));
        var sizes = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            sizes.Add(controller.CurrentBatchSize);
            Feed(controller, 1, Server(0, 4.0));
        }

        // Around 15 s / 4 ms = 3,750, reached through the EWMA; a probe for size spread may plan 25 % more.
        Assert.InRange(sizes.Average(), 3500, 4700);
    }

    [Fact]
    public void TransientSlowdown_DoesNotInflateTheBaseline_AndTheBatchRecovers()
    {
        var controller = new AdaptiveBatchController();
        Feed(controller, 40, Server(500, 1.0));
        var before = controller.CurrentBatchSize;

        Feed(controller, 10, Server(500, 5.0));
        Assert.True(controller.CurrentBatchSize < before);
        Assert.True(controller.BaselineMsPerItem < 1.25, $"baseline inflated to {controller.BaselineMsPerItem:F2}");

        Feed(controller, 60, Server(500, 1.0));
        Assert.True(controller.CurrentBatchSize >= before * 0.9, $"recovered only to {controller.CurrentBatchSize} of {before}");
    }

    [Fact]
    public void ShortFinalPage_IsNotASample()
    {
        var controller = new AdaptiveBatchController(5000);
        Feed(controller, 6, Server(100, 0.2));
        var (size, fixedMs) = (controller.CurrentBatchSize, controller.FixedMs);

        controller.RecordRequest(latencyMs: 2000, itemsReturned: 12, itemsRequested: size, wasSuccess: true);

        Assert.Equal(size, controller.CurrentBatchSize);
        Assert.Equal(fixedMs, controller.FixedMs);
    }

    [Fact]
    public void NetworkFailure_ImmediatelyBacksOffToMinBatchSize()
    {
        var controller = new AdaptiveBatchController(2500);
        Feed(controller, 5, Server(0, 0.5));

        Assert.Equal(AdaptiveBatchController.MinBatchSize, controller.RecordRequest(300, 0, controller.CurrentBatchSize, wasSuccess: false));
    }

    [Fact]
    public void OverTheTimeBudget_FloorsAtMinBatchSize()
    {
        var controller = new AdaptiveBatchController(500);
        Feed(controller, 20, Server(0, 100.0)); // 25 s even at 250 items

        Assert.Equal(AdaptiveBatchController.MinBatchSize, controller.CurrentBatchSize);
    }

    [Fact]
    public void Reset_RestoresDefaultsAndForgetsTheServer()
    {
        var controller = new AdaptiveBatchController(2500);
        Feed(controller, 10, Server(500, 1.0));

        controller.Reset();

        Assert.Equal(AdaptiveBatchController.DefaultBatchSize, controller.CurrentBatchSize);
        Assert.Equal(0, controller.BaselineMsPerItem);
        Assert.Equal(0, controller.FixedMs);
        Assert.True(controller.IsWarmingUp);
    }
}
