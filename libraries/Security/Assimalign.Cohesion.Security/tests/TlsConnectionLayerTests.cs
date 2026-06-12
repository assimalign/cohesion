using System;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Security.Tests;

public class TlsConnectionLayerTests : IClassFixture<TestCertificateFixture>
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private readonly TestCertificateFixture _fixture;

    public TlsConnectionLayerTests(TestCertificateFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Describe_WithUnsecuredCapabilities_ShouldFlipSecurityToTlsAndPreserveRest()
    {
        // Arrange
        TlsConnectionLayer layer = new(new TlsServerOptions());
        ConnectionCapabilities capabilities = TestPipeConnection.DefaultCapabilities;

        // Act
        ConnectionCapabilities described = layer.Describe(capabilities);

        // Assert
        described.ShouldBe(capabilities with { Security = ConnectionSecurity.Tls });
    }

    [Fact]
    public async Task UpgradeAsync_WithServerOptions_ShouldCompleteServerSideHandshake()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (TestPipeConnection client, TestPipeConnection server) = InMemoryConnectionPair.Create();
        TlsConnectionLayer layer = new(new TlsServerOptions
        {
            AuthenticationOptions =
            {
                ServerCertificate = _fixture.Certificate
            }
        });
        await using SslStream clientSsl = new(
            client.AsStream(),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: static (_, _, _, _) => true);

        // Act: drive the layer's server handshake against a raw client SslStream on the far end.
        Task<IConnection> serverUpgrade = layer.UpgradeAsync(server, timeout.Token).AsTask();
        Task clientHandshake = clientSsl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = "localhost" },
            timeout.Token);
        await Task.WhenAll(serverUpgrade, clientHandshake);
        IConnection secured = await serverUpgrade;

        // Assert
        secured.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);

        // Bytes the raw client writes arrive decrypted on the upgraded connection.
        byte[] request = Encoding.UTF8.GetBytes("ping");
        await clientSsl.WriteAsync(request, timeout.Token);
        await clientSsl.FlushAsync(timeout.Token);
        byte[] received = await secured.Input.ReadExactlyAsync(request.Length, timeout.Token);
        received.ShouldBe(request);

        // And bytes the upgraded connection writes arrive decrypted at the raw client.
        byte[] response = Encoding.UTF8.GetBytes("pong");
        await secured.Output.WriteAsync(response, timeout.Token);
        byte[] clientReceived = new byte[response.Length];
        await clientSsl.ReadExactlyAsync(clientReceived, timeout.Token);
        clientReceived.ShouldBe(response);
    }

    [Fact]
    public void Ctor_WithNullServerOptions_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        Should.Throw<ArgumentNullException>(() => new TlsConnectionLayer((TlsServerOptions)null!));
    }

    [Fact]
    public void Ctor_WithNullClientOptions_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        Should.Throw<ArgumentNullException>(() => new TlsConnectionLayer((TlsClientOptions)null!));
    }

    [Fact]
    public void UpgradeAsync_WithNullConnection_ShouldThrowArgumentNullException()
    {
        // Arrange
        TlsConnectionLayer layer = new(new TlsServerOptions());

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => _ = layer.UpgradeAsync(null!));
    }
}
