using System;
using System.IO;
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

/// <summary>Checks RFC 9218 stream-limit validation against the QUIC driver's advertised credit.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class Http3PriorityLimitRoundTripTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [QuicTheory(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 priority: A real QUIC update below the inbound stream limit should remain serviceable")]
    [InlineData(0L)]
    [InlineData(4L)]
    public async Task ReceiveAsync_OnRealQuicPriorityBelowStreamLimit_ShouldRecordPriority(long streamId)
    {
        // Arrange — two advertised streams permit request IDs 0 and 4. The handler waits for the
        // independent control drain to record the update before it sends its response.
        using CancellationTokenSource timeout = new(_testTimeout);
        CancellationToken cancellationToken = timeout.Token;
        TaskCompletionSource<HttpPriority> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(
            async exchange =>
            {
                Http3ConnectionContext context = ((Http3Context)exchange).Connection!;
                while (!context.TryGetRequestStreamPriority(streamId, out HttpPriority priority))
                {
                    await Task.Delay(10, cancellationToken);
                }

                context.TryGetRequestStreamPriority(streamId, out HttpPriority recorded);
                observed.TrySetResult(recorded);
                exchange.Response.StatusCode = HttpStatusCode.Ok;
            },
            configure: null,
            configureListener: null,
            cancellationToken,
            configureTransport: options => options.MaxBidirectionalStreamCount = 2);
        await using QuicConnection connection = await QuicConnection.ConnectAsync(CreateClientOptions(server.BaseUri.Port), cancellationToken);
        await using QuicStream control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
        await using QuicStream request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);

        // Act
        await control.WriteAsync(CreatePriorityControlStream(streamId, "u=1"), cancellationToken);
        await request.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/priority", "https", "localhost"), completeWrites: true, cancellationToken);
        using MemoryStream response = new();
        await request.CopyToAsync(response, cancellationToken);

        // Assert
        (await observed.Task.WaitAsync(cancellationToken)).Urgency.ShouldBe(1);
        response.Length.ShouldBeGreaterThan(0L);
    }

    [QuicTheory(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 priority: A real QUIC update at or beyond the inbound stream limit should close with H3_ID_ERROR")]
    [InlineData(8L, false)]
    [InlineData(12L, false)]
    [InlineData(8L, true)]
    public async Task ReceiveAsync_OnRealQuicPriorityBeyondStreamLimit_ShouldCloseWithIdError(long streamId, bool oversized)
    {
        // Arrange — no request streams are opened or disposed, so the advertised two-stream
        // allowance cannot replenish while this invalid update is checked.
        using CancellationTokenSource timeout = new(_testTimeout);
        CancellationToken cancellationToken = timeout.Token;
        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(
            _ => Task.CompletedTask,
            configure: null,
            configureListener: null,
            cancellationToken,
            configureTransport: options => options.MaxBidirectionalStreamCount = 2);
        await using QuicConnection connection = await QuicConnection.ConnectAsync(CreateClientOptions(server.BaseUri.Port), cancellationToken);
        await using QuicStream control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
        await using QuicStream serverControl = await connection.AcceptInboundStreamAsync(cancellationToken);
        string priority = oversized ? "u=1, " + new string('x', 17 * 1024) : "u=1";

        // Act — watch the server control stream to observe the connection-close code even if the
        // oversized write itself is interrupted by the close as soon as its element ID is received.
        QuicException closed = await Should.ThrowAsync<QuicException>(async () =>
        {
            await control.WriteAsync(CreatePriorityControlStream(streamId, priority), cancellationToken);
            await serverControl.CopyToAsync(Stream.Null, cancellationToken);
        });

        // Assert
        closed.QuicError.ShouldBe(QuicError.ConnectionAborted);
        closed.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.IdError);
    }

    private static byte[] CreatePriorityControlStream(long streamId, string priority)
        => HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
            [], ((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(streamId, priority)));

    private static QuicClientConnectionOptions CreateClientOptions(int port) => new()
    {
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
        DefaultStreamErrorCode = 0x10c,
        DefaultCloseErrorCode = 0x100,
        MaxInboundUnidirectionalStreams = 3,
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [SslApplicationProtocol.Http3],
            TargetHost = "localhost",
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        },
    };
}
