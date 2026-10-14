using System;
using System.Net;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Connections.Udp.Tests;

public class ConnectionLifecycleTests
{
    [Theory(DisplayName = "Cohesion Test [Connections.Udp] - Lifecycle: Datagram identity and closure are exposed through the root")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionClosed_OnDisposeOrAbort_ShouldPublishTerminalState(bool abort)
    {
        // Arrange
        UdpConnectionFactory factory = new();
        await using IDatagramConnection connection = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IConnection root = connection;
        root.Id.ShouldNotBe(ConnectionId.Empty);
        root.State.ShouldBe(ConnectionState.Open);
        root.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();

        // Act
        if (abort)
        {
            root.Abort(new InvalidOperationException("test"));
            root.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        }
        await root.DisposeAsync();

        // Assert
        root.State.ShouldBe(abort ? ConnectionState.Aborted : ConnectionState.Closed);
        root.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        await root.DisposeAsync();
        root.Id.ShouldBe(connection.Id);
    }
}
