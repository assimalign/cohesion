using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Management.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Management.Tests;

public sealed class CertificateHandshakeTests
{
    [Fact(DisplayName = "Cohesion Test [Web] - Certificate state: new TLS handshakes observe the committed certificate version")]
    public async Task ApplyAsync_BetweenHandshakes_ShouldRotateTheServedCertificateAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using X509Certificate2 initial = VersionedCertificateResolver.CreateCertificate();
        using CertificateResourceStateHandler handler = CertificateResourceStateHandler.Create(new VersionedCertificateResolver(), initial);
        var tls = new TlsServerOptions();
        handler.ConfigureTls(tls);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = ServeTwoHandshakesAsync(listener, tls.AuthenticationOptions, timeout.Token);

        // Act
        Task<string> firstRequest = ReadCertificateAsync(port, timeout.Token);
        if (await Task.WhenAny(server, firstRequest) == server) { await server; }
        string first = await firstRequest;
        using JsonDocument value = JsonDocument.Parse("""{"certificateRef":{"provider":"secrets","key":"api-tls","version":"v2"}}""");
        ResourceStateApplyResult applied = await handler.ApplyAsync(new ResourceStateApplyContext(Guid.NewGuid(), Guid.NewGuid(), 1, "v2", 1), "https", new ResourceStateValue(true, value.RootElement), timeout.Token);
        string second = await ReadCertificateAsync(port, timeout.Token);
        await server;

        // Assert
        applied.Status.ShouldBe(ResourceStateApplyStatus.Applied);
        first.ShouldBe(initial.Thumbprint);
        second.ShouldNotBe(first);
    }

    [Theory(DisplayName = "Cohesion Test [Web] - Certificate state: mutable version aliases are refused")]
    [InlineData("latest")]
    [InlineData("LATEST")]
    [InlineData("*")]
    [InlineData(" ")]
    public void CertificateVersion_WithMutableAlias_ShouldReject(string version) => ResourceCertificateVersion.TryCreate(version, out _).ShouldBeFalse();

    private static async Task ServeTwoHandshakesAsync(TcpListener listener, SslServerAuthenticationOptions options, CancellationToken cancellationToken)
    {
        for (int index = 0; index < 2; index++)
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = new SslStream(connection.GetStream());
            await stream.AuthenticateAsServerAsync(options, cancellationToken);
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
            await stream.ReadExactlyAsync(new byte[1], cancellationToken);
        }
    }

    private static async Task<string> ReadCertificateAsync(int port, CancellationToken cancellationToken)
    {
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        await using var stream = new SslStream(connection.GetStream(), leaveInnerStreamOpen: false, static (_, _, _, _) => true);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, cancellationToken);
        await stream.ReadExactlyAsync(new byte[1], cancellationToken);
        await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
        return stream.RemoteCertificate!.GetCertHashString();
    }
}
