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
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConnectAsync_WithAcceptingListener_ShouldEstablishConnectedPair()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
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

    [Fact]
    public async Task OpenStreamAsync_Bidirectional_ShouldEchoAcrossPeers()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
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
        using CancellationTokenSource cancellation = new(TestTimeout);
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
        using CancellationTokenSource cancellation = new(TestTimeout);
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
        using CancellationTokenSource cancellation = new(TestTimeout);
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
        using CancellationTokenSource cancellation = new(TestTimeout);
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
        using CancellationTokenSource cancellation = new(TestTimeout);
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
            Action<QuicConnectionListenerOptions>? configureListener = null)
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
