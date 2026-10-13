using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Quic.Internal;
using Assimalign.Cohesion.Connections.Quic.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Connections.Quic.Tests;

// Real-QUIC tests report a discovery-time skip where the platform lacks a QUIC
// implementation (for example, a missing libmsquic); xunit 2.x has no runtime skip.
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class QuicConnectionListenerTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [QuicFact(DisplayName = "Cohesion Test [Connections.Quic] - BindAsync: Construction should remain unbound until explicit bind")]
    public async Task BindAsync_OnUnboundListener_ShouldBindConcreteEndPoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        QuicConnectionListenerOptions options = CreateOptions(certificate, new IPEndPoint(IPAddress.Loopback, 0));
        await using QuicConnectionListener listener = new(options);

        ((IPEndPoint)listener.EndPoint).Port.ShouldBe(0);

        // Act
        await listener.BindAsync(cancellation.Token);
        EndPoint firstBoundEndPoint = listener.EndPoint;
        await listener.BindAsync(cancellation.Token);

        // Assert
        ((IPEndPoint)listener.EndPoint).Port.ShouldNotBe(0);
        listener.EndPoint.ShouldBe(firstBoundEndPoint);
    }

    [QuicFact(DisplayName = "Cohesion Test [Connections.Quic] - BindAsync: Dispose should release a fixed endpoint for a new listener")]
    public async Task BindAsync_AfterDisposedListenerOnSameEndPoint_ShouldSucceed()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        QuicConnectionListener first = new(CreateOptions(certificate, new IPEndPoint(IPAddress.Loopback, 0)));
        await first.BindAsync(cancellation.Token);
        IPEndPoint endPoint = first.EndPoint.ShouldBeOfType<IPEndPoint>();

        // Act
        await first.DisposeAsync();

        await using QuicConnectionListener second = new(CreateOptions(certificate, endPoint));
        await second.BindAsync(cancellation.Token);

        // Assert
        second.EndPoint.ShouldBe(endPoint);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - BindAsync: Disposed listener should reject bind and accept")]
    public async Task BindAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        QuicConnectionListener listener = new(CreateOptions(certificate, new IPEndPoint(IPAddress.Loopback, 0)));
        await listener.DisposeAsync();

        // Act / Assert
        await Should.ThrowAsync<ObjectDisposedException>(async () => await listener.BindAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () => await listener.AcceptAsync());
    }

    [QuicFact(DisplayName = "Cohesion Test [Connections.Quic] - AcceptAsync: A client the certificate policy refuses should not end the accept")]
    public async Task AcceptAsync_AfterRefusingAClientWithoutACertificate_ShouldAcceptTheNextClient()
    {
        // Arrange — the listener requires a client certificate, so a client without one fails its handshake,
        // which System.Net.Quic reports from the next accept while it keeps listening (#1304).
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        using X509Certificate2 clientCertificate = QuicTestCertificate.CreateClient();
        QuicConnectionListenerOptions options = CreateOptions(certificate, new IPEndPoint(IPAddress.Loopback, 0));
        options.ServerAuthenticationOptions.ClientCertificateRequired = true;
        options.ServerAuthenticationOptions.RemoteCertificateValidationCallback = static (_, presented, _, _) => presented is not null;
        using EventSourceRecorder recorder = new(QuicConnectionEventSource.Log, EventLevel.Warning);
        await using QuicConnectionListener listener = new(options);
        await listener.BindAsync(cancellation.Token);
        Task<IMultiplexedConnection> accept = listener.AcceptAsync(cancellation.Token).AsTask();

        // Act — the first client is refused. Wait until the pending accept has dealt with the refusal
        // (it reports the dropped handshake, or it fails), then a second client presents a certificate.
        await QuicTestClient.ConnectRefusedAsync(listener.EndPoint, cancellation.Token);
        await WaitUntilAsync(() => accept.IsCompleted || recorder.Events.Any(e => e.EventName == "HandshakeFailed"), cancellation.Token);
        bool acceptEndedByRefusal = accept.IsCompleted;
        await using MultiplexedConnection client = (MultiplexedConnection)await QuicTestClient.CreateFactory(clientCertificate).ConnectAsync(listener.EndPoint, cancellation.Token);
        await using MultiplexedConnection server = (MultiplexedConnection)await accept;

        // Assert — the accept that was pending when the refusal happened returned the second client.
        acceptEndedByRefusal.ShouldBeFalse();
        server.Tls.ShouldNotBeNull().RemoteCertificate!.Thumbprint.ShouldBe(clientCertificate.Thumbprint);
    }

    [QuicFact]
    public async Task CreateAsync_WithEphemeralEndPoint_ShouldBindConcreteEndPoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        // Act
        await using QuicConnectionListener listener = await QuicConnectionListener.CreateAsync(options =>
        {
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            options.ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols = [new SslApplicationProtocol("cohesion-test")],
                EnabledSslProtocols = SslProtocols.Tls13
            };
        }, cancellation.Token);

        // Assert
        IPEndPoint boundEndPoint = listener.EndPoint.ShouldBeOfType<IPEndPoint>();

        boundEndPoint.Address.ShouldBe(IPAddress.Loopback);
        boundEndPoint.Port.ShouldNotBe(0);
    }

    [QuicFact]
    public async Task CreateAsync_WithoutServerCertificate_ShouldThrowInvalidOperationException()
    {
        // Arrange
        // The default options carry an ALPN list but no server certificate.
        QuicConnectionListenerOptions options = new()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0)
        };

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await QuicConnectionListener.CreateAsync(options));
    }

    [QuicFact]
    public async Task CreateAsync_WithEmptyApplicationProtocols_ShouldThrowInvalidOperationException()
    {
        // Arrange
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        QuicConnectionListenerOptions options = new()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0)
        };

        options.ServerAuthenticationOptions.ServerCertificate = certificate;
        options.ServerAuthenticationOptions.ApplicationProtocols = new List<SslApplicationProtocol>();

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await QuicConnectionListener.CreateAsync(options));
    }

    [Fact]
    public async Task CreateAsync_WithNullOptions_ShouldThrowArgumentNullException()
    {
        // Arrange / Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await QuicConnectionListener.CreateAsync((QuicConnectionListenerOptions)null!));
    }

    [QuicFact]
    public async Task Capabilities_OnCreatedListener_ShouldDescribeMultiplexedTlsQuicStream()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using QuicConnectionListener listener = await QuicConnectionListener.CreateAsync(options =>
        {
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            options.ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols = [new SslApplicationProtocol("cohesion-test")],
                EnabledSslProtocols = SslProtocols.Tls13
            };
        }, cancellation.Token);

        // Act
        ConnectionCapabilities capabilities = listener.Capabilities;

        // Assert
        capabilities.ShouldBe(new ConnectionCapabilities(
            ConnectionProtocol.Quic,
            ConnectionDelivery.Stream,
            IsReliable: true,
            IsOrdered: true,
            IsMultiplexed: true,
            ConnectionSecurity.Tls));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private static QuicConnectionListenerOptions CreateOptions(X509Certificate2 certificate, IPEndPoint endPoint)
    {
        return new QuicConnectionListenerOptions
        {
            EndPoint = endPoint,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols = [new SslApplicationProtocol("cohesion-test")],
                EnabledSslProtocols = SslProtocols.Tls13
            }
        };
    }
}
