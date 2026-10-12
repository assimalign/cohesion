using System;
using System.Buffers;
using System.IO.Pipelines;
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

namespace Assimalign.Cohesion.Connections.Quic.Tests;

// Existing facts gate on QuicListener.IsSupported; QuicTheory cases report a discovery-time skip
// where the platform lacks a QUIC implementation (for example, a missing libmsquic).
[Collection(nameof(EventSourceCollection))]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class QuicMultiplexedConnectionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Features: Registers distinct connection features and isolated stream abort features over real QUIC")]
    public async Task Features_OnRealQuic_ShouldRegisterNamedFeaturesWithoutParentFallback()
    {
        // Arrange — this gate must execute against QUIC rather than silently return when unsupported.
        QuicListener.IsSupported.ShouldBeTrue();
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);

        // Act
        ITlsConnectionInfo tls = pair.Server.Tls.ShouldNotBeNull();
        IMultiplexedConnectionStreamLimits limits = pair.Server.StreamLimits.ShouldNotBeNull();
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);
        await using (clientStream)
        await using (serverStream)
        {
            // Assert
            tls.ShouldNotBeSameAs(limits);
            tls.Name.ShouldBe(ITlsConnectionInfo.FeatureName);
            limits.Name.ShouldBe(IMultiplexedConnectionStreamLimits.FeatureName);
            pair.Server.Features.Get(ITlsConnectionInfo.FeatureName).ShouldBeSameAs(tls);
            pair.Server.Features.Get(IMultiplexedConnectionStreamLimits.FeatureName).ShouldBeSameAs(limits);
            pair.Client.Tls.ShouldNotBeNull().Name.ShouldBe(ITlsConnectionInfo.FeatureName);
            pair.Client.StreamLimits.ShouldNotBeNull().Name.ShouldBe(IMultiplexedConnectionStreamLimits.FeatureName);
            clientStream.DirectionalAbort.ShouldNotBeNull().Name.ShouldBe(IMultiplexedStreamAbort.FeatureName);
            serverStream.DirectionalAbort.ShouldNotBeNull().Name.ShouldBe(IMultiplexedStreamAbort.FeatureName);
            clientStream.Features.Get(IMultiplexedStreamAbort.FeatureName).ShouldBeSameAs(clientStream.DirectionalAbort);
            serverStream.Features.Get(IMultiplexedStreamAbort.FeatureName).ShouldBeSameAs(serverStream.DirectionalAbort);
            clientStream.Tls.ShouldBeNull();
            serverStream.Tls.ShouldBeNull();
            clientStream.Features.Get(IMultiplexedConnectionStreamLimits.FeatureName).ShouldBeNull();
            serverStream.Features.Get(IMultiplexedConnectionStreamLimits.FeatureName).ShouldBeNull();
            clientStream.Abort();
            serverStream.Abort();
        }
    }

    [QuicTheory(DisplayName = "Cohesion Test [Connections.Quic] - Stream limits: Releasing inbound bidirectional streams should advance the cumulative allowance once")]
    [InlineData(1)]
    public async Task InboundBidirectionalStreamLimit_OnSuccessiveDisposals_ShouldReplenishOnce(int initialLimit)
    {
        // Arrange — one concurrent stream still permits many sequential streams on a live connection.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(
            certificate,
            cancellation.Token,
            configureListener: options => options.MaxBidirectionalStreamCount = initialLimit);
        IMultiplexedConnectionStreamLimits limits = pair.Server.StreamLimits.ShouldNotBeNull();
        limits.InboundBidirectionalStreamLimit.ShouldBe(initialLimit);

        for (int index = 0; index < 3; index++)
        {
            (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);
            await using (clientStream)
            await using (serverStream)
            {
                // Act — Abort starts native release; repeated disposal must not release credit twice.
                serverStream.Abort();
                limits.InboundBidirectionalStreamLimit.ShouldBe(initialLimit + index + 1L);
                await serverStream.DisposeAsync();
                await serverStream.DisposeAsync();

                // Assert — the next loop's stream crosses the previous limit without closing the connection.
                limits.InboundBidirectionalStreamLimit.ShouldBe(initialLimit + index + 1L);
                pair.Server.State.ShouldBe(ConnectionState.Open);
            }
        }
    }

    [QuicTheory(DisplayName = "Cohesion Test [Connections.Quic] - Stream limits: Only inbound bidirectional disposal should replenish the bound")]
    [InlineData(ConnectionDirection.Bidirectional)]
    [InlineData(ConnectionDirection.WriteOnly)]
    public async Task InboundBidirectionalStreamLimit_OnStreamDisposal_ShouldExcludeOutboundAndUnidirectionalStreams(ConnectionDirection direction)
    {
        // Arrange — the factory currently advertises zero inbound bidirectional streams.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(
            certificate,
            cancellation.Token,
            configureListener: options => options.MaxBidirectionalStreamCount = 1);
        IMultiplexedConnectionStreamLimits clientLimits = pair.Client.StreamLimits.ShouldNotBeNull();
        IMultiplexedConnectionStreamLimits serverLimits = pair.Server.StreamLimits.ShouldNotBeNull();
        clientLimits.InboundBidirectionalStreamLimit.ShouldBe(0);
        serverLimits.InboundBidirectionalStreamLimit.ShouldBe(1);
        await using MultiplexedStream clientStream = await pair.Client.OpenStreamAsync(direction, cancellation.Token);
        await clientStream.Output.WriteAsync(new byte[] { 1 }, cancellation.Token);
        await using MultiplexedStream serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);
        await ReadBytesAsync(serverStream.Input, 1, cancellation.Token);

        // Act
        clientStream.Abort();
        serverStream.Abort();
        await clientStream.DisposeAsync();
        await serverStream.DisposeAsync();

        // Assert — outbound streams never replenish this peer's inbound allowance, nor do inbound uni streams.
        clientLimits.InboundBidirectionalStreamLimit.ShouldBe(0);
        serverLimits.InboundBidirectionalStreamLimit.ShouldBe(direction == ConnectionDirection.Bidirectional ? 2L : 1L);
    }

    [QuicTheory(DisplayName = "Cohesion Test [Connections.Quic] - Stream limits: Binding should freeze the same allowance for handshakes and accepted wrappers")]
    [InlineData(1)]
    public async Task InboundBidirectionalStreamLimit_WhenOptionsChangeAfterBind_ShouldKeepTheNegotiatedSnapshot(int initialLimit)
    {
        // Arrange — changing the original options after binding must change neither the wire allowance
        // nor the facet. A live read in either place would change the allowance to zero.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        QuicConnectionListenerOptions? listenerOptions = null;
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(
            certificate,
            cancellation.Token,
            configureListener: options =>
            {
                options.MaxBidirectionalStreamCount = initialLimit;
                listenerOptions = options;
            },
            afterBind: () => listenerOptions!.MaxBidirectionalStreamCount = 0);

        // Act — opening a request also proves the handshake advertised the frozen nonzero allowance.
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);
        await using (clientStream)
        await using (serverStream)
        {
            // Assert
            pair.Server.StreamLimits.ShouldNotBeNull().InboundBidirectionalStreamLimit.ShouldBe(initialLimit);
            clientStream.Abort();
            serverStream.Abort();
        }
    }

    [Fact]
    public async Task ConnectAsync_WithAcceptingListener_ShouldEstablishConnectedPair()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        // Act
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);

        // Assert
        pair.Client.State.ShouldBe(ConnectionState.Open);
        pair.Server.State.ShouldBe(ConnectionState.Open);
        pair.Client.Id.ShouldNotBe(pair.Server.Id);

        ((IPEndPoint)pair.Client.RemoteEndPoint!).Port.ShouldBe(((IPEndPoint)pair.Listener.EndPoint).Port);

        pair.Client.Capabilities.ShouldBe(new ConnectionCapabilities(
            ConnectionProtocol.Quic,
            ConnectionDelivery.Stream,
            IsReliable: true,
            IsOrdered: true,
            IsMultiplexed: true,
            ConnectionSecurity.Tls));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - ApplicationProtocol: Should report the protocol ALPN selected on both peers")]
    public async Task ApplicationProtocol_OnEstablishedPair_ShouldReportNegotiatedProtocol()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        // Act
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);

        // Assert — the QUIC handshake is a TLS 1.3 handshake (RFC 9001), so both peers expose it.
        pair.Server.Tls.ShouldNotBeNull().ApplicationProtocol.ShouldBe(new SslApplicationProtocol("cohesion-test"));
        pair.Client.Tls.ShouldNotBeNull().ApplicationProtocol.ShouldBe(new SslApplicationProtocol("cohesion-test"));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - TlsProtocol and CipherSuite: Should report the TLS 1.3 session on both peers")]
    public async Task TlsProtocolAndCipherSuite_OnEstablishedPair_ShouldReportTls13Session()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        // Act
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);
        ITlsConnectionInfo server = pair.Server.Tls.ShouldNotBeNull();
        ITlsConnectionInfo client = pair.Client.Tls.ShouldNotBeNull();

        // Assert
        server.TlsProtocol.ShouldBe(SslProtocols.Tls13);
        client.TlsProtocol.ShouldBe(SslProtocols.Tls13);
        server.CipherSuite.ShouldNotBe(default);
        server.CipherSuite.ShouldBe(client.CipherSuite);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - RemoteCertificate: Should report each peer's certificate to the other")]
    public async Task RemoteCertificate_WithClientCertificate_ShouldReportPeerCertificates()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the server requests a client certificate and accepts the test one.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        using X509Certificate2 clientCertificate = QuicTestCertificate.CreateClient();

        // Act
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(
            certificate,
            cancellation.Token,
            listener =>
            {
                listener.ServerAuthenticationOptions.ClientCertificateRequired = true;
                listener.ServerAuthenticationOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
            },
            client =>
            {
                client.ClientAuthenticationOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
                client.ClientAuthenticationOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate;
            });

        // Assert
        pair.Server.Tls.ShouldNotBeNull().RemoteCertificate!.Thumbprint.ShouldBe(clientCertificate.Thumbprint);
        pair.Client.Tls.ShouldNotBeNull().RemoteCertificate!.Thumbprint.ShouldBe(certificate.Thumbprint);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - RemoteCertificate: Should be null on the server when the client presents none")]
    public async Task RemoteCertificate_WithoutClientCertificate_ShouldBeNullOnServer()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        // Act
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);

        // Assert
        pair.Server.Tls.ShouldNotBeNull().RemoteCertificate.ShouldBeNull();
    }

    [Fact]
    public async Task OpenStreamAsync_Bidirectional_ShouldEchoAcrossPeers()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);

        byte[] payload = [1, 2, 3, 4, 5];

        // Act
        await using MultiplexedStream clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellation.Token);

        // A freshly opened QUIC stream is not visible to the peer until data is flushed on it,
        // so write before accepting on the server side.
        await clientStream.Output.WriteAsync(payload, cancellation.Token);

        await using MultiplexedStream serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);

        byte[] received = await ReadBytesAsync(serverStream.Input, payload.Length, cancellation.Token);

        await serverStream.Output.WriteAsync(received, cancellation.Token);

        byte[] echoed = await ReadBytesAsync(clientStream.Input, payload.Length, cancellation.Token);

        // Assert
        clientStream.Direction.ShouldBe(ConnectionDirection.Bidirectional);
        serverStream.Direction.ShouldBe(ConnectionDirection.Bidirectional);
        received.ShouldBe(payload);
        echoed.ShouldBe(payload);
    }

    [Fact]
    public async Task OpenStreamAsync_WriteOnly_ShouldSurfaceUnidirectionalHalves()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);

        byte[] payload = [9, 8, 7, 6];

        // Act
        await using MultiplexedStream clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.WriteOnly, cancellation.Token);

        // The outbound unidirectional stream has no readable half: its input is pre-completed.
        ReadResult inputResult = await clientStream.Input.ReadAsync(cancellation.Token);

        clientStream.Input.AdvanceTo(inputResult.Buffer.End);

        // A freshly opened QUIC stream is not visible to the peer until data is flushed on it,
        // so write before accepting on the server side.
        await clientStream.Output.WriteAsync(payload, cancellation.Token);

        await using MultiplexedStream serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);

        byte[] received = await ReadBytesAsync(serverStream.Input, payload.Length, cancellation.Token);

        // Assert
        clientStream.Direction.ShouldBe(ConnectionDirection.WriteOnly);
        inputResult.IsCompleted.ShouldBeTrue();
        inputResult.Buffer.IsEmpty.ShouldBeTrue();

        serverStream.Direction.ShouldBe(ConnectionDirection.ReadOnly);
        received.ShouldBe(payload);

        // The inbound unidirectional stream has no writable half: writing throws.
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await serverStream.Output.WriteAsync(payload, cancellation.Token));
    }

    [Fact]
    public async Task OpenStreamAsync_WithReadOnlyDirection_ShouldThrowArgumentException()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);

        // Act / Assert
        // A peer cannot open a stream that only the remote side writes to.
        await Should.ThrowAsync<ArgumentException>(
            async () => await pair.Client.OpenStreamAsync(ConnectionDirection.ReadOnly, cancellation.Token));
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_ShouldBeIdempotent()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);

        // Act
        await pair.Client.DisposeAsync();

        Exception? exception = await Record.ExceptionAsync(async () => await pair.Client.DisposeAsync());

        // Assert
        exception.ShouldBeNull();
        pair.Client.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public async Task DisposeAsync_WithInboundUnidirectionalStream_ShouldCloseConnectionBeforeStreamSignals()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        // Distinct sentinel codes so the assertion proves which teardown signal reached the
        // peer first, not a coincidental default.
        const long closeErrorCode = 0x22;
        const long streamErrorCode = 0x33;

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, options =>
        {
            options.DefaultCloseErrorCode = closeErrorCode;
            options.DefaultStreamErrorCode = streamErrorCode;
        });

        // A long-lived inbound unidirectional stream stands in for a critical control channel
        // (the HTTP/3 control and QPACK streams): the client holds it open for the
        // connection's lifetime and must never see it terminate ahead of the connection close
        // (RFC 9114 §6.2.1 — H3_CLOSED_CRITICAL_STREAM).
        await using MultiplexedStream clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.WriteOnly, cancellation.Token);

        await clientStream.Output.WriteAsync(new byte[] { 1 }, cancellation.Token);

        await using MultiplexedStream serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);

        // Act
        await pair.Server.DisposeAsync();

        // Assert — the first teardown signal the client observes on its open unidirectional
        // stream must be the connection close (carrying the close code), not a stream-level
        // abort (which would carry the stream code).
        QuicException exception = await WaitForClientWriteFailureAsync(clientStream, cancellation.Token);

        exception.QuicError.ShouldBe(QuicError.ConnectionAborted);
        exception.ApplicationErrorCode.ShouldBe(closeErrorCode);
    }

    [Fact]
    public async Task DisposeAsync_WithOpenBidirectionalStream_ShouldDeliverOutboundDataBeforeClose()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);

        byte[] payload = [1, 2, 3, 4, 5];

        await using MultiplexedStream clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellation.Token);

        await clientStream.Output.WriteAsync(payload, cancellation.Token);

        await using MultiplexedStream serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);

        byte[] received = await ReadBytesAsync(serverStream.Input, payload.Length, cancellation.Token);

        await serverStream.Output.WriteAsync(received, cancellation.Token);

        byte[] echoed = await ReadBytesAsync(clientStream.Input, payload.Length, cancellation.Token);

        // Act — dispose with the bidirectional stream still tracked; its write half must
        // complete gracefully (FIN, delivery acknowledged) before the connection close goes out.
        await pair.Server.DisposeAsync();

        // Assert — the client sees a graceful end of stream, not an abort overtaking it.
        ReadResult endOfStream = await clientStream.Input.ReadAsync(cancellation.Token);

        echoed.ShouldBe(payload);
        endOfStream.IsCompleted.ShouldBeTrue();
        endOfStream.Buffer.IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - DisposeAsync: A peer withholding stream credit should not hold the disposal beyond CloseDeliveryTimeout")]
    public async Task DisposeAsync_WhenPeerWithholdsStreamCredit_ShouldCloseWithinCloseDeliveryTimeout()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the server finished its stream, but the client never reads, so the response's tail and its
        // FIN wait for credit the client never grants while it keeps the connection alive.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using WithheldCreditPair pair = await WithheldCreditPair.CreateAsync(
            certificate,
            options => options.CloseDeliveryTimeout = TimeSpan.FromMilliseconds(250),
            cancellation.Token);
        await pair.ServerStream.Output.CompleteAsync();
        await pair.ServerStream.Input.CompleteAsync();

        // Act
        Task disposal = pair.Server.DisposeAsync().AsTask();

        // Assert — the disposal gives up on the delivery and closes the connection, instead of waiting for as long
        // as the client keeps the connection alive.
        try
        {
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException("The connection's disposal still waited on a stream the peer withheld credit for.");
        }

        pair.Server.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - DisposeAsync: CloseDeliveryTimeout should be validated")]
    public void CloseDeliveryTimeout_OutOfRange_ShouldThrowArgumentOutOfRange()
    {
        // Arrange
        QuicConnectionListenerOptions listenerOptions = new();
        QuicConnectionFactoryOptions factoryOptions = new();

        // Act / Assert — zero and infinite are meaningful; any other negative value is not.
        Should.Throw<ArgumentOutOfRangeException>(() => listenerOptions.CloseDeliveryTimeout = TimeSpan.FromMilliseconds(-2));
        Should.Throw<ArgumentOutOfRangeException>(() => factoryOptions.CloseDeliveryTimeout = TimeSpan.FromMilliseconds(-2));
        listenerOptions.CloseDeliveryTimeout = TimeSpan.Zero;
        factoryOptions.CloseDeliveryTimeout = Timeout.InfiniteTimeSpan;
        listenerOptions.CloseDeliveryTimeout.ShouldBe(TimeSpan.Zero);
        factoryOptions.CloseDeliveryTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
        new QuicConnectionListenerOptions().CloseDeliveryTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        new QuicConnectionFactoryOptions().CloseDeliveryTimeout.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream ConnectionClosed: A peer aborting the stream should signal the other end without a read or write")]
    public async Task StreamConnectionClosed_OnPeerAbort_ShouldFire()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);
        await using MultiplexedStream clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellation.Token);

        // A freshly opened QUIC stream is not visible to the peer until data is flushed on it.
        await clientStream.Output.WriteAsync(new byte[] { 1 }, cancellation.Token);

        await using MultiplexedStream serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);
        await ReadBytesAsync(serverStream.Input, 1, cancellation.Token);

        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = serverStream.ConnectionClosed.Register(() => closed.TrySetResult());

        // Act — RESET_STREAM and STOP_SENDING from the client.
        clientStream.Abort();

        // Assert
        try
        {
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException("The server stream's ConnectionClosed did not fire after the peer aborted the stream.");
        }
    }

    // Sentinel defaults for the code-carrying tests, so an assertion proves the caller's code reached the
    // peer rather than a coincidental default.
    private const long sentinelStreamErrorCode = 0x33;
    private const long sentinelCloseErrorCode = 0x22;

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream AbortRead: Should send STOP_SENDING with the caller's code")]
    public async Task AbortRead_WithErrorCode_ShouldSendStopSendingWithTheCode()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            // Act
            serverStream.DirectionalAbort.ShouldNotBeNull().AbortRead(0x42);

            // Assert — the client's sending direction is stopped with the code, and the server's own
            // stream keeps its lifecycle: aborting one direction is not the stream ending.
            QuicException exception = await WaitForClientWriteFailureAsync(clientStream, cancellation.Token);

            exception.QuicError.ShouldBe(QuicError.StreamAborted);
            exception.ApplicationErrorCode.ShouldBe(0x42);
            serverStream.State.ShouldBe(ConnectionState.Open);
            serverStream.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream AbortRead: Later reads should fail even when the pipe still holds received octets")]
    public async Task AbortRead_WithBufferedOctets_ShouldFailLaterReads()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the server's pipe holds octets it received but the holder has not examined, so a read
        // could be answered from the buffer without touching the QUIC stream.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            await clientStream.Output.WriteAsync(new byte[] { 2, 3, 4, 5 }, cancellation.Token);
            await BufferWithoutExaminingAsync(serverStream.Input, 4, cancellation.Token);

            // Act
            serverStream.DirectionalAbort.ShouldNotBeNull().AbortRead(0x42);

            // Assert — the contract: every later read fails, buffered octets included, as on the in-memory driver.
            QuicException read = await Should.ThrowAsync<QuicException>(
                async () => await serverStream.Input.ReadAsync(cancellation.Token));
            read.QuicError.ShouldBe(QuicError.OperationAborted);
            Should.Throw<QuicException>(() => serverStream.Input.TryRead(out _));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream AbortWrite: Should send RESET_STREAM with the caller's code")]
    public async Task AbortWrite_WithErrorCode_ShouldSendResetStreamWithTheCode()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            // Act
            serverStream.DirectionalAbort.ShouldNotBeNull().AbortWrite(0x43);

            // Assert — the client's read sees the server's sending direction reset with the code.
            QuicException exception = await Should.ThrowAsync<QuicException>(
                async () => await clientStream.Input.ReadAsync(cancellation.Token));

            exception.QuicError.ShouldBe(QuicError.StreamAborted);
            exception.ApplicationErrorCode.ShouldBe(0x43);
        }
    }

    [QuicTheory(DisplayName = "Cohesion Test [Connections.Quic] - Stream Abort: The root reset should end both directions with the caller's code")]
    [InlineData(0L)]
    [InlineData((1L << 62) - 1)]
    public async Task Abort_WithStreamErrorCode_ShouldResetBothDirectionsAndEndLifecycle(long errorCode)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            IMultiplexedStream stream = serverStream;

            // Act
            stream.Abort(errorCode, new InvalidOperationException("reset stream"));

            // Assert — the root alone sends RESET_STREAM and STOP_SENDING, then ends the lifecycle.
            QuicException read = await Should.ThrowAsync<QuicException>(
                async () => await clientStream.Input.ReadAsync(cancellation.Token));
            QuicException write = await WaitForClientWriteFailureAsync(clientStream, cancellation.Token);
            read.QuicError.ShouldBe(QuicError.StreamAborted);
            write.QuicError.ShouldBe(QuicError.StreamAborted);
            read.ApplicationErrorCode.ShouldBe(errorCode);
            write.ApplicationErrorCode.ShouldBe(errorCode);
            stream.State.ShouldBe(ConnectionState.Aborted);
            stream.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream AbortRead: Should keep the receiving direction's code when the stream is later aborted")]
    public async Task Abort_AfterAbortRead_ShouldKeepTheReadDirectionCode()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            // Arrange — the receiving direction already has its first code.
            serverStream.DirectionalAbort.ShouldNotBeNull().AbortRead(0x10e);

            // Act — the root reset must preserve it while resetting the sending direction.
            serverStream.Abort(0x42);

            // Assert — each direction keeps its first code; the root reset ends the lifecycle.
            QuicException read = await Should.ThrowAsync<QuicException>(
                async () => await clientStream.Input.ReadAsync(cancellation.Token));
            QuicException write = await WaitForClientWriteFailureAsync(clientStream, cancellation.Token);

            read.ApplicationErrorCode.ShouldBe(0x42);
            write.ApplicationErrorCode.ShouldBe(0x10e);
            serverStream.State.ShouldBe(ConnectionState.Aborted);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream AbortRead: A response completed after stopping the request should still reach the peer whole")]
    public async Task AbortRead_ThenCompleteOutput_ShouldDeliverTheResponseAndItsFin()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the RFC 9114 §4.1 pattern: the server stops the rest of the request with a code, then
        // sends its whole response and ends its side gracefully.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);
        byte[] response = [9, 8, 7];

        await using (clientStream)
        await using (serverStream)
        {
            // Act
            serverStream.DirectionalAbort.ShouldNotBeNull().AbortRead(0x100);
            await serverStream.Output.WriteAsync(response, cancellation.Token);
            await serverStream.Output.CompleteAsync();

            // Assert — the whole response and the FIN, then the stop with the caller's code, not the default.
            byte[] received = await ReadBytesAsync(clientStream.Input, response.Length, cancellation.Token);
            ReadResult end = await clientStream.Input.ReadAsync(cancellation.Token);
            QuicException write = await WaitForClientWriteFailureAsync(clientStream, cancellation.Token);

            received.ShouldBe(response);
            end.IsCompleted.ShouldBeTrue();
            end.Buffer.IsEmpty.ShouldBeTrue();
            write.QuicError.ShouldBe(QuicError.StreamAborted);
            write.ApplicationErrorCode.ShouldBe(0x100);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream half-close: Completing Output should send the FIN and keep reading the peer's data")]
    public async Task CompleteOutput_WhilePeerStillSends_ShouldSendFinAndKeepReading()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — more than the stream's flow-control window follows the server's FIN, so the server must
        // keep reading for the client to finish (#1330).
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);
        byte[] response = [9, 8, 7];
        byte[] upload = new byte[512 * 1024];
        Random.Shared.NextBytes(upload);

        await using (clientStream)
        await using (serverStream)
        {
            // Act — the server ends its side first.
            await serverStream.Output.WriteAsync(response, cancellation.Token);
            await serverStream.Output.CompleteAsync();

            byte[] received = await ReadBytesAsync(clientStream.Input, response.Length, cancellation.Token);
            ReadResult end = await clientStream.Input.ReadAsync(cancellation.Token);
            clientStream.Input.AdvanceTo(end.Buffer.End);

            Task sending = SendAndFinishAsync(clientStream, upload, cancellation.Token);
            byte[] uploaded = await ReadBytesAsync(serverStream.Input, upload.Length, cancellation.Token);
            ReadResult uploadEnd = await serverStream.Input.ReadAsync(cancellation.Token);
            await sending;

            // Assert — the client saw the response and the FIN; the server read the whole upload and its FIN,
            // and neither end saw the stream abandoned.
            received.ShouldBe(response);
            end.IsCompleted.ShouldBeTrue();
            end.Buffer.IsEmpty.ShouldBeTrue();
            uploaded.ShouldBe(upload);
            uploadEnd.IsCompleted.ShouldBeTrue();
            uploadEnd.Buffer.IsEmpty.ShouldBeTrue();
            serverStream.State.ShouldBe(ConnectionState.Open);
            serverStream.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
            clientStream.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Quic] - Stream abort: Repeated reset exchanges should release tracked streams while the connection stays open")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Abort_RepeatedResetExchanges_ShouldReleaseTrackedStreams(bool codedReset)
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — HTTP/3 resets both stream directions before aborting the request wrapper.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        QuicMultiplexedConnection server = pair.Server.ShouldBeOfType<QuicMultiplexedConnection>();
        int streamsBaseline = server.TrackedStreamCount;
        long gaugeBaseline = QuicConnectionEventSource.Log.CurrentStreams;

        for (int exchange = 0; exchange < 6; exchange++)
        {
            (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

            try
            {
                // Act — the request ends without completing the aborted input pipe.
                long errorCode = sentinelStreamErrorCode;
                if (codedReset)
                {
                    errorCode = 0x10c;
                    serverStream.Abort(errorCode);
                }

                serverStream.Abort();
                QuicException reset = await Should.ThrowAsync<QuicException>(async () =>
                    await clientStream.Input.ReadAsync(cancellation.Token));
                reset.QuicError.ShouldBe(QuicError.StreamAborted);
                reset.ApplicationErrorCode.ShouldBe(errorCode);
                await clientStream.DisposeAsync();

                for (int attempt = 0; attempt < 200 && server.TrackedStreamCount != streamsBaseline; attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
                }

                // Assert — no connection-owned wrapper survives the reset, and explicit disposal is optional.
                server.TrackedStreamCount.ShouldBe(streamsBaseline);
                QuicConnectionEventSource.Log.CurrentStreams.ShouldBe(gaugeBaseline);
                serverStream.State.ShouldBe(ConnectionState.Aborted);
                serverStream.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
                server.State.ShouldBe(ConnectionState.Open);
            }
            finally
            {
                await serverStream.DisposeAsync();
                await clientStream.DisposeAsync();
            }

            QuicConnectionEventSource.Log.CurrentStreams.ShouldBe(gaugeBaseline);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream completion: Repeated exchanges should release tracked streams and restore the gauge without disposal")]
    public async Task CompleteHalves_RepeatedExchanges_ShouldReleaseTrackedStreamsAndRestoreGauge()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — each request and response uses one bidirectional stream on the same connection.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);
        QuicMultiplexedConnection client = pair.Client.ShouldBeOfType<QuicMultiplexedConnection>();
        QuicMultiplexedConnection server = pair.Server.ShouldBeOfType<QuicMultiplexedConnection>();
        int clientBaseline = client.TrackedStreamCount;
        int serverBaseline = server.TrackedStreamCount;
        long streamsBaseline = QuicConnectionEventSource.Log.CurrentStreams;

        for (int exchange = 0; exchange < 6; exchange++)
        {
            (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

            try
            {
                // Act — complete each pipe as HTTP/3 does, without disposing either wrapper.
                await clientStream.Output.CompleteAsync();
                ReadResult requestEnd = await serverStream.Input.ReadAsync(cancellation.Token);
                serverStream.Input.AdvanceTo(requestEnd.Buffer.End);
                requestEnd.IsCompleted.ShouldBeTrue();
                requestEnd.Buffer.IsEmpty.ShouldBeTrue();
                await serverStream.Input.CompleteAsync();

                // A completed receiving half alone must leave the response available and tracked.
                server.TrackedStreamCount.ShouldBe(serverBaseline + 1);
                client.TrackedStreamCount.ShouldBe(clientBaseline + 1);
                QuicConnectionEventSource.Log.CurrentStreams.ShouldBe(streamsBaseline + 2);

                byte[] response = [(byte)exchange];
                await serverStream.Output.WriteAsync(response, cancellation.Token);
                await serverStream.Output.CompleteAsync();
                (await ReadBytesAsync(clientStream.Input, response.Length, cancellation.Token)).ShouldBe(response);
                ReadResult responseEnd = await clientStream.Input.ReadAsync(cancellation.Token);
                clientStream.Input.AdvanceTo(responseEnd.Buffer.End);
                responseEnd.IsCompleted.ShouldBeTrue();
                responseEnd.Buffer.IsEmpty.ShouldBeTrue();
                await clientStream.Input.CompleteAsync();

                // Releasing QuicStream waits in the background for the FIN acknowledgement.
                for (int attempt = 0; attempt < 200 &&
                    (server.TrackedStreamCount != serverBaseline || client.TrackedStreamCount != clientBaseline ||
                     QuicConnectionEventSource.Log.CurrentStreams != streamsBaseline); attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
                }

                // Assert — each exchange restores the baseline while the connection remains usable.
                server.TrackedStreamCount.ShouldBe(serverBaseline);
                client.TrackedStreamCount.ShouldBe(clientBaseline);
                QuicConnectionEventSource.Log.CurrentStreams.ShouldBe(streamsBaseline);
                serverStream.State.ShouldBe(ConnectionState.Open);
                clientStream.State.ShouldBe(ConnectionState.Open);
                serverStream.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
                clientStream.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
                server.State.ShouldBe(ConnectionState.Open);
                client.State.ShouldBe(ConnectionState.Open);
            }
            finally
            {
                await serverStream.DisposeAsync();
                await clientStream.DisposeAsync();
            }

            // A later explicit disposal cannot report a second close or decrement the gauge again.
            QuicConnectionEventSource.Log.CurrentStreams.ShouldBe(streamsBaseline);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream half-close: Completing Input should stop the peer with the default code and keep writing")]
    public async Task CompleteInput_WhilePeerStillReads_ShouldStopReadingAndKeepWriting()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);
        byte[] response = [6, 5, 4];

        await using (clientStream)
        await using (serverStream)
        {
            // Act — the server stops reading, then still answers.
            await serverStream.Input.CompleteAsync();
            QuicException stopped = await WaitForClientWriteFailureAsync(clientStream, cancellation.Token);

            await serverStream.Output.WriteAsync(response, cancellation.Token);
            await serverStream.Output.CompleteAsync();

            byte[] received = await ReadBytesAsync(clientStream.Input, response.Length, cancellation.Token);
            ReadResult end = await clientStream.Input.ReadAsync(cancellation.Token);

            // Assert — STOP_SENDING carried the default code, and the response arrived whole with its FIN.
            stopped.QuicError.ShouldBe(QuicError.StreamAborted);
            stopped.ApplicationErrorCode.ShouldBe(sentinelStreamErrorCode);
            received.ShouldBe(response);
            end.IsCompleted.ShouldBeTrue();
            end.Buffer.IsEmpty.ShouldBeTrue();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream half-close: Completing Output with an error should reset the sending direction")]
    public async Task CompleteOutput_WithError_ShouldResetTheSendingDirection()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            // Act — an abnormal end of the sending direction must not look like a complete one.
            await serverStream.Output.CompleteAsync(new InvalidOperationException("the response failed"));

            // Assert — RESET_STREAM with the default code, never a FIN; the server can still read.
            QuicException reset = await Should.ThrowAsync<QuicException>(
                async () => await clientStream.Input.ReadAsync(cancellation.Token));
            reset.QuicError.ShouldBe(QuicError.StreamAborted);
            reset.ApplicationErrorCode.ShouldBe(sentinelStreamErrorCode);

            await clientStream.Output.WriteAsync(new byte[] { 3 }, cancellation.Token);
            (await ReadBytesAsync(serverStream.Input, 1, cancellation.Token)).ShouldBe(new byte[] { 3 });
        }
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Quic] - Stream half-close: An abort after completing Output should keep the FIN and the data before it")]
    [InlineData(false)] // AbortWrite with a code
    [InlineData(true)]  // Abort of the whole stream
    public async Task Abort_AfterCompletingOutput_ShouldKeepTheFin(bool wholeStream)
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the server ends its side while the response's tail and the FIN still wait for credit, so
        // QuicStream would still reset the direction.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using WithheldCreditPair pair = await WithheldCreditPair.CreateAsync(certificate, static _ => { }, cancellation.Token);
        await pair.ServerStream.Output.CompleteAsync();

        // Act — the completion was the first signal for the sending direction; a later abort must not replace it.
        if (wholeStream)
        {
            pair.ServerStream.Abort();
        }
        else
        {
            pair.ServerStream.DirectionalAbort.ShouldNotBeNull().AbortWrite(0x42);
        }

        // Assert — the client reads the whole response and its FIN, never a RESET_STREAM.
        int read = await pair.ReadToEndAsync(cancellation.Token);

        read.ShouldBe(WithheldCreditPair.ResponseLength);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream half-close: Completing Output when its final flush fails should reset the sending direction")]
    public async Task CompleteOutput_WhenFinalFlushFails_ShouldResetTheSendingDirection()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — MsQuic's send buffer is full, so the next write to the QUIC stream stays in flight. The pipe
        // then holds 1 MiB in several segments and flushes them one at a time: the first is in flight.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using WithheldCreditPair pair = await WithheldCreditPair.CreateAsync(certificate, static _ => { }, cancellation.Token);
        PipeWriter output = pair.ServerStream.Output;
        await output.WriteAsync(new byte[256 * 1024], cancellation.Token);

        output.Write(new byte[1024 * 1024]);
        ValueTask<FlushResult> flushing = output.FlushAsync(cancellation.Token);
        flushing.IsCompleted.ShouldBeFalse();

        // Act — the completion's own flush collides with the one in flight and fails, leaving most of the
        // buffered octets unsent.
        Task<Exception?> completing = Task.Run<Exception?>(() => Record.Exception(() => output.Complete()));
        Exception? readFailure = await Record.ExceptionAsync(() => pair.ReadToEndAsync(cancellation.Token));
        Exception? completion = await completing.WaitAsync(cancellation.Token);
        await Record.ExceptionAsync(async () => await flushing);

        // Assert — the failed flush surfaced, and the client saw RESET_STREAM with the default code instead of a
        // FIN after partial data.
        completion.ShouldBeAssignableTo<InvalidOperationException>();
        QuicException reset = readFailure.ShouldBeOfType<QuicException>();
        reset.QuicError.ShouldBe(QuicError.StreamAborted);
        reset.ApplicationErrorCode.ShouldBe(sentinelStreamErrorCode);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Stream half-close: The stream pipes should leave the QUIC stream open when they complete")]
    public void CreateStreamOptions_ShouldLeaveTheStreamOpen()
    {
        // Arrange — no QUIC needed: the driver ends each direction itself, so neither pipe adapter may
        // dispose the stream underneath the other direction (#1330).
        QuicConnectionListenerOptions listenerOptions = new();
        QuicConnectionFactoryOptions factoryOptions = new();

        // Act
        using StreamPipeOptionsContext server = listenerOptions.CreateStreamOptions();
        using StreamPipeOptionsContext client = factoryOptions.CreateStreamOptions();

        // Assert
        server.ReaderOptions.LeaveOpen.ShouldBeTrue();
        server.WriterOptions.LeaveOpen.ShouldBeTrue();
        client.ReaderOptions.LeaveOpen.ShouldBeTrue();
        client.WriterOptions.LeaveOpen.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Quic] - Stream abort: A code outside the QUIC range should be rejected")]
    [InlineData(-1L)]
    [InlineData(1L << 62)]
    public async Task AbortReadAndWrite_WithCodeOutOfRange_ShouldThrowArgumentOutOfRange(long errorCode)
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            IMultiplexedStreamAbort abort = serverStream.DirectionalAbort.ShouldNotBeNull();

            // Act / Assert
            Should.Throw<ArgumentOutOfRangeException>(() => abort.AbortRead(errorCode));
            Should.Throw<ArgumentOutOfRangeException>(() => abort.AbortWrite(errorCode));
            Should.Throw<ArgumentOutOfRangeException>(() => serverStream.Abort(errorCode));
            Should.Throw<ArgumentOutOfRangeException>(() => pair.Server.Abort(errorCode));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Connection Abort: Should close the connection with the caller's code")]
    public async Task Abort_WithErrorCode_ShouldCloseTheConnectionWithTheCode()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            // Act — an HTTP/3 connection error, H3_FRAME_UNEXPECTED; the later disposal must not replace it.
            pair.Server.Abort(0x105, new InvalidOperationException("frame unexpected"));
            await pair.Server.DisposeAsync();

            // Assert
            QuicException exception = await WaitForClientWriteFailureAsync(clientStream, cancellation.Token);

            exception.QuicError.ShouldBe(QuicError.ConnectionAborted);
            exception.ApplicationErrorCode.ShouldBe(0x105);
            pair.Server.State.ShouldBe(ConnectionState.Aborted);
            pair.Server.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Connection Abort: Without a code should close with the configured default")]
    public async Task Abort_WithoutErrorCode_ShouldCloseWithTheDefaultCode()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(certificate, cancellation.Token, WithSentinelCodes);
        (MultiplexedStream clientStream, MultiplexedStream serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            // Act
            pair.Server.Abort(new InvalidOperationException("no code"));

            // Assert
            QuicException exception = await WaitForClientWriteFailureAsync(clientStream, cancellation.Token);

            exception.QuicError.ShouldBe(QuicError.ConnectionAborted);
            exception.ApplicationErrorCode.ShouldBe(sentinelCloseErrorCode);
        }
    }

    [QuicTheory(DisplayName = "Cohesion Test [Connections.Quic] - Connection Abort: Disposal should retain its close code while delivery is pending")]
    [InlineData(0x42L)]
    public async Task Abort_AfterDisposalStarts_ShouldKeepTheDisposalCloseCode(long errorCode)
    {
        // Arrange — the peer withholds stream credit so disposal pauses before sending CONNECTION_CLOSE.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        await using WithheldCreditPair pair = await WithheldCreditPair.CreateAsync(
            certificate,
            options => options.CloseDeliveryTimeout = TimeSpan.FromMilliseconds(100),
            cancellation.Token);
        Task disposal = pair.Server.DisposeAsync().AsTask();
        disposal.IsCompleted.ShouldBeFalse();
        pair.Server.State.ShouldBe(ConnectionState.Closing);

        // Act — a later coded abort must not choose the close while disposal waits for the peer.
        pair.Server.Abort(errorCode);
        await disposal.WaitAsync(cancellation.Token);

        // Assert
        QuicException closed = await Should.ThrowAsync<QuicException>(
            async () => await pair.ReadToEndAsync(cancellation.Token));
        closed.QuicError.ShouldBe(QuicError.ConnectionAborted);
        closed.ApplicationErrorCode.ShouldBe(sentinelCloseErrorCode);
        pair.Server.State.ShouldBe(ConnectionState.Closed);
    }

    private static void WithSentinelCodes(QuicConnectionListenerOptions options)
    {
        options.DefaultStreamErrorCode = sentinelStreamErrorCode;
        options.DefaultCloseErrorCode = sentinelCloseErrorCode;
    }

    /// <summary>
    /// Opens a bidirectional stream from the client and accepts it on the server, which sees a QUIC stream only
    /// once data is flushed on it; the server has read the one octet that announced it.
    /// </summary>
    private static async Task<(MultiplexedStream Client, MultiplexedStream Server)> OpenAcceptedStreamAsync(LoopbackPair pair, CancellationToken cancellationToken)
    {
        MultiplexedStream clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellationToken);
        await clientStream.Output.WriteAsync(new byte[] { 1 }, cancellationToken);

        MultiplexedStream serverStream = await pair.Server.AcceptStreamAsync(cancellationToken);
        await ReadBytesAsync(serverStream.Input, 1, cancellationToken);

        return (clientStream, serverStream);
    }

    /// <summary>
    /// Writes <paramref name="payload"/> in 16 KiB pieces, each flushed, then ends the stream's sending
    /// direction with its FIN.
    /// </summary>
    private static async Task SendAndFinishAsync(MultiplexedStream stream, byte[] payload, CancellationToken cancellationToken)
    {
        const int piece = 16 * 1024;

        for (int offset = 0; offset < payload.Length; offset += piece)
        {
            await stream.Output.WriteAsync(payload.AsMemory(offset, Math.Min(piece, payload.Length - offset)), cancellationToken);
        }

        await stream.Output.CompleteAsync();
    }

    /// <summary>
    /// Writes on the supplied stream until the peer's teardown signal surfaces, returning the
    /// <see cref="QuicException"/> that carries it.
    /// </summary>
    private static async Task<QuicException> WaitForClientWriteFailureAsync(MultiplexedStream clientStream, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await clientStream.Output.WriteAsync(new byte[] { 0 }, cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
            catch (QuicException exception)
            {
                return exception;
            }
        }
    }

    /// <summary>
    /// Reads until the pipe holds at least <paramref name="count"/> octets, then hands them back unconsumed and
    /// unexamined, so the pipe's next read can return them without reading the stream.
    /// </summary>
    private static async Task BufferWithoutExaminingAsync(PipeReader reader, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);

            if (result.Buffer.Length >= count)
            {
                reader.AdvanceTo(result.Buffer.Start);
                return;
            }

            if (result.IsCompleted)
            {
                throw new InvalidOperationException($"The stream completed before {count} bytes were received.");
            }

            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }

    private static async Task<byte[]> ReadBytesAsync(PipeReader reader, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);

            if (result.Buffer.Length >= count)
            {
                byte[] bytes = result.Buffer.Slice(0, count).ToArray();

                reader.AdvanceTo(result.Buffer.GetPosition(count));

                return bytes;
            }

            if (result.IsCompleted)
            {
                throw new InvalidOperationException($"The stream completed before {count} bytes were received.");
            }

            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }

    /// <summary>
    /// A server stream whose peer withholds credit: a raw <see cref="QuicConnection"/> client with small receive
    /// windows opens a bidirectional stream, sends one octet and its FIN, and never reads. The server has read
    /// the octet and written <see cref="ResponseLength"/> octets, more than the client absorbs, so the tail of
    /// the response, and a FIN after it, stay undelivered until the client reads.
    /// </summary>
    private sealed class WithheldCreditPair : IAsyncDisposable
    {
        // The client absorbs its connection window plus what System.Net.Quic buffers for a stream it does not
        // read (64 KiB); the rest stays queued at the server, well inside what MsQuic buffers for its writes.
        public const int ResponseLength = 160 * 1024;

        private WithheldCreditPair(QuicConnectionListener listener, QuicConnection client, QuicStream clientStream, MultiplexedConnection server, MultiplexedStream serverStream)
        {
            Listener = listener;
            Client = client;
            ClientStream = clientStream;
            Server = server;
            ServerStream = serverStream;
        }

        public QuicConnectionListener Listener { get; }

        public QuicConnection Client { get; }

        public QuicStream ClientStream { get; }

        public MultiplexedConnection Server { get; }

        public MultiplexedStream ServerStream { get; }

        public static async Task<WithheldCreditPair> CreateAsync(
            X509Certificate2 certificate,
            Action<QuicConnectionListenerOptions> configureListener,
            CancellationToken cancellationToken)
        {
            SslApplicationProtocol applicationProtocol = new("cohesion-test");

            QuicConnectionListener listener = await QuicConnectionListener.CreateAsync(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                options.ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [applicationProtocol],
                    EnabledSslProtocols = SslProtocols.Tls13
                };
                options.DefaultStreamErrorCode = sentinelStreamErrorCode;
                options.DefaultCloseErrorCode = sentinelCloseErrorCode;

                configureListener(options);
            }, cancellationToken);

            QuicConnection? client = null;

            try
            {
                ValueTask<MultiplexedConnection> accepting = listener.AcceptAsync(cancellationToken);

                client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
                {
                    RemoteEndPoint = listener.EndPoint,
                    DefaultStreamErrorCode = sentinelStreamErrorCode,
                    DefaultCloseErrorCode = sentinelCloseErrorCode,
                    InitialReceiveWindowSizes = new QuicReceiveWindowSizes
                    {
                        Connection = 16 * 1024,
                        LocallyInitiatedBidirectionalStream = 16 * 1024
                    },
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        ApplicationProtocols = [applicationProtocol],
                        EnabledSslProtocols = SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true
                    }
                }, cancellationToken);

                MultiplexedConnection server = await accepting;

                QuicStream clientStream = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
                await clientStream.WriteAsync(new byte[] { 1 }, completeWrites: true, cancellationToken);

                MultiplexedStream serverStream = await server.AcceptStreamAsync(cancellationToken);
                await ReadBytesAsync(serverStream.Input, 1, cancellationToken);

                byte[] response = new byte[ResponseLength];
                Random.Shared.NextBytes(response);
                await SendAsync(serverStream, response, cancellationToken);

                return new WithheldCreditPair(listener, client, clientStream, server, serverStream);
            }
            catch
            {
                if (client is not null)
                {
                    await client.DisposeAsync();
                }

                await listener.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// Reads the client's side of the stream to its end, returning the number of octets read.
        /// </summary>
        public async Task<int> ReadToEndAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[16 * 1024];
            int total = 0;

            while (true)
            {
                int read = await ClientStream.ReadAsync(buffer, cancellationToken);

                if (read == 0)
                {
                    return total;
                }

                total += read;
            }
        }

        // The client goes first: closing it ends whatever the server still has to deliver, so the server's
        // stream and connection never wait on it.
        public async ValueTask DisposeAsync()
        {
            await ClientStream.DisposeAsync();
            await Client.DisposeAsync();
            await ServerStream.DisposeAsync();
            await Server.DisposeAsync();
            await Listener.DisposeAsync();
        }

        private static async Task SendAsync(MultiplexedStream stream, byte[] payload, CancellationToken cancellationToken)
        {
            const int piece = 16 * 1024;

            for (int offset = 0; offset < payload.Length; offset += piece)
            {
                await stream.Output.WriteAsync(payload.AsMemory(offset, Math.Min(piece, payload.Length - offset)), cancellationToken);
            }
        }
    }

    private sealed class LoopbackPair : IAsyncDisposable
    {
        private LoopbackPair(QuicConnectionListener listener, MultiplexedConnection client, MultiplexedConnection server)
        {
            Listener = listener;
            Client = client;
            Server = server;
        }

        public QuicConnectionListener Listener { get; }

        public MultiplexedConnection Client { get; }

        public MultiplexedConnection Server { get; }

        public static async Task<LoopbackPair> CreateAsync(
            X509Certificate2 certificate,
            CancellationToken cancellationToken,
            Action<QuicConnectionListenerOptions>? configureListener = null,
            Action<QuicConnectionFactoryOptions>? configureClient = null,
            Action? afterBind = null)
        {
            SslApplicationProtocol applicationProtocol = new("cohesion-test");

            QuicConnectionListener listener = await QuicConnectionListener.CreateAsync(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                options.ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [applicationProtocol],
                    EnabledSslProtocols = SslProtocols.Tls13
                };

                configureListener?.Invoke(options);
            }, cancellationToken);

            try
            {
                afterBind?.Invoke();
                ValueTask<MultiplexedConnection> acceptTask = listener.AcceptAsync(cancellationToken);

                QuicConnectionFactory factory = QuicConnectionFactory.Create(options =>
                {
                    options.ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        ApplicationProtocols = [applicationProtocol],
                        EnabledSslProtocols = SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true
                    };

                    configureClient?.Invoke(options);
                });

                MultiplexedConnection client = await factory.ConnectAsync(listener.EndPoint, cancellationToken);
                MultiplexedConnection server = await acceptTask;

                return new LoopbackPair(listener, client, server);
            }
            catch
            {
                await listener.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            await Listener.DisposeAsync();
        }
    }
}
