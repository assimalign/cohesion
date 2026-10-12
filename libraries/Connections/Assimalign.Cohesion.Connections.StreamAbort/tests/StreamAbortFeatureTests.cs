using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.StreamAbort.Tests.TestObjects;

namespace Assimalign.Cohesion.Connections.StreamAbort.Tests;

public class StreamAbortFeatureTests
{
    [Fact(DisplayName = "Cohesion Test [Connections.StreamAbort] - DirectionalAbort: Returns the registered feature")]
    public void DirectionalAbort_WhenRegistered_ShouldReturnFeature()
    {
        // Arrange
        IConnection connection = new TestConnection();
        TestFeature feature = new();
        connection.Features.Set(feature);

        // Act
        IMultiplexedStreamAbort? result = connection.DirectionalAbort;

        // Assert
        result.ShouldBeSameAs(feature);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.StreamAbort] - DirectionalAbort: Returns null without a feature")]
    public void DirectionalAbort_WhenAbsent_ShouldReturnNull()
    {
        // Arrange
        IConnection connection = new TestConnection();

        // Act
        IMultiplexedStreamAbort? result = connection.DirectionalAbort;

        // Assert
        result.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.StreamAbort] - FeatureName: Pins the registration name")]
    public void FeatureName_ForContract_ShouldMatchPinnedName()
    {
        // Arrange
        TestFeature feature = new();

        // Act
        string name = feature.Name;

        // Assert
        IMultiplexedStreamAbort.FeatureName.ShouldBe("StreamAbort");
        name.ShouldBe(IMultiplexedStreamAbort.FeatureName);
    }
}
