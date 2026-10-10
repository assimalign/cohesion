using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The RFC 9114 §8.1 codes the HTTP/3 transport chooses, observed on the wire of a real QUIC connection
/// (#1080). The QUIC driver carries a code per stream direction and on the connection close, so the codes
/// reach a real peer and not just the in-memory one: <c>STOP_SENDING(H3_NO_ERROR)</c> after a complete
/// response, which .NET's <c>HttpClient</c> accepts where it fails the request on the driver's default
/// <c>H3_REQUEST_CANCELLED</c>; the stream error's code on a reset; and the connection error's code on the
/// connection close.
/// </summary>
/// <remarks>
/// System.Net.Quic is Windows/Linux/macOS only, so the class is platform-annotated and every test returns
/// early when <see cref="QuicListener.IsSupported"/> is <see langword="false"/>, matching
/// <see cref="Http3RoundTripTests"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class Http3ErrorCodeRoundTripTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    // The client's own defaults, distinct from every code the server is expected to send.
    private const long clientStreamErrorCode = 0x10c;
    private const long clientCloseErrorCode = 0x100;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Error Codes: HttpClient should receive the complete response when the handler leaves a large upload unread over real QUIC")]
    public async Task SendAsync_OnLargeUnreadUploadOverRealQuic_ShouldDeliverTheCompleteResponse()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — 4 MiB is far past the QUIC stream's receive window, so the client is still uploading when
        // the response completes. The server refuses the rest with STOP_SENDING(H3_NO_ERROR) instead of
        // draining it, and the client must keep the complete response (RFC 9114 §4.1).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        byte[] upload = new byte[4 * 1024 * 1024];

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(exchange =>
        {
            exchange.Response.StatusCode = HttpStatusCode.Ok;
            exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("complete without the upload"));
            return Task.CompletedTask;
        }, cancellationToken);

        // Act
        using HttpClient client = CreateHttp3Client();
        using HttpResponseMessage response = await PostExactHttp3Async(client, new Uri(server.BaseUri, "/ignore"), upload, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Assert
        response.Version.ShouldBe(NetHttpVersion.Version30);
        ((int)response.StatusCode).ShouldBe(200);
        body.ShouldBe("complete without the upload");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Error Codes: HttpClient should receive a complete streamed response when the handler leaves a large upload unread over real QUIC")]
    public async Task SendAsync_OnStreamedResponseWithLargeUnreadUploadOverRealQuic_ShouldDeliverTheCompleteResponse()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the response goes out through the streaming sink, whose completion ends the stream, so
        // the refusal of the unread upload has to precede the sink's FIN just as it precedes a buffered one.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        byte[] upload = new byte[4 * 1024 * 1024];

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(
            async exchange =>
            {
                exchange.Response.StatusCode = HttpStatusCode.Ok;
                await exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("streamed "), cancellationToken);
                await exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("without the upload"), cancellationToken);
            },
            configure: null,
            configureListener: static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()),
            cancellationToken);

        // Act
        using HttpClient client = CreateHttp3Client();
        using HttpResponseMessage response = await PostExactHttp3Async(client, new Uri(server.BaseUri, "/ignore"), upload, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Assert
        ((int)response.StatusCode).ShouldBe(200);
        body.ShouldBe("streamed without the upload");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Error Codes: HttpClient should receive the 413 when a large upload exceeds MaxRequestBodySize over real QUIC")]
    public async Task SendAsync_OnLargeUploadOverMaxRequestBodySizeOverRealQuic_ShouldDeliverThe413()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — a 1 KiB cap against a 1 MiB upload: the transport answers 413 and refuses the rest.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(
            async exchange =>
            {
                try
                {
                    await exchange.Request.Body.CopyToAsync(Stream.Null, cancellationToken);
                }
                catch (IOException)
                {
                    // The body was rejected for its size; the send path answers 413.
                }
            },
            http3 => http3.Limits.MaxRequestBodySize = 1024,
            cancellationToken);

        // Act
        using HttpClient client = CreateHttp3Client();
        using HttpResponseMessage response = await PostExactHttp3Async(client, new Uri(server.BaseUri, "/upload"), new byte[1024 * 1024], cancellationToken);

        // Assert
        ((int)response.StatusCode).ShouldBe(413);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Error Codes: An unread request should be stopped with H3_NO_ERROR after the complete response over real QUIC")]
    public async Task SendAsync_OnUnreadRequestOverRealQuic_ShouldStopSendingWithNoError()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — a raw client sends a request head and part of a body, and keeps its side open.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(exchange =>
        {
            exchange.Response.StatusCode = HttpStatusCode.Ok;
            exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("done"));
            return Task.CompletedTask;
        }, cancellationToken);

        await using QuicConnection connection = await QuicConnection.ConnectAsync(CreateClientOptions(server.BaseUri.Port), cancellationToken);
        await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);

        // Act
        await stream.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "localhost"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[512])), cancellationToken);

        byte[] responseOctets = await ReadToEndAsync(stream, cancellationToken);

        // Assert — the complete response and its FIN, then the stop with the code RFC 9114 §4.1 asks for.
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(responseOctets);
        frames.Count.ShouldBe(2);
        Encoding.ASCII.GetString(frames[1].Payload).ShouldBe("done");

        QuicException stop = await Should.ThrowAsync<QuicException>(() => stream.WritesClosed.WaitAsync(cancellationToken));
        stop.QuicError.ShouldBe(QuicError.StreamAborted);
        stop.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.NoError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Error Codes: A malformed request should be reset with H3_MESSAGE_ERROR over real QUIC")]
    public async Task Receive_OnMalformedRequestOverRealQuic_ShouldResetWithMessageError()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — a :path that is not origin-form is a malformed request, a stream error (RFC 9114 §4.1.2).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(_ => Task.CompletedTask, cancellationToken);
        await using QuicConnection connection = await QuicConnection.ConnectAsync(CreateClientOptions(server.BaseUri.Port), cancellationToken);
        await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);

        // Act
        await stream.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "relative/path", "https", "localhost"), completeWrites: true, cancellationToken);

        // Assert — the response direction is reset with the stream error's code, not the driver's default.
        QuicException reset = await Should.ThrowAsync<QuicException>(() => ReadToEndAsync(stream, cancellationToken));
        reset.QuicError.ShouldBe(QuicError.StreamAborted);
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.MessageError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Error Codes: A connection error should close the connection with its code over real QUIC")]
    public async Task Receive_OnConnectionErrorOverRealQuic_ShouldCloseWithItsCode()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — DATA before HEADERS on a request stream is a connection error, H3_FRAME_UNEXPECTED
        // (RFC 9114 §4.1).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(_ => Task.CompletedTask, cancellationToken);
        await using QuicConnection connection = await QuicConnection.ConnectAsync(CreateClientOptions(server.BaseUri.Port), cancellationToken);
        await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);

        // Act
        await stream.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[4]), cancellationToken);

        // Assert — the connection closes with the error's code, not the driver's default H3_NO_ERROR.
        QuicException closed = await Should.ThrowAsync<QuicException>(() => ReadToEndAsync(stream, cancellationToken));
        closed.QuicError.ShouldBe(QuicError.ConnectionAborted);
        closed.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.UnexpectedFrame);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Error Codes: A reserved unidirectional stream type should be stopped with H3_STREAM_CREATION_ERROR over real QUIC")]
    public async Task Receive_OnReservedUnidirectionalStreamTypeOverRealQuic_ShouldStopSendingWithStreamCreationError()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — 0x21 is a reserved stream type (0x1f × N + 0x21). RFC 9114 §6.2: its recipient MUST abort
        // reading it or discard its data, and SHOULD abort with H3_STREAM_CREATION_ERROR. The client keeps its
        // side open, as a peer still sending would.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(_ => Task.CompletedTask, cancellationToken);
        await using QuicConnection connection = await QuicConnection.ConnectAsync(CreateClientOptions(server.BaseUri.Port), cancellationToken);
        await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);

        // Act
        await stream.WriteAsync(new byte[] { 0x21, 0x00, 0x00 }, cancellationToken);

        // Assert — STOP_SENDING with the code the RFC recommends, not the driver's default.
        QuicException stop = await Should.ThrowAsync<QuicException>(() => stream.WritesClosed.WaitAsync(cancellationToken));
        stop.QuicError.ShouldBe(QuicError.StreamAborted);
        stop.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.StreamCreationError);
    }

    private static async Task<byte[]> ReadToEndAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        using MemoryStream received = new();
        byte[] buffer = new byte[4096];
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            received.Write(buffer, 0, read);
        }

        return received.ToArray();
    }

    private static byte[] Combine(params byte[][] parts)
    {
        using MemoryStream combined = new();

        foreach (byte[] part in parts)
        {
            combined.Write(part, 0, part.Length);
        }

        return combined.ToArray();
    }

    private static async Task<HttpResponseMessage> PostExactHttp3Async(HttpClient client, Uri uri, byte[] content, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(ClientHttpMethod.Post, uri)
        {
            Version = NetHttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(content)
        };

        return await client.SendAsync(request, cancellationToken);
    }

    private static HttpClient CreateHttp3Client()
    {
        HttpClientHandler handler = new()
        {
            // The loopback certificate is a throwaway self-signed test cert; accept it unconditionally.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        return new HttpClient(handler, disposeHandler: true);
    }

    private static QuicClientConnectionOptions CreateClientOptions(int port) => new()
    {
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
        DefaultStreamErrorCode = clientStreamErrorCode,
        DefaultCloseErrorCode = clientCloseErrorCode,
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
