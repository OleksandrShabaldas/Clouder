using Clouder.Storage;

namespace Clouder.Storage.Tests;

/// <summary>
/// The shared speed limit. Uses an injected clock so the tests are deterministic
/// rather than timing-dependent.
/// </summary>
public class BandwidthLimiterTests
{
    private sealed class FakeClock
    {
        public long Now { get; set; }
        public void Advance(long ms) => Now += ms;
    }

    [Fact]
    public void ZeroMeansUnlimited()
    {
        var limiter = new BandwidthLimiter(0);

        for (int i = 0; i < 100; i++)
            Assert.True(limiter.TryConsume(1_000_000, out _));
    }

    [Fact]
    public void SpendsTheBudgetThenMakesTheCallerWait()
    {
        var clock = new FakeClock();
        var limiter = new BandwidthLimiter(1000, () => clock.Now);

        // A full second's worth is available immediately.
        Assert.True(limiter.TryConsume(1000, out _));

        // The next byte has to wait.
        Assert.False(limiter.TryConsume(500, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        // Half a second later, half the budget is back.
        clock.Advance(500);
        Assert.True(limiter.TryConsume(500, out _));
    }

    [Fact]
    public void RefillsOverTimeButDoesNotAccumulateUnboundedBurst()
    {
        var clock = new FakeClock();
        var limiter = new BandwidthLimiter(1000, () => clock.Now);

        limiter.TryConsume(1000, out _);   // drain

        // Idle for ten seconds — tokens cap at one second's worth, so a later burst
        // can't blow through the limit.
        clock.Advance(10_000);

        Assert.True(limiter.TryConsume(1000, out _));
        Assert.False(limiter.TryConsume(1, out _));
    }

    [Fact]
    public void OversizedReadIsNotDeadlocked()
    {
        var clock = new FakeClock();
        var limiter = new BandwidthLimiter(1000, () => clock.Now);

        limiter.TryConsume(1000, out _); // drain the initial allowance

        // A single read larger than the whole per-second budget can never fit in the
        // bucket; it must still eventually pass rather than blocking forever.
        Assert.False(limiter.TryConsume(5000, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        clock.Advance(1000); // bucket refills to full
        Assert.True(limiter.TryConsume(5000, out _));
    }

    [Fact]
    public void ConcurrentTransfersShareOneBudget()
    {
        var clock = new FakeClock();
        var limiter = new BandwidthLimiter(1000, () => clock.Now);

        // Four "transfers" drawing on the same limiter consume one shared 1000-byte
        // allowance — the bug this class fixes was each getting its own.
        Assert.True(limiter.TryConsume(250, out _));
        Assert.True(limiter.TryConsume(250, out _));
        Assert.True(limiter.TryConsume(250, out _));
        Assert.True(limiter.TryConsume(250, out _));

        Assert.False(limiter.TryConsume(250, out _));
    }

    [Fact]
    public void ChangingTheRateTakesEffectImmediately()
    {
        var clock = new FakeClock();
        var limiter = new BandwidthLimiter(1000, () => clock.Now);

        limiter.TryConsume(1000, out _);
        limiter.BytesPerSecond = 0; // unlimited

        Assert.True(limiter.TryConsume(999_999, out _));
    }

    [Fact]
    public async Task ConsumeAsync_ReturnsOnceBudgetAllows()
    {
        var limiter = new BandwidthLimiter(1_000_000);

        // Well within budget: should complete without a meaningful delay.
        await limiter.ConsumeAsync(1000);
        await limiter.ConsumeAsync(1000);
    }

    [Fact]
    public void TransferBudget_KeepsDirectionsIndependent()
    {
        var budget = new TransferBudget();
        budget.Upload.BytesPerSecond = 1000;
        budget.Download.BytesPerSecond = 0;

        Assert.True(budget.Upload.TryConsume(1000, out _));
        Assert.False(budget.Upload.TryConsume(1, out _));

        // Downloads are unlimited and unaffected by the upload budget being spent.
        Assert.True(budget.Download.TryConsume(10_000_000, out _));
    }
}
