using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpSaslConnectionFeatureExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp] - AmqpSasl: Should return null for a connection that did not authenticate")]
    public async Task AmqpSasl_WithoutFeature_ShouldReturnNull()
    {
        // Arrange
        await using TestConnectionPair connections = new();

        // Act
        IAmqpSaslConnectionInfo? info = connections.Server.AmqpSasl;

        // Assert
        info.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - AmqpSasl: Should return the outcome the handshake recorded on the connection")]
    public async Task AmqpSasl_WithFeature_ShouldReturnFeature()
    {
        // Arrange
        await using TestConnectionPair connections = new();
        TestSaslConnectionInfo recorded = new() { Identity = "alice", HostName = "broker.test" };
        connections.Server.Features.Set<IAmqpSaslConnectionInfo>(recorded);

        // Act
        IAmqpSaslConnectionInfo? info = connections.Server.AmqpSasl;

        // Assert
        info.ShouldBeSameAs(recorded);
        info.ShouldNotBeNull().Identity.ShouldBe("alice");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - AmqpSasl: Should refuse a null connection")]
    public void AmqpSasl_WithNullConnection_ShouldThrowArgumentNullException()
    {
        // Arrange
        IConnection connection = null!;

        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => connection.AmqpSasl);

        // Assert
        exception.ParamName.ShouldBe("connection");
    }
}
