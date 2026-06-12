using System;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Security.Tests;

public class TlsConnectionExtensionsTests : IClassFixture<TestCertificateFixture>
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private readonly TestCertificateFixture _fixture;

    public TlsConnectionExtensionsTests(TestCertificateFixture fixture)
    {
        _fixture = fixture;
    }

    private TlsServerOptions CreateServerOptions() => new()
    {
        AuthenticationOptions =
        {
            ServerCertificate = _fixture.Certificate
        }
    };

    private static TlsClientOptions CreateClientOptions() => new()
    {
        AuthenticationOptions =
        {
            TargetHost = "localhost",
            RemoteCertificateValidationCallback = static (_, _, _, _) => true
        }
    };

    [Fact]
    public async Task UseTls_OnListener_ShouldSecureAcceptedConnections()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (TestPipeConnection client, TestPipeConnection server) = InMemoryConnectionPair.Create();
        TestConnectionListener listener = new();
        listener.Enqueue(server);
        await using SslStream clientSsl = new(
            client.AsStream(),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: static (_, _, _, _) => true);

        // Act: accept through the TLS-layered listener while a raw client handshakes on the far end.
        IConnectionListener secured = listener.UseTls(CreateServerOptions());
        Task<IConnection> acceptTask = secured.AcceptAsync(timeout.Token).AsTask();
        Task clientHandshake = clientSsl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = "localhost" },
            timeout.Token);
        await Task.WhenAll(acceptTask, clientHandshake);
        IConnection accepted = await acceptTask;

        // Assert
        accepted.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);

        byte[] payload = Encoding.UTF8.GetBytes("secured accept");
        await clientSsl.WriteAsync(payload, timeout.Token);
        await clientSsl.FlushAsync(timeout.Token);
        byte[] received = await accepted.Input.ReadExactlyAsync(payload.Length, timeout.Token);
        received.ShouldBe(payload);
    }

    [Fact]
    public void UseTls_OnListener_ShouldReportTlsCapabilities()
    {
        // Arrange
        TestConnectionListener listener = new();

        // Act
        IConnectionListener secured = listener.UseTls(CreateServerOptions());

        // Assert
        secured.Capabilities.ShouldBe(listener.Capabilities with { Security = ConnectionSecurity.Tls });
    }

    [Fact]
    public async Task UseTls_OnFactory_ShouldSecureEstablishedConnections()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (TestPipeConnection client, TestPipeConnection server) = InMemoryConnectionPair.Create();
        TestConnectionFactory factory = new();
        factory.Enqueue(client);
        await using SslStream serverSsl = new(server.AsStream(), leaveInnerStreamOpen: false);

        // Act: connect through the TLS-layered factory while a raw server handshakes on the far end.
        IConnectionFactory secured = factory.UseTls(CreateClientOptions());
        Task<IConnection> connectTask = secured.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 22000), timeout.Token).AsTask();
        Task serverHandshake = serverSsl.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions { ServerCertificate = _fixture.Certificate },
            timeout.Token);
        await Task.WhenAll(connectTask, serverHandshake);
        IConnection connected = await connectTask;

        // Assert
        connected.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);

        byte[] payload = Encoding.UTF8.GetBytes("secured connect");
        await connected.Output.WriteAsync(payload, timeout.Token);
        byte[] received = new byte[payload.Length];
        await serverSsl.ReadExactlyAsync(received, timeout.Token);
        received.ShouldBe(payload);
    }

    [Fact]
    public void UseTls_WithNullArguments_ShouldThrowArgumentNullException()
    {
        // Arrange
        TestConnectionListener listener = new();
        TestConnectionFactory factory = new();
        (TestPipeConnection client, TestPipeConnection _) = InMemoryConnectionPair.Create();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => listener.UseTls(null!));
        Should.Throw<ArgumentNullException>(() => ((IConnectionListener)null!).UseTls(new TlsServerOptions()));
        Should.Throw<ArgumentNullException>(() => factory.UseTls(null!));
        Should.Throw<ArgumentNullException>(() => ((IConnectionFactory)null!).UseTls(new TlsClientOptions()));
        Should.Throw<ArgumentNullException>(() => _ = client.UpgradeToTlsAsync((TlsServerOptions)null!));
        Should.Throw<ArgumentNullException>(() => _ = client.UpgradeToTlsAsync((TlsClientOptions)null!));
        Should.Throw<ArgumentNullException>(() => _ = ((IConnection)null!).UpgradeToTlsAsync(new TlsServerOptions()));
    }
}
