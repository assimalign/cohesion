using System;
using System.Buffers;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Core.Tests;

public class AdaptiveMemoryPoolPressurePolicyTests
{
    [Theory(DisplayName = "Cohesion Test [Core] - PeakRetentionRatio: rejects non-finite and out-of-range fractions")]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void PeakRetentionRatio_InvalidValue_ShouldThrowWithoutChangingPolicy(double value)
    {
        // Arrange
        var policy = new AdaptiveMemoryPoolPressurePolicy { PeakRetentionRatio = 0.5 };

        // Act
        var exception = Should.Throw<ArgumentOutOfRangeException>(() => policy.PeakRetentionRatio = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        policy.PeakRetentionRatio.ShouldBe(0.5);
    }

    [Theory(DisplayName = "Cohesion Test [Core] - PeakRetentionRatio: accepts finite fractions including both endpoints")]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void PeakRetentionRatio_ValidValue_ShouldRoundTrip(double value)
    {
        // Arrange
        var policy = new AdaptiveMemoryPoolPressurePolicy();

        // Act
        policy.PeakRetentionRatio = value;

        // Assert
        policy.PeakRetentionRatio.ShouldBe(value);
    }
}
