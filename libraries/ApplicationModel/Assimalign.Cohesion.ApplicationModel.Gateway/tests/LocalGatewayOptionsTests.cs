using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class LocalGatewayOptionsTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Liveness threshold: invalid assignments preserve the validated value")]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(int.MinValue, true)]
    public void LivenessFailureThreshold_InvalidAssignment_ThrowsAndPreservesValue(int value, bool createGateway)
    {
        // Arrange
        var options = new LocalGatewayOptions { LivenessFailureThreshold = 2 };
        if (createGateway)
        {
            _ = new LocalGateway(options);
        }

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.LivenessFailureThreshold = value);

        // Assert
        exception.ParamName.ShouldBe(nameof(LocalGatewayOptions.LivenessFailureThreshold));
        options.LivenessFailureThreshold.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Restart limit: invalid assignments preserve the validated value")]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, false)]
    [InlineData(-1, true)]
    [InlineData(int.MinValue, true)]
    public void MaximumRestartAttempts_InvalidAssignment_ThrowsAndPreservesValue(int value, bool createGateway)
    {
        // Arrange
        var options = new LocalGatewayOptions { MaximumRestartAttempts = 2 };
        if (createGateway)
        {
            _ = new LocalGateway(options);
        }

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.MaximumRestartAttempts = value);

        // Assert
        exception.ParamName.ShouldBe(nameof(LocalGatewayOptions.MaximumRestartAttempts));
        options.MaximumRestartAttempts.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Supervision limits: valid updates after construction are accepted")]
    [InlineData(1, 0)]
    [InlineData(3, 5)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void SetLimits_AfterGatewayConstruction_AcceptsValidValues(int threshold, int attempts)
    {
        // Arrange
        var options = new LocalGatewayOptions();
        _ = new LocalGateway(options);

        // Act
        options.LivenessFailureThreshold = threshold;
        options.MaximumRestartAttempts = attempts;

        // Assert
        options.LivenessFailureThreshold.ShouldBe(threshold);
        options.MaximumRestartAttempts.ShouldBe(attempts);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Supervision limits: defaults remain three failures and five restarts")]
    public void Constructor_DefaultOptions_UsesExpectedLimits()
    {
        // Arrange / Act
        var options = new LocalGatewayOptions();

        // Assert
        options.LivenessFailureThreshold.ShouldBe(3);
        options.MaximumRestartAttempts.ShouldBe(5);
    }
}
