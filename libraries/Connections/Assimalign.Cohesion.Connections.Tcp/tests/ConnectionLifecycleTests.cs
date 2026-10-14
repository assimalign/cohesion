using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

public class ConnectionLifecycleTests
{
    [Theory(DisplayName = "Cohesion Test [Connections.Tcp] - ConnectionClosed: Local and idle peer closure remove accepted connections")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionClosed_OnDisposeOrAbort_ShouldNotifyIdlePeerAndUntrack(bool abort)
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using TcpConnectionListener listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await listener.BindAsync(timeout.Token);
        TcpConnectionFactory factory = new();
        ValueTask<IDuplexConnection> accepting = listener.AcceptAsync(timeout.Token);
        await using IDuplexConnection client = await factory.ConnectAsync(listener.EndPoint, timeout.Token);
        await using IDuplexConnection server = await accepting;
        listener.Id.ShouldNotBe(ListenerId.Empty);
        listener.Connections.Count.ShouldBe(1);
        TaskCompletionSource peerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = server.ConnectionClosed.Register(() => peerClosed.TrySetResult());

        // Act
        if (abort)
        {
            client.Abort();
            client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        }
        await client.DisposeAsync();
        client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        await peerClosed.Task.WaitAsync(timeout.Token);
        while (listener.Connections.Count != 0)
        {
            await Task.Delay(1, timeout.Token);
        }

        // Assert
        client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        server.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        listener.Connections.Count.ShouldBe(0);
        await server.DisposeAsync();
        server.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }
}
