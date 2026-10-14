using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The HTTP/3 extended CONNECT tunnel over real QUIC (RFC 9220, #1316): a raw QUIC client opens a
/// request stream, sends an extended CONNECT, and pushes a transfer far larger than QUIC's stream
/// flow-control windows through an echoing tunnel — so the server's tunnel reads must extend the
/// client's send credit while its writes wait on the client's — then ends its side with a FIN.
/// </summary>
/// <remarks>
/// The in-memory driver the other tunnel tests use has no flow control, so this is where QUIC's own
/// pacing is exercised. System.Net.Quic is Windows/Linux/macOS only, so the class is platform-annotated
/// and every test returns early when <see cref="QuicListener.IsSupported"/> is <see langword="false"/>,
/// matching <see cref="Http3RoundTripTests"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class Http3ExtendedConnectRoundTripTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    // RFC 9114 §8.1 — the codes the client uses when it aborts a stream or closes the connection.
    private const long requestCancelled = 0x10c;
    private const long noError = 0x100;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: A tunnel should echo 4 MiB through QUIC flow control over real QUIC and end with FINs from both sides")]
    public async Task Tunnel_OnLargeEchoOverRealQuic_ShouldCrossQuicFlowControlAndEndWithFins()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the handler accepts the tunnel and echoes until the client ends its side, then ends
        // its own by disposing the tunnel.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        byte[] upload = Http2TestPeer.CreateBody(4 * 1024 * 1024);

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(async exchange =>
        {
            await using Stream tunnel = await exchange.ExtendedConnect!.AcceptAsync(cancellationToken);
            byte[] buffer = new byte[16 * 1024];
            int read;

            while ((read = await tunnel.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await tunnel.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        },
        configure: null,
        configureListener: static options => options.Interceptors.Add(HttpExtendedConnectInterceptor.Create()),
        cancellationToken);

        await using QuicConnection connection = await QuicConnection.ConnectAsync(CreateClientOptions(server.BaseUri.Port), cancellationToken);
        await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
        Http3FrameCollector output = new(PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)));

        await stream.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "localhost")), cancellationToken);
        await output.ReadUntilAsync(collector => collector.Frames.Count >= 1, "the tunnel's response head");

        // Act — send 4 MiB in 64 KiB DATA frames, then the client's FIN, while reading the echo.
        Task sending = SendAsync(stream, upload, cancellationToken);
        await output.ReadUntilAsync(collector => collector.IsCompleted, "the server's FIN after the whole echo");
        await sending.WaitAsync(cancellationToken);

        // Assert — a 200 head, then the whole transfer back, then the server's FIN rather than a reset.
        output.Frames[0].FrameType.ShouldBe((long)Http3FrameType.Headers);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(output.Frames[0].Payload)[":status"].ShouldBe("200");
        output.Failure.ShouldBeNull();
        byte[] echoed = output.DataPayload();
        echoed.Length.ShouldBe(upload.Length);
        echoed.AsSpan().SequenceEqual(upload).ShouldBeTrue("the echo should match the upload octet for octet");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: After the server ends its side over real QUIC, the request stream should still deliver the client's data")]
    public async Task Tunnel_OnServerEndingItsSideFirstOverRealQuic_ShouldStillReadTheClientsData()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the handler sends a farewell, ends its side of the tunnel (the FIN), then reads what the
        // client still sends through the exchange's request body, which carries the CONNECT's DATA. Before
        // #1330 the QUIC driver disposed the stream with the FIN, so the client was stopped with the default
        // code and the read failed.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        byte[] farewell = "server done"u8.ToArray();
        byte[] upload = Http2TestPeer.CreateBody(1024 * 1024);
        TaskCompletionSource<byte[]> serverRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(async exchange =>
        {
            try
            {
                Stream tunnel = await exchange.ExtendedConnect!.AcceptAsync(cancellationToken);
                await tunnel.WriteAsync(farewell, cancellationToken);
                await tunnel.DisposeAsync();

                using MemoryStream received = new();
                await exchange.Request.Body.CopyToAsync(received, cancellationToken);
                serverRead.TrySetResult(received.ToArray());
            }
            catch (Exception exception)
            {
                serverRead.TrySetException(exception);
            }
        },
        configure: null,
        configureListener: static options => options.Interceptors.Add(HttpExtendedConnectInterceptor.Create()),
        cancellationToken);

        await using QuicConnection connection = await QuicConnection.ConnectAsync(CreateClientOptions(server.BaseUri.Port), cancellationToken);
        await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
        Http3FrameCollector output = new(PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)));

        await stream.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/proxy"),
            (":authority", "localhost")), cancellationToken);
        await output.ReadUntilAsync(collector => collector.IsCompleted, "the server's FIN after its farewell");

        // Act — the client keeps sending after the server's FIN: more than QUIC's stream window, so the
        // server must keep reading for the transfer to finish.
        await SendAsync(stream, upload, cancellationToken);
        byte[] read = await serverRead.Task.WaitAsync(cancellationToken);
        await stream.WritesClosed.WaitAsync(cancellationToken);

        // Assert — the server's side ended cleanly after its farewell, and it read the client's whole transfer.
        output.Failure.ShouldBeNull();
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(output.Frames[0].Payload)[":status"].ShouldBe("200");
        output.DataPayload().ShouldBe(farewell);
        read.Length.ShouldBe(upload.Length);
        read.AsSpan().SequenceEqual(upload).ShouldBeTrue("the server should read the client's data octet for octet");
    }

    private static async Task SendAsync(QuicStream stream, byte[] payload, CancellationToken cancellationToken)
    {
        const int chunk = 64 * 1024;

        for (int offset = 0; offset < payload.Length; offset += chunk)
        {
            byte[] frame = HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, payload.AsSpan(offset, Math.Min(chunk, payload.Length - offset)).ToArray());
            await stream.WriteAsync(frame, cancellationToken);
        }

        stream.CompleteWrites();
    }

    private static QuicClientConnectionOptions CreateClientOptions(int port) => new()
    {
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
        DefaultStreamErrorCode = requestCancelled,
        DefaultCloseErrorCode = noError,
        // The server opens its control stream (and QPACK streams when enabled) toward the client.
        MaxInboundUnidirectionalStreams = 3,
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
            TargetHost = "localhost",
            // The loopback certificate is a throwaway self-signed test certificate.
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        },
    };
}
