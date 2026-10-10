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

namespace Assimalign.Cohesion.Connections.Quic.Tests;

// Every test gates on QuicListener.IsSupported and no-ops where the platform lacks a QUIC
// implementation (for example, a missing libmsquic); xunit 2.x has no runtime skip.
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class QuicMultiplexedConnectionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

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
        pair.Server.ShouldBeAssignableTo<ITlsConnectionInfo>()!.ApplicationProtocol.ShouldBe(new SslApplicationProtocol("cohesion-test"));
        pair.Client.ShouldBeAssignableTo<ITlsConnectionInfo>()!.ApplicationProtocol.ShouldBe(new SslApplicationProtocol("cohesion-test"));
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
        ITlsConnectionInfo server = pair.Server.ShouldBeAssignableTo<ITlsConnectionInfo>()!;
        ITlsConnectionInfo client = pair.Client.ShouldBeAssignableTo<ITlsConnectionInfo>()!;

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
        pair.Server.ShouldBeAssignableTo<ITlsConnectionInfo>()!.RemoteCertificate!.Thumbprint.ShouldBe(clientCertificate.Thumbprint);
        pair.Client.ShouldBeAssignableTo<ITlsConnectionInfo>()!.RemoteCertificate!.Thumbprint.ShouldBe(certificate.Thumbprint);
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
        pair.Server.ShouldBeAssignableTo<ITlsConnectionInfo>()!.RemoteCertificate.ShouldBeNull();
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
        await using Connection clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellation.Token);

        // A freshly opened QUIC stream is not visible to the peer until data is flushed on it,
        // so write before accepting on the server side.
        await clientStream.Output.WriteAsync(payload, cancellation.Token);

        await using Connection serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);

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
        await using Connection clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.WriteOnly, cancellation.Token);

        // The outbound unidirectional stream has no readable half: its input is pre-completed.
        ReadResult inputResult = await clientStream.Input.ReadAsync(cancellation.Token);

        clientStream.Input.AdvanceTo(inputResult.Buffer.End);

        // A freshly opened QUIC stream is not visible to the peer until data is flushed on it,
        // so write before accepting on the server side.
        await clientStream.Output.WriteAsync(payload, cancellation.Token);

        await using Connection serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);

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
        await using Connection clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.WriteOnly, cancellation.Token);

        await clientStream.Output.WriteAsync(new byte[] { 1 }, cancellation.Token);

        await using Connection serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);

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

        await using Connection clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellation.Token);

        await clientStream.Output.WriteAsync(payload, cancellation.Token);

        await using Connection serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);

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
        await using Connection clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellation.Token);

        // A freshly opened QUIC stream is not visible to the peer until data is flushed on it.
        await clientStream.Output.WriteAsync(new byte[] { 1 }, cancellation.Token);

        await using Connection serverStream = await pair.Server.AcceptStreamAsync(cancellation.Token);
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
        (Connection clientStream, Connection serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            // Act
            serverStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!.AbortRead(0x42);

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
        (Connection clientStream, Connection serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            await clientStream.Output.WriteAsync(new byte[] { 2, 3, 4, 5 }, cancellation.Token);
            await BufferWithoutExaminingAsync(serverStream.Input, 4, cancellation.Token);

            // Act
            serverStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!.AbortRead(0x42);

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
        (Connection clientStream, Connection serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            // Act
            serverStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!.AbortWrite(0x43);

            // Assert — the client's read sees the server's sending direction reset with the code.
            QuicException exception = await Should.ThrowAsync<QuicException>(
                async () => await clientStream.Input.ReadAsync(cancellation.Token));

            exception.QuicError.ShouldBe(QuicError.StreamAborted);
            exception.ApplicationErrorCode.ShouldBe(0x43);
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
        (Connection clientStream, Connection serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            // Act — the reset an HTTP/3 server sends: both directions with the code, then the lifecycle abort.
            IMultiplexedStreamAbort abort = serverStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!;
            abort.AbortWrite(0x10e);
            abort.AbortRead(0x10e);
            serverStream.Abort();

            // Assert — both directions carry the code; the default never reaches the wire.
            QuicException read = await Should.ThrowAsync<QuicException>(
                async () => await clientStream.Input.ReadAsync(cancellation.Token));
            QuicException write = await WaitForClientWriteFailureAsync(clientStream, cancellation.Token);

            read.ApplicationErrorCode.ShouldBe(0x10e);
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
        (Connection clientStream, Connection serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);
        byte[] response = [9, 8, 7];

        await using (clientStream)
        await using (serverStream)
        {
            // Act
            serverStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!.AbortRead(0x100);
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
        (Connection clientStream, Connection serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            IMultiplexedStreamAbort abort = serverStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!;

            // Act / Assert
            Should.Throw<ArgumentOutOfRangeException>(() => abort.AbortRead(errorCode));
            Should.Throw<ArgumentOutOfRangeException>(() => abort.AbortWrite(errorCode));
            Should.Throw<ArgumentOutOfRangeException>(() => pair.Server.ShouldBeAssignableTo<IMultiplexedConnectionAbort>()!.Abort(errorCode));
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
        (Connection clientStream, Connection serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

        await using (clientStream)
        await using (serverStream)
        {
            // Act — an HTTP/3 connection error, H3_FRAME_UNEXPECTED; the later disposal must not replace it.
            pair.Server.ShouldBeAssignableTo<IMultiplexedConnectionAbort>()!.Abort(0x105, new InvalidOperationException("frame unexpected"));
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
        (Connection clientStream, Connection serverStream) = await OpenAcceptedStreamAsync(pair, cancellation.Token);

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

    private static void WithSentinelCodes(QuicConnectionListenerOptions options)
    {
        options.DefaultStreamErrorCode = sentinelStreamErrorCode;
        options.DefaultCloseErrorCode = sentinelCloseErrorCode;
    }

    /// <summary>
    /// Opens a bidirectional stream from the client and accepts it on the server, which sees a QUIC stream only
    /// once data is flushed on it; the server has read the one octet that announced it.
    /// </summary>
    private static async Task<(Connection Client, Connection Server)> OpenAcceptedStreamAsync(LoopbackPair pair, CancellationToken cancellationToken)
    {
        Connection clientStream = await pair.Client.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellationToken);
        await clientStream.Output.WriteAsync(new byte[] { 1 }, cancellationToken);

        Connection serverStream = await pair.Server.AcceptStreamAsync(cancellationToken);
        await ReadBytesAsync(serverStream.Input, 1, cancellationToken);

        return (clientStream, serverStream);
    }

    /// <summary>
    /// Writes on the supplied stream until the peer's teardown signal surfaces, returning the
    /// <see cref="QuicException"/> that carries it.
    /// </summary>
    private static async Task<QuicException> WaitForClientWriteFailureAsync(Connection clientStream, CancellationToken cancellationToken)
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
            Action<QuicConnectionFactoryOptions>? configureClient = null)
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
