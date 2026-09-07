using System;
using System.Collections.Generic;
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

namespace Assimalign.Cohesion.Connections.Quic.Tests;

// Every test gates on QuicListener.IsSupported and no-ops where the platform lacks a QUIC
// implementation (for example, a missing libmsquic); xunit 2.x has no runtime skip.
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class QuicConnectionListenerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - BindAsync: Construction should remain unbound until explicit bind")]
    public async Task BindAsync_OnUnboundListener_ShouldBindConcreteEndPoint()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
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

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - BindAsync: Dispose should release a fixed endpoint for a new listener")]
    public async Task BindAsync_AfterDisposedListenerOnSameEndPoint_ShouldSucceed()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
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
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        QuicConnectionListener listener = new(CreateOptions(certificate, new IPEndPoint(IPAddress.Loopback, 0)));
        await listener.DisposeAsync();

        // Act / Assert
        await Should.ThrowAsync<ObjectDisposedException>(async () => await listener.BindAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () => await listener.AcceptAsync());
    }

    [Fact]
    public async Task CreateAsync_WithEphemeralEndPoint_ShouldBindConcreteEndPoint()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
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

    [Fact]
    public async Task CreateAsync_WithoutServerCertificate_ShouldThrowInvalidOperationException()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

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

    [Fact]
    public async Task CreateAsync_WithEmptyApplicationProtocols_ShouldThrowInvalidOperationException()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

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
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange / Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await QuicConnectionListener.CreateAsync((QuicConnectionListenerOptions)null!));
    }

    [Fact]
    public async Task Capabilities_OnCreatedListener_ShouldDescribeMultiplexedTlsQuicStream()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
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
