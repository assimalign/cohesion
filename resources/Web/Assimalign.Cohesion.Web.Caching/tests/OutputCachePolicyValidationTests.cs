using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Caching.Tests;

public sealed class OutputCachePolicyValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Caching] - MaximumBodySize: Nonpositive policy overrides fail on assignment")]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void MaximumBodySize_NonpositiveOverride_ShouldThrowAtAssignment(long limit)
    {
        // Arrange
        OutputCachePolicy policy = new() { MaximumBodySize = 1 };

        // Act and assert
        Should.Throw<ArgumentOutOfRangeException>(() => policy.MaximumBodySize = limit);
        policy.MaximumBodySize.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Caching] - MaximumBodySize: Null and positive overrides remain accepted")]
    [InlineData(null)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void MaximumBodySize_ValidOverride_ShouldRetainValue(long? limit)
    {
        // Arrange
        OutputCachePolicy policy = new();

        // Act
        policy.MaximumBodySize = limit;

        // Assert
        policy.MaximumBodySize.ShouldBe(limit);
    }
}
