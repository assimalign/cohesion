using System;
using System.Net;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.NamedPipes.Tests;

public class NamedPipeConnectionFactoryTests
{
    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Factory: Should describe a reliable, ordered named-pipe stream")]
    public void Capabilities_OnFactory_ShouldDescribeReliableOrderedNamedPipeStream()
    {
        // Arrange
        NamedPipeConnectionFactory factory = new();

        // Act
        ConnectionCapabilities capabilities = factory.Capabilities;

        // Assert
        capabilities.ShouldBe(new ConnectionCapabilities(
            ConnectionProtocol.NamedPipe,
            ConnectionDelivery.Stream,
            IsReliable: true,
            IsOrdered: true,
            IsMultiplexed: false,
            ConnectionSecurity.None));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Factory: Should reject a non-named-pipe endpoint")]
    public async Task ConnectAsync_WithNonNamedPipeEndPoint_ShouldThrowNotSupportedException()
    {
        // Arrange
        NamedPipeConnectionFactory factory = new();

        // Act / Assert
        await Should.ThrowAsync<NotSupportedException>(
            async () => await factory.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1)));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Factory: Should reject a null endpoint")]
    public async Task ConnectAsync_WithNullEndPoint_ShouldThrowArgumentNullException()
    {
        // Arrange
        NamedPipeConnectionFactory factory = new();

        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(async () => await factory.ConnectAsync(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Factory: Should reject a null configure delegate")]
    public void Create_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => NamedPipeConnectionFactory.Create(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Factory: Should reject null options")]
    public void Constructor_WithNullOptions_ShouldThrowArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new NamedPipeConnectionFactory(null!));
    }
}
