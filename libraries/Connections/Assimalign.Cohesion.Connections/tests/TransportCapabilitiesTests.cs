using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

public class TransportCapabilitiesTests
{
    private static ConnectionCapabilities CreateTcpCapabilities() => new(
        ConnectionProtocol.Tcp,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        Security: ConnectionSecurity.None);

    [Fact]
    public void Equals_WithIdenticalValues_ShouldBeEqual()
    {
        // Arrange
        ConnectionCapabilities first = CreateTcpCapabilities();
        ConnectionCapabilities second = CreateTcpCapabilities();

        // Act & Assert
        first.ShouldBe(second);
        (first == second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Fact]
    public void Equals_WithDifferentSecurity_ShouldNotBeEqual()
    {
        // Arrange
        ConnectionCapabilities plain = CreateTcpCapabilities();
        ConnectionCapabilities secured = CreateTcpCapabilities() with { Security = ConnectionSecurity.Tls };

        // Act & Assert
        plain.ShouldNotBe(secured);
        (plain != secured).ShouldBeTrue();
    }

    [Fact]
    public void With_WhenSecurityFlipped_ShouldPreserveOtherValues()
    {
        // Arrange
        ConnectionCapabilities original = CreateTcpCapabilities();

        // Act
        ConnectionCapabilities secured = original with { Security = ConnectionSecurity.Tls };

        // Assert
        secured.Security.ShouldBe(ConnectionSecurity.Tls);
        secured.Protocol.ShouldBe(original.Protocol);
        secured.Delivery.ShouldBe(original.Delivery);
        secured.IsReliable.ShouldBe(original.IsReliable);
        secured.IsOrdered.ShouldBe(original.IsOrdered);
        secured.IsMultiplexed.ShouldBe(original.IsMultiplexed);

        // The source value is unchanged by the non-destructive mutation.
        original.Security.ShouldBe(ConnectionSecurity.None);
    }
}
