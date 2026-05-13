namespace Assimalign.Cohesion.Caching.Tests;

public class CacheEnumsTests
{
    [Fact(DisplayName = "Cohesion Test [Caching] - CacheEntryPriority: enum exposes Low/Normal/High/NeverRemove")]
    public void CacheEntryPriority_ExposesExpectedValues()
    {
        Assert.Equal(0, (int)CacheEntryPriority.Low);
        Assert.Equal(1, (int)CacheEntryPriority.Normal);
        Assert.Equal(2, (int)CacheEntryPriority.High);
        Assert.Equal(3, (int)CacheEntryPriority.NeverRemove);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheEvictionReason: covers None/Removed/Replaced/Expired/TokenExpired/Capacity")]
    public void CacheEvictionReason_ExposesExpectedValues()
    {
        Assert.Equal(0, (int)CacheEvictionReason.None);
        Assert.Equal(1, (int)CacheEvictionReason.Removed);
        Assert.Equal(2, (int)CacheEvictionReason.Replaced);
        Assert.Equal(3, (int)CacheEvictionReason.Expired);
        Assert.Equal(4, (int)CacheEvictionReason.TokenExpired);
        Assert.Equal(5, (int)CacheEvictionReason.Capacity);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheErrorCode: ordinals are stable")]
    public void CacheErrorCode_ExposesExpectedValues()
    {
        Assert.Equal(0, (int)CacheErrorCode.Unknown);
        Assert.Equal(1, (int)CacheErrorCode.Disposed);
        Assert.Equal(2, (int)CacheErrorCode.InvalidEntry);
        Assert.Equal(3, (int)CacheErrorCode.CapacityExceeded);
    }
}
