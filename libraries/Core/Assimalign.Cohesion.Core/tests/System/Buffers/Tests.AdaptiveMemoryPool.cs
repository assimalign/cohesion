using Xunit;

namespace System.Buffers.Tests;

public class AdaptiveMemoryPoolTests
{
    [Fact]
    public void Return_WhenPolicyRetentionLimitIsReached_ShouldRetainOnlyAllowedBlocks()
    {
        // Arrange
        using var pool = new AdaptiveMemoryPool(new AdaptiveMemoryPoolOptions()
        {
            TrimInterval = TimeSpan.Zero,
            Policy = new FixedRetentionPolicy(2)
        });

        IMemoryOwner<byte> first = pool.Rent();
        IMemoryOwner<byte> second = pool.Rent();
        IMemoryOwner<byte> third = pool.Rent();

        // Act
        first.Dispose();
        second.Dispose();
        third.Dispose();

        AdaptiveMemoryPoolSnapshot snapshot = pool.GetSnapshot();

        // Assert
        Assert.Equal(2, snapshot.AllocatedBlockCount);
        Assert.Equal(0, snapshot.InUseBlockCount);
        Assert.Equal(2, snapshot.RetainedBlockCount);
    }

    [Fact]
    public void Trim_WhenWarmWindowExpires_ShouldReduceRetainedBlocksToMinimum()
    {
        // Arrange
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 03, 16, 0, 0, 0, TimeSpan.Zero));
        using var pool = new AdaptiveMemoryPool(new AdaptiveMemoryPoolOptions()
        {
            TimeProvider = timeProvider,
            TrimInterval = TimeSpan.Zero,
            Policy = new AdaptiveMemoryPoolPressurePolicy()
            {
                MinimumRetainedBlocks = 1,
                MaximumRetainedBlocks = 8,
                PeakRetentionRatio = 1,
                WarmWindow = TimeSpan.FromSeconds(10)
            }
        });

        IMemoryOwner<byte> first = pool.Rent();
        IMemoryOwner<byte> second = pool.Rent();
        IMemoryOwner<byte> third = pool.Rent();

        first.Dispose();
        second.Dispose();
        third.Dispose();

        // Act
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        pool.Trim();

        AdaptiveMemoryPoolSnapshot snapshot = pool.GetSnapshot();

        // Assert
        Assert.Equal(1, snapshot.AllocatedBlockCount);
        Assert.Equal(1, snapshot.RetainedBlockCount);
    }

    private sealed class FixedRetentionPolicy : IAdaptiveMemoryPoolPolicy
    {
        private readonly int _retentionLimit;

        public FixedRetentionPolicy(int retentionLimit)
        {
            _retentionLimit = retentionLimit;
        }

        public int GetRetentionLimit(AdaptiveMemoryPoolSnapshot snapshot)
        {
            return _retentionLimit;
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public TestTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan timeSpan)
        {
            _utcNow = _utcNow.Add(timeSpan);
        }
    }
}
