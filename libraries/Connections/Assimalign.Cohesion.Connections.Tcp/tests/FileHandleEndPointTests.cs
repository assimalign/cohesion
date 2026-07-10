using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

/// <summary>
/// Covers the socket-activation / file-descriptor hand-off path (systemd <c>.socket</c>, launchd, or a
/// supervising parent process): the listener adopts an already-bound, already-listening socket by its
/// handle and accepts on it directly, without re-binding or re-listening.
/// </summary>
public class FileHandleEndPointTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - FileHandle: Should adopt an inherited listening socket and accept on it")]
    public async Task AcceptAsync_WithFileHandleEndPoint_ShouldAdoptInheritedSocketAndAccept()
    {
        // Arrange — a "parent" has already bound and started listening; the child inherits the descriptor.
        using CancellationTokenSource cancellation = new(TestTimeout);

        Socket activated = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        activated.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        activated.Listen(16);

        IPEndPoint bound = (IPEndPoint)activated.LocalEndPoint!;
        ulong fileHandle = (ulong)activated.SafeHandle.DangerousGetHandle();

        await using TcpConnectionListener listener = TcpConnectionListener.Create(
            options => options.EndPoint = new FileHandleEndPoint(fileHandle, FileHandleType.Tcp));

        ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

        TcpConnectionFactory factory = new();

        // Act — dialing the inherited listening socket's address should be accepted by the adopting listener.
        await using Connection client = await factory.ConnectAsync(bound, cancellation.Token);
        await using Connection server = await acceptTask;

        // Assert
        server.ShouldNotBeNull();
        ((IPEndPoint)server.LocalEndPoint!).Port.ShouldBe(bound.Port);

        // The adopting listener owns the descriptor (ownsHandle: true) and closes it on dispose; mark the
        // simulated parent's handle invalid so it does not double-close the same descriptor.
        activated.SafeHandle.SetHandleAsInvalid();
    }
}
