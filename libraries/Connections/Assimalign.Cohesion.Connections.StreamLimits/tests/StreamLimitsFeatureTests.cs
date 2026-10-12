using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.StreamLimits.Tests.TestObjects;

namespace Assimalign.Cohesion.Connections.StreamLimits.Tests;

public class StreamLimitsFeatureTests
{
    [Fact(DisplayName = "Cohesion Test [Connections.StreamLimits] - StreamLimits: Returns the registered feature")]
    public void StreamLimits_WhenRegistered_ShouldReturnFeature()
    {
        // Arrange
        IMultiplexedConnection connection = new TestConnection();
        TestFeature feature = new();
        connection.Features.Set(feature);

        // Act
        IMultiplexedConnectionStreamLimits? result = connection.StreamLimits;

        // Assert
        result.ShouldBeSameAs(feature);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.StreamLimits] - StreamLimits: Returns null without a feature")]
    public void StreamLimits_WhenAbsent_ShouldReturnNull()
    {
        // Arrange
        IMultiplexedConnection connection = new TestConnection();

        // Act
        IMultiplexedConnectionStreamLimits? result = connection.StreamLimits;

        // Assert
        result.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.StreamLimits] - FeatureName: Pins the registration name")]
    public void FeatureName_ForContract_ShouldMatchPinnedName()
    {
        // Arrange
        TestFeature feature = new();

        // Act
        string name = feature.Name;

        // Assert
        IMultiplexedConnectionStreamLimits.FeatureName.ShouldBe("StreamLimits");
        name.ShouldBe(IMultiplexedConnectionStreamLimits.FeatureName);
    }
}
