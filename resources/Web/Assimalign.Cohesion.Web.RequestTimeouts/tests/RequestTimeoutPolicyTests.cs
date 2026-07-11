using System;
using System.Threading;

using Assimalign.Cohesion.Http;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Tests;

/// <summary>
/// Model-level coverage for <see cref="RequestTimeoutPolicy"/>, <see cref="RequestTimeoutMetadata"/>,
/// and <see cref="RequestTimeoutOptions"/>: defaults, validation, and the disabled spellings.
/// </summary>
public class RequestTimeoutPolicyTests
{
    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Policy: Defaults should be a bare 504 with no timeout")]
    public void Policy_Defaults_ShouldBeBare504WithNoTimeout()
    {
        // Arrange / Act
        RequestTimeoutPolicy policy = new();

        // Assert
        policy.Timeout.ShouldBeNull();
        policy.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        policy.WriteProblemDetails.ShouldBeFalse();
        policy.WriteResponse.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Policy: A zero or negative timeout should throw")]
    [InlineData(0)]
    [InlineData(-5)]
    public void Policy_ZeroOrNegativeTimeout_ShouldThrow(int milliseconds)
    {
        // Arrange
        TimeSpan timeout = TimeSpan.FromMilliseconds(milliseconds);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => new RequestTimeoutPolicy { Timeout = timeout });
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Policy: An infinite timeout should throw (null is the disable spelling)")]
    public void Policy_InfiniteTimeout_ShouldThrow()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => new RequestTimeoutPolicy { Timeout = Timeout.InfiniteTimeSpan });
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Policy: Disabled should carry a null timeout")]
    public void Policy_Disabled_ShouldCarryNullTimeout()
    {
        // Arrange / Act / Assert
        RequestTimeoutPolicy.Disabled.Timeout.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Metadata: A null policy should throw")]
    public void Metadata_NullPolicy_ShouldThrow()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentNullException>(() => new RequestTimeoutMetadata((RequestTimeoutPolicy)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Metadata: The timeout constructor should carry a default-504 policy")]
    public void Metadata_TimeoutConstructor_ShouldCarryDefault504Policy()
    {
        // Arrange
        TimeSpan timeout = TimeSpan.FromSeconds(3);

        // Act
        RequestTimeoutMetadata metadata = new(timeout);

        // Assert
        metadata.Policy.Timeout.ShouldBe(timeout);
        metadata.Policy.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Metadata: A zero or negative timeout should throw")]
    public void Metadata_ZeroOrNegativeTimeout_ShouldThrow()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => new RequestTimeoutMetadata(TimeSpan.Zero));
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Metadata: Disabled should carry the disabled policy")]
    public void Metadata_Disabled_ShouldCarryDisabledPolicy()
    {
        // Arrange / Act / Assert
        RequestTimeoutMetadata.Disabled.Policy.ShouldBeSameAs(RequestTimeoutPolicy.Disabled);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Options: Defaults should be no global policy, the system clock, and debugger suspension")]
    public void Options_Defaults_ShouldBeNoPolicySystemClockAndDebuggerSuspension()
    {
        // Arrange / Act
        RequestTimeoutOptions options = new();

        // Assert
        options.DefaultPolicy.ShouldBeNull();
        options.TimeProvider.ShouldBeSameAs(TimeProvider.System);
        options.SuspendWhenDebuggerAttached.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Options: A null TimeProvider should throw")]
    public void Options_NullTimeProvider_ShouldThrow()
    {
        // Arrange
        RequestTimeoutOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.TimeProvider = null!);
    }
}
