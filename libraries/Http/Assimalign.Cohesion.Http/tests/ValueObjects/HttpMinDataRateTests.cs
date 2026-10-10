using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpMinDataRateTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - MinDataRate: Should store the rate and grace period")]
    public void Constructor_OnValidValues_ShouldStoreDimensions()
    {
        // Act
        HttpMinDataRate rate = new(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));

        // Assert
        rate.BytesPerSecond.ShouldBe(240);
        rate.GracePeriod.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Theory(DisplayName = "Cohesion Test [Http] - MinDataRate: Should reject a rate that is not greater than zero")]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public void Constructor_OnRateNotGreaterThanZero_ShouldThrow(double bytesPerSecond)
    {
        // Act and assert — NaN compares false against zero either way, so it is refused like zero: a NaN
        // rate would make every allowance NaN and switch the check off.
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpMinDataRate(bytesPerSecond, TimeSpan.FromSeconds(5)));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - MinDataRate: Should reject a non-positive grace period")]
    public void Constructor_OnNonPositiveGracePeriod_ShouldThrow()
    {
        // Act and assert
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpMinDataRate(240, TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpMinDataRate(240, TimeSpan.FromSeconds(-1)));
    }
}
