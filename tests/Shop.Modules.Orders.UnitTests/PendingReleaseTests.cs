using Shop.Modules.Orders.Releases;
using Xunit;

namespace Shop.Modules.Orders.UnitTests;

public sealed class PendingReleaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(6, 160)]
    [InlineData(7, 300)]
    [InlineData(1000, 300)]
    public void RetriesWaitTwiceAsLongEachTimeUpToFiveMinutes(int attempts, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), PendingRelease.RetryDelay(attempts));
    }

    [Fact]
    public void QueuedReleaseIsDueAtOnce()
    {
        var pending = new PendingRelease(Guid.CreateVersion7(), "The warehouse is down.", Now);

        Assert.Equal(0, pending.Attempts);
        Assert.Equal(Now, pending.NextAttemptAt);
    }

    [Fact]
    public void FailedRetryCountsAndSchedulesTheNext()
    {
        var pending = new PendingRelease(Guid.CreateVersion7(), "The warehouse is down.", Now);

        pending.RetryFailed("Still down.", Now);

        Assert.Equal(1, pending.Attempts);
        Assert.Equal("Still down.", pending.LastError);
        Assert.Equal(Now.AddSeconds(5), pending.NextAttemptAt);
    }

    [Fact]
    public void LongErrorIsCutToFitItsColumn()
    {
        var pending = new PendingRelease(Guid.CreateVersion7(), new string('x', 5000), Now);

        Assert.Equal(PendingRelease.MaxErrorLength, pending.LastError.Length);
    }
}
