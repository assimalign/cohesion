using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// Owner decision 39 of 2026-10-07: a failed transport dial reaches <see cref="KeyValueClient.ConnectAsync"/>
/// callers as a <see cref="KeyValueClientException"/> of the
/// <see cref="KeyValueClientErrorKind.ConnectionFailure"/> kind, translated from the core's
/// <see cref="DatabaseClientException"/>, which keeps the transport's exception. A canceled dial still
/// throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class KeyValueClientDialFailureTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Connect: a port nothing listens on fails with the ConnectionFailure kind")]
    public async Task ConnectAsync_NothingListening_ShouldThrowConnectionFailure()
    {
        // Arrange
        using var unreachable = new UnreachableEndPoint();
        await using var client = CreateClient(new TcpConnectionFactory(), unreachable.EndPoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        var exception = await Should.ThrowAsync<KeyValueClientException>(async () => await client.ConnectAsync(timeout.Token));

        // Assert
        exception.Kind.ShouldBe(KeyValueClientErrorKind.ConnectionFailure);
        exception.Code.ShouldBe(ProtocolErrorCode.ConnectionFailure);
        exception.ConnectionUsable.ShouldBeFalse();
        exception.Message.ShouldContain(unreachable.EndPoint.ToString(), Case.Sensitive);
        var core = exception.InnerException.ShouldBeOfType<DatabaseClientException>();
        core.Code.ShouldBe(ProtocolErrorCode.ConnectionFailure);
        core.InnerException.ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.ConnectionRefused);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Connect: a canceled dial throws OperationCanceledException")]
    public async Task ConnectAsync_CanceledDuringDial_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var factory = new StalledConnectionFactory();
        await using var client = CreateClient(factory, new IPEndPoint(IPAddress.Loopback, DatabaseConnectionSettings.DefaultPort));
        using var cancellation = new CancellationTokenSource();

        // Act
        Task<KeyValueConnection> connect = client.ConnectAsync(cancellation.Token).AsTask();
        await factory.Dialing.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        var exception = await Should.ThrowAsync<OperationCanceledException>(connect);

        // Assert
        exception.CancellationToken.ShouldBe(cancellation.Token);
    }

    private static KeyValueClient CreateClient(IConnectionFactory factory, EndPoint endPoint)
        => KeyValueClient.Create(new KeyValueClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = KeyValueClientTestHarness.DatabaseName,
                Principal = "tester",
                EndPoint = endPoint,
                MaxPoolSize = 1,
            },
            ConnectionFactory = factory,
        });
}
