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

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

/// <summary>
/// Owner decision 39 of 2026-10-07: a failed transport dial reaches <see cref="BlobClient.ConnectAsync"/>
/// callers as a <see cref="BlobClientException"/> carrying <see cref="ProtocolErrorCode.ConnectionFailure"/>,
/// translated from the core's <see cref="DatabaseClientException"/>, which keeps the transport's
/// exception. A canceled dial still throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class BlobClientDialFailureTests
{
    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Connect: a port nothing listens on fails with ConnectionFailure")]
    public async Task ConnectAsync_NothingListening_ShouldThrowConnectionFailure()
    {
        // Arrange
        using var unreachable = new UnreachableEndPoint();
        await using var client = CreateClient(new TcpConnectionFactory(), unreachable.EndPoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        var exception = await Should.ThrowAsync<BlobClientException>(async () => await client.ConnectAsync(timeout.Token));

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.ConnectionFailure);
        exception.Message.ShouldContain(unreachable.EndPoint.ToString(), Case.Sensitive);
        var core = exception.InnerException.ShouldBeOfType<DatabaseClientException>();
        core.Code.ShouldBe(ProtocolErrorCode.ConnectionFailure);
        core.InnerException.ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.ConnectionRefused);
    }

    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Connect: a canceled dial throws OperationCanceledException")]
    public async Task ConnectAsync_CanceledDuringDial_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var factory = new StalledConnectionFactory();
        await using var client = CreateClient(factory, new IPEndPoint(IPAddress.Loopback, DatabaseConnectionSettings.DefaultPort));
        using var cancellation = new CancellationTokenSource();

        // Act
        Task<BlobConnection> connect = client.ConnectAsync(cancellation.Token).AsTask();
        await factory.Dialing.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        var exception = await Should.ThrowAsync<OperationCanceledException>(connect);

        // Assert
        exception.CancellationToken.ShouldBe(cancellation.Token);
    }

    private static BlobClient CreateClient(IConnectionFactory factory, EndPoint endPoint)
        => BlobClient.Create(new BlobClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = "blob",
                Principal = "tester",
                EndPoint = endPoint,
                MaxPoolSize = 1,
            },
            ConnectionFactory = factory,
        });
}
