using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class HttpMinDataRateTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRate: Should store the rate and grace period")]
    public void Constructor_OnValidValues_ShouldStoreDimensions()
    {
        HttpMinDataRate rate = new(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));

        rate.BytesPerSecond.ShouldBe(240);
        rate.GracePeriod.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - MinDataRate: Should reject a non-positive rate")]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void Constructor_OnNonPositiveRate_ShouldThrow(double bytesPerSecond)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpMinDataRate(bytesPerSecond, TimeSpan.FromSeconds(5)));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRate: Should reject a non-positive grace period")]
    public void Constructor_OnNonPositiveGracePeriod_ShouldThrow()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpMinDataRate(240, TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpMinDataRate(240, TimeSpan.FromSeconds(-1)));
    }
}
