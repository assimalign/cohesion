using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Udp.Tests;

public class UdpConnectionFactoryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Bind_WithEphemeralLoopbackEndPoint_ShouldPopulateLocalEndPointOnly()
    {
        // Arrange
        UdpConnectionFactory factory = new();

        // Act
        await using IDatagramConnection connection = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        // Assert
        IPEndPoint localEndPoint = connection.LocalEndPoint.ShouldBeOfType<IPEndPoint>();

        localEndPoint.Address.ShouldBe(IPAddress.Loopback);
        localEndPoint.Port.ShouldNotBe(0);
        connection.RemoteEndPoint.ShouldBeNull();
    }

    [Fact]
    public async Task Connect_ToBoundServerEndPoint_ShouldPopulateRemoteEndPoint()
    {
        // Arrange
        UdpConnectionFactory factory = new();

        await using IDatagramConnection server = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        // Act
        await using IDatagramConnection client = factory.Connect(server.LocalEndPoint);

        // Assert
        client.RemoteEndPoint.ShouldBe(server.LocalEndPoint);
        ((IPEndPoint)client.LocalEndPoint).Port.ShouldNotBe(0);
    }

    [Fact]
    public void Bind_WithNonIPEndPoint_ShouldThrowNotSupportedException()
    {
        // Arrange
        UdpConnectionFactory factory = new();

        UnixDomainSocketEndPoint endPoint = new("cohesion-udp-test.sock");

        // Act / Assert
        Should.Throw<NotSupportedException>(() => factory.Bind(endPoint));
    }

    [Fact]
    public void Connect_WithNonIPEndPoint_ShouldThrowNotSupportedException()
    {
        // Arrange
        UdpConnectionFactory factory = new();

        UnixDomainSocketEndPoint endPoint = new("cohesion-udp-test.sock");

        // Act / Assert
        Should.Throw<NotSupportedException>(() => factory.Connect(endPoint));
    }

    [Fact]
    public void Bind_WithNullArguments_ShouldThrowArgumentNullException()
    {
        // Arrange
        UdpConnectionFactory factory = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => factory.Bind((EndPoint)null!));
        Should.Throw<ArgumentNullException>(() => factory.Bind((UdpBindOptions)null!));
    }

    [Fact]
    public async Task Connect_WithNullArguments_ShouldThrowArgumentNullException()
    {
        // Arrange
        UdpConnectionFactory factory = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => factory.Connect(null!));
        Should.Throw<ArgumentNullException>(() => factory.Connect(new IPEndPoint(IPAddress.Loopback, 1), null!));

        await Should.ThrowAsync<ArgumentNullException>(async () => await factory.ConnectAsync(null!));
    }

    [Fact]
    public void Create_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentNullException>(() => UdpConnectionFactory.Create(null!));
    }

    [Fact]
    public void Capabilities_OnFactory_ShouldDescribeUnreliableUnorderedUdpDatagram()
    {
        // Arrange
        UdpConnectionFactory factory = new();

        // Act
        ConnectionCapabilities capabilities = factory.Capabilities;

        // Assert
        capabilities.ShouldBe(new ConnectionCapabilities(
            ConnectionProtocol.Udp,
            ConnectionDelivery.Datagram,
            IsReliable: false,
            IsOrdered: false,
            IsMultiplexed: false,
            ConnectionSecurity.None));
    }

    [Fact]
    public async Task BindAsync_WithPreCanceledToken_ShouldReturnCanceledTask()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        using CancellationTokenSource preCanceled = new();

        preCanceled.Cancel();

        UdpConnectionFactory factory = UdpConnectionFactory.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        // Act
        Exception? exception = await Record.ExceptionAsync(async () => await factory.BindAsync(preCanceled.Token));

        // Assert
        exception.ShouldBeAssignableTo<OperationCanceledException>();
    }
}
