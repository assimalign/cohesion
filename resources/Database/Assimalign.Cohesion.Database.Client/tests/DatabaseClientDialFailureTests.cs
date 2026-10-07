using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>
/// Owner decision 39 of 2026-10-07: a failed transport dial reaches the caller of
/// <see cref="DatabaseClient.RentAsync"/> as a <see cref="DatabaseClientException"/> with
/// <see cref="ProtocolErrorCode.ConnectionFailure"/>, the endpoint in its message and the
/// transport's exception inside. The caller's cancellation and a disposed object pass through
/// unchanged.
/// </summary>
public sealed class DatabaseClientDialFailureTests
{
    private static readonly IPEndPoint ScriptedEndPoint = new(IPAddress.Loopback, DatabaseConnectionSettings.DefaultPort);

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Dial: a port nothing listens on fails with ConnectionFailure and the socket error")]
    public async Task RentAsync_NothingListening_ShouldThrowConnectionFailure()
    {
        // Arrange
        using var unreachable = new UnreachableEndPoint();
        await using var client = CreateClient(new TcpConnectionFactory(), unreachable.EndPoint);

        // Act
        var first = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(ClientTestHarness.Timeout()));

        // The pool holds one connection, so a second rent dials again only if the failed dial
        // released its slot; a leaked slot makes it wait until its token cancels instead.
        var second = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(ClientTestHarness.Timeout()));

        // Assert
        first.Code.ShouldBe(ProtocolErrorCode.ConnectionFailure);
        first.Message.ShouldContain(unreachable.EndPoint.ToString(), Case.Sensitive);
        first.InnerException.ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.ConnectionRefused);
        second.Code.ShouldBe(ProtocolErrorCode.ConnectionFailure);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Dial: a transport failure is wrapped with ConnectionFailure, the original inside")]
    [InlineData("socket")]
    [InlineData("io")]
    [InlineData("tls")]
    [InlineData("timeout")]
    [InlineData("aborted")]
    public async Task RentAsync_TransportFailure_ShouldWrapWithConnectionFailure(string failure)
    {
        // Arrange
        Exception original = failure switch
        {
            "socket" => new SocketException((int)SocketError.HostUnreachable),
            "io" => new IOException("The transport closed during the dial."),
            "tls" => new AuthenticationException("The remote certificate is invalid."),
            "timeout" => new TimeoutException("The connect timed out."),
            _ => new ConnectionAbortedException("The listener is gone."),
        };
        await using var client = CreateClient(ScriptedConnectionFactory.Failing(() => original), ScriptedEndPoint);

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(ClientTestHarness.Timeout()));

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.ConnectionFailure);
        exception.InnerException.ShouldBeSameAs(original);
        exception.Message.ShouldContain(ScriptedEndPoint.ToString(), Case.Sensitive);
        exception.Message.ShouldContain(original.Message, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Dial: a canceled dial throws OperationCanceledException and frees its slot")]
    public async Task RentAsync_CanceledDuringDial_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var factory = ScriptedConnectionFactory.Stalled();
        await using var client = CreateClient(factory, ScriptedEndPoint);
        using var cancellation = new CancellationTokenSource();

        // Act
        Task<DatabaseConnection> rent = client.RentAsync(cancellation.Token).AsTask();
        await factory.Dialing.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        var exception = await Should.ThrowAsync<OperationCanceledException>(rent);

        // The pool holds one connection: a second rent reaches the dial only if the first freed its slot.
        using var second = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Should.ThrowAsync<OperationCanceledException>(async () => await client.RentAsync(second.Token));

        // Assert
        exception.ShouldNotBeAssignableTo<DatabaseClientException>();
        exception.CancellationToken.ShouldBe(cancellation.Token);
        factory.Dials.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Dial: a TLS handshake timeout is the transport's cancellation and is wrapped")]
    public async Task RentAsync_TlsHandshakeTimeout_ShouldThrowConnectionFailure()
    {
        // Arrange: nothing accepts on the in-memory listener, so the TLS handshake never hears back
        // and the TLS layer cancels it at its own timeout while the caller's token stays live.
        await using var listener = new InMemoryConnectionListener();
        IConnectionFactory factory = listener.CreateFactory().UseTls(new TlsClientOptions
        {
            AuthenticationOptions = { TargetHost = "localhost" },
            HandshakeTimeout = TimeSpan.FromMilliseconds(200),
        });
        await using var client = CreateClient(factory, listener.EndPoint);

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(ClientTestHarness.Timeout()));

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.ConnectionFailure);
        exception.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        exception.Message.ShouldContain("timed out", Case.Sensitive);
        exception.Message.ShouldContain(listener.EndPoint.ToString()!, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Dial: a TLS alert from the server is wrapped with the TLS failure inside")]
    public async Task RentAsync_TlsAlert_ShouldThrowConnectionFailure()
    {
        // Arrange: the server end answers the client hello with a fatal handshake_failure alert.
        await using var listener = new InMemoryConnectionListener();
        IConnectionFactory factory = listener.CreateFactory().UseTls(new TlsClientOptions
        {
            AuthenticationOptions = { TargetHost = "localhost" },
        });
        await using var client = CreateClient(factory, listener.EndPoint);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = Task.Run(async () =>
        {
            await using Connection accepted = await listener.AcceptAsync(ClientTestHarness.Timeout());
            Stream stream = accepted.AsStream();
            await stream.WriteAsync(new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28 });
            await stream.FlushAsync();
            await released.Task;
        });

        // Act
        DatabaseClientException exception;
        try
        {
            exception = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(ClientTestHarness.Timeout()));
        }
        finally
        {
            released.TrySetResult();
        }
        await server;

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.ConnectionFailure);
        exception.InnerException.ShouldBeOfType<AuthenticationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Dial: a disposed transport object throws ObjectDisposedException unwrapped")]
    public async Task RentAsync_DisposedTransport_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var disposed = new ObjectDisposedException("transport");
        await using var client = CreateClient(ScriptedConnectionFactory.Failing(() => disposed), ScriptedEndPoint);

        // Act
        var exception = await Should.ThrowAsync<ObjectDisposedException>(async () => await client.RentAsync(ClientTestHarness.Timeout()));

        // Assert
        exception.ShouldBeSameAs(disposed);
    }

    private static DatabaseClient CreateClient(IConnectionFactory factory, EndPoint endPoint)
        => DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = ClientTestHarness.DatabaseName,
                Principal = "tester",
                EndPoint = endPoint,
                MaxPoolSize = 1,
            },
            ConnectionFactory = factory,
            Family = SqlProtocol.Family,
        });
}
