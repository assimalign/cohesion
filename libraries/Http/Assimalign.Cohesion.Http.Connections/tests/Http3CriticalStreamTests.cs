using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The peer's critical streams over the in-memory driver (#1083): its control stream (RFC 9114 §6.2.1,
/// §7.2) and its QPACK encoder and decoder streams (RFC 9204 §4.2). Closing one, or breaking a control
/// stream rule, closes the connection with the RFC's code; frames the rules permit leave it serving.
/// </summary>
public class Http3CriticalStreamTests
{
    // RFC 9114 §8.1 codes, as the connection is closed with them.
    private const long closedCriticalStream = 0x104;
    private const long frameUnexpected = 0x105;
    private const long frameError = 0x106;
    private const long idError = 0x108;
    // RFC 9204 §6.
    private const long qpackDecoderStreamError = 0x202;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: The peer ending its control stream should close the connection with H3_CLOSED_CRITICAL_STREAM")]
    public async Task ReceiveAsync_OnPeerControlStreamEnded_ShouldAbortConnectionWithClosedCriticalStream()
    {
        // Arrange — the control stream opens with SETTINGS and stays open while a request is served.
        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x01, 0)),
            ConnectionDirection.ReadOnly,
            completeInput: false);
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/served", "https", "a"));
        TestMultiplexedConnection connection = new(control, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/served");
        connection.AbortReason.ShouldBeNull();

        // Act — the peer ends its control stream (FIN).
        control.CompleteInput();
        await connection.ConnectionClosed.WhenCancelledAsync();

        // Assert
        connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.ClosedCriticalStream);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: The peer resetting its control stream should close the connection with H3_CLOSED_CRITICAL_STREAM")]
    public async Task ReceiveAsync_OnPeerControlStreamReset_ShouldAbortConnectionWithClosedCriticalStream()
    {
        // Arrange — a live client whose control stream carries SETTINGS and a PRIORITY_UPDATE for stream 0.
        // Once the update is recorded, the drain is past SETTINGS and waiting on the open stream.
        CodeRecordingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(decorate: inner => server = new CodeRecordingMultiplexedConnection(inner));
        Task<bool> receive = peer.MoveNextAsync();

        Connection control = await peer.OpenUnidirectionalStreamAsync();
        await control.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
            new (long, long)[] { (0x01, 0) },
            ((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(0, "u=1"))));
        await WaitForPriorityAsync(peer.ConnectionContext.ShouldBeOfType<Http3ConnectionContext>(), streamId: 0);
        server!.AbortErrorCode.ShouldBeNull();

        // Act — the client resets its control stream (RESET_STREAM).
        control.DirectionalAbort.ShouldNotBeNull().AbortWrite(0x10c);
        await server.ConnectionClosed.WhenCancelledAsync();

        // Assert — the connection closes with the code, and the receive enumeration ends.
        server.AbortErrorCode.ShouldBe(closedCriticalStream);
        (await receive.WaitAsync(_timeout)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: The control stream ending inside a frame should close the connection with H3_FRAME_ERROR")]
    public async Task ReceiveAsync_OnControlStreamEndingInsideFrame_ShouldAbortConnectionWithFrameError()
    {
        // Arrange — RFC 9114 §7.1: a frame of reserved type declares ten octets; the stream ends after three.
        byte[] payload =
        [
            .. HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x01, 0)),
            .. HttpProtocolPayloadFactory.CreateHttp3FrameHeader(0x21, 10),
            1, 2, 3,
        ];

        // Act
        TestMultiplexedConnection connection = await DriveControlStreamAsync(payload, completeInput: true);

        // Assert
        connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.FrameError);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: A frame the control stream does not permit should close the connection with its RFC 9114 code")]
    [InlineData(0x00L, frameUnexpected)] // DATA (§7.2.1)
    [InlineData(0x01L, frameUnexpected)] // HEADERS (§7.2.2)
    [InlineData(0x02L, frameUnexpected)] // reserved: HTTP/2 PRIORITY (§7.2.8)
    [InlineData(0x04L, frameUnexpected)] // a second SETTINGS (§7.2.4)
    [InlineData(0x05L, frameUnexpected)] // PUSH_PROMISE, which a client never sends (§7.2.5)
    [InlineData(0x06L, frameUnexpected)] // reserved: HTTP/2 PING
    [InlineData(0x08L, frameUnexpected)] // reserved: HTTP/2 WINDOW_UPDATE
    [InlineData(0x09L, frameUnexpected)] // reserved: HTTP/2 CONTINUATION
    [InlineData(0x03L, idError)]         // CANCEL_PUSH for a push the server never promised (§7.2.3)
    public async Task ReceiveAsync_OnProhibitedControlFrame_ShouldAbortConnectionWithCode(long frameType, long expectedCode)
    {
        // Arrange — the control stream stays open, so only the frame can end the connection.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
            new (long, long)[] { (0x01, 0) },
            (frameType, HttpProtocolPayloadFactory.CreateHttp3VarintPayload(0)));

        // Act
        TestMultiplexedConnection connection = await DriveControlStreamAsync(payload, completeInput: false);

        // Assert
        ((long)connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode).ShouldBe(expectedCode);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: A request PRIORITY_UPDATE that names no request stream should close the connection")]
    [InlineData(new byte[] { 0x02, (byte)'u', (byte)'=', (byte)'1' }, idError)]    // stream 2: server-initiated bidirectional
    [InlineData(new byte[] { 0x03, (byte)'u', (byte)'=', (byte)'1' }, idError)]    // stream 3: server-initiated unidirectional
    [InlineData(new byte[] { 0x40 }, frameError)]                                     // a two-octet element ID cut short
    public async Task ReceiveAsync_OnPriorityUpdateForNoRequestStream_ShouldAbortConnection(byte[] priorityPayload, long expectedCode)
    {
        // Arrange — RFC 9218 §7.2: the request variant must reference a client-initiated bidirectional stream.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
            new (long, long)[] { (0x01, 0) },
            ((long)Http3FrameType.PriorityUpdateRequest, priorityPayload));

        // Act
        TestMultiplexedConnection connection = await DriveControlStreamAsync(payload, completeInput: false);

        // Assert
        ((long)connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode).ShouldBe(expectedCode);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: A GOAWAY or MAX_PUSH_ID that breaks its rules should close the connection with its RFC 9114 code")]
    [InlineData(0x07L, new byte[] { 0x04 }, new byte[] { 0x08 }, idError)]  // GOAWAY raises its identifier (§5.2)
    [InlineData(0x0DL, new byte[] { 0x08 }, new byte[] { 0x04 }, idError)]  // MAX_PUSH_ID lowers the maximum (§7.2.7)
    [InlineData(0x07L, new byte[] { 0x04, 0x00 }, null, frameError)]        // GOAWAY with an octet after its identifier (§7.1)
    [InlineData(0x0DL, new byte[] { }, null, frameError)]                   // MAX_PUSH_ID without its identifier
    [InlineData(0x07L, new byte[] { 0x40 }, null, frameError)]              // GOAWAY whose two-octet identifier is cut short
    public async Task ReceiveAsync_OnGoAwayOrMaxPushIdBreakingItsRules_ShouldAbortConnection(long frameType, byte[] first, byte[]? second, long expectedCode)
    {
        // Arrange — the control stream stays open, so only the frames can end the connection.
        (long, byte[])[] frames = second is null ? [(frameType, first)] : [(frameType, first), (frameType, second)];
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(new (long, long)[] { (0x01, 0) }, frames);

        // Act
        TestMultiplexedConnection connection = await DriveControlStreamAsync(payload, completeInput: false);

        // Assert
        ((long)connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode).ShouldBe(expectedCode);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: A GOAWAY that lowers its identifier and a MAX_PUSH_ID that raises its maximum should leave the connection open")]
    public async Task ReceiveAsync_OnGoAwayAndMaxPushIdInOrder_ShouldKeepServing()
    {
        // Arrange — both orders RFC 9114 permits, then a PRIORITY_UPDATE that shows the drain read past them.
        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
                new (long, long)[] { (0x01, 0) },
                ((long)Http3FrameType.GoAway, HttpProtocolPayloadFactory.CreateHttp3VarintPayload(8)),
                ((long)Http3FrameType.GoAway, HttpProtocolPayloadFactory.CreateHttp3VarintPayload(4)),
                ((long)Http3FrameType.MaxPushId, HttpProtocolPayloadFactory.CreateHttp3VarintPayload(4)),
                ((long)Http3FrameType.MaxPushId, HttpProtocolPayloadFactory.CreateHttp3VarintPayload(16_384)),
                ((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(4, "u=6"))),
            ConnectionDirection.ReadOnly,
            completeInput: false);
        TestMultiplexedConnection connection = new(control);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        // Act
        (await enumerator.MoveNextAsync()).ShouldBeFalse();
        await WaitForPriorityAsync(connectionContext.ShouldBeOfType<Http3ConnectionContext>(), streamId: 4);

        // Assert
        connection.AbortReason.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: A QPACK decoder instruction the server's encoder cannot have caused should close the connection with QPACK_DECODER_STREAM_ERROR")]
    [InlineData(new byte[] { 0x80 })]                                                       // Section Acknowledgment, stream 0 (RFC 9204 §4.4.1)
    [InlineData(new byte[] { 0x44, 0x81 })]                                                 // a Stream Cancellation, then a Section Acknowledgment
    [InlineData(new byte[] { 0x01 })]                                                       // Insert Count Increment of 1 (§4.4.3)
    [InlineData(new byte[] { 0x00 })]                                                       // Insert Count Increment of 0
    [InlineData(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })] // a Stream Cancellation whose stream ID overflows
    public async Task ReceiveAsync_OnUnexpectedQPackDecoderInstruction_ShouldAbortWithDecoderStreamError(byte[] instructions)
    {
        // Arrange — the server's field sections never reference the dynamic table, so only a Stream Cancellation
        // may arrive on the peer's decoder stream, which stays open.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(0x03, instructions);

        // Act
        TestMultiplexedConnection connection = await DriveControlStreamAsync(payload, completeInput: false);

        // Assert
        ((long)connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode).ShouldBe(qpackDecoderStreamError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: Stream Cancellations on the QPACK decoder stream should be read without closing the connection")]
    public async Task ReceiveAsync_OnQPackStreamCancellations_ShouldKeepServing()
    {
        // Arrange — two Stream Cancellations, one with a multi-octet stream ID; a request is then served, and the
        // decoder stream ends, which shows the instructions were read without an error of their own.
        TestConnection decoder = new(
            HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(0x03, [0x40, 0x7F, 0x01]),
            ConnectionDirection.ReadOnly,
            completeInput: false);
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/served", "https", "a"));
        TestMultiplexedConnection connection = new(decoder, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/served");

        // Act
        decoder.CompleteInput();
        await connection.ConnectionClosed.WhenCancelledAsync();

        // Assert
        connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.ClosedCriticalStream);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: An oversized PRIORITY_UPDATE should be skipped and the control stream still read")]
    public async Task ReceiveAsync_OnOversizedPriorityUpdate_ShouldSkipItAndApplyTheNext()
    {
        // Arrange — a PRIORITY_UPDATE beyond the 16 KiB the drain buffers, then one it can apply. Before #1083
        // the oversized frame stopped the drain, leaving the rest of the critical stream unread.
        byte[] oversized = HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(0, "u=1, " + new string('x', 17 * 1024));
        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
                new (long, long)[] { (0x01, 0) },
                ((long)Http3FrameType.PriorityUpdateRequest, oversized),
                ((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(4, "u=6"))),
            ConnectionDirection.ReadOnly,
            completeInput: false);
        TestMultiplexedConnection connection = new(control);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        // Act
        (await enumerator.MoveNextAsync()).ShouldBeFalse();
        Http3ConnectionContext context = connectionContext.ShouldBeOfType<Http3ConnectionContext>();
        await WaitForPriorityAsync(context, streamId: 4);

        // Assert — the oversized update had no effect, the next one applied, and the connection is open.
        context.TryGetRequestStreamPriority(0, out _).ShouldBeFalse();
        context.TryGetRequestStreamPriority(4, out HttpPriority applied).ShouldBeTrue();
        applied.Urgency.ShouldBe(6);
        connection.AbortReason.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: The peer ending a QPACK stream should close the connection with H3_CLOSED_CRITICAL_STREAM")]
    [InlineData(0x02L, false)] // encoder stream, dynamic table disabled: watched
    [InlineData(0x02L, true)]  // encoder stream, dynamic table enabled: drained
    [InlineData(0x03L, false)] // decoder stream
    [InlineData(0x03L, true)]
    public async Task ReceiveAsync_OnPeerQPackStreamEnded_ShouldAbortConnectionWithClosedCriticalStream(long streamType, bool dynamicTable)
    {
        // Arrange — RFC 9204 §4.2: the sender must not close either QPACK stream. A request served first shows
        // the open stream is accepted; the stream then ends.
        TestConnection qpack = new(
            HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(streamType),
            ConnectionDirection.ReadOnly,
            completeInput: false);
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/served", "https", "a"));
        TestMultiplexedConnection connection = new(qpack, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), http3 =>
        {
            if (dynamicTable)
            {
                http3.QPack.MaxTableCapacity = 4096;
                http3.QPack.MaxBlockedStreams = 16;
            }
        });

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/served");
        connection.AbortReason.ShouldBeNull();

        // Act
        qpack.CompleteInput();
        await connection.ConnectionClosed.WhenCancelledAsync();

        // Assert
        connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.ClosedCriticalStream);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Critical Streams: Open critical streams should not close the connection when it is disposed")]
    public async Task DisposeAsync_WithOpenCriticalStreams_ShouldNotAbortConnection()
    {
        // Arrange — control, QPACK encoder, and QPACK decoder streams, all left open by the peer.
        TestConnection control = new(HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x01, 0)), ConnectionDirection.ReadOnly, completeInput: false);
        TestConnection encoder = new(HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(0x02), ConnectionDirection.ReadOnly, completeInput: false);
        TestConnection decoder = new(HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(0x03), ConnectionDirection.ReadOnly, completeInput: false);
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/served", "https", "a"));
        TestMultiplexedConnection connection = new(control, encoder, decoder, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnection httpConnection = await listener.AcceptOrListenAsync();
        IHttpConnectionContext connectionContext = await httpConnection.OpenAsync();

        await using (IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator())
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            (await enumerator.MoveNextAsync()).ShouldBeFalse();
        }

        // Act
        await httpConnection.DisposeAsync();

        // Assert — the drains end with the connection; none of them reports a closed critical stream.
        connection.IsDisposed.ShouldBeTrue();
        connection.AbortReason.ShouldBeNull();
        connection.State.ShouldBe(ConnectionState.Closed);
    }

    private static async Task<TestMultiplexedConnection> DriveControlStreamAsync(byte[] payload, bool completeInput)
    {
        TestConnection control = new(payload, ConnectionDirection.ReadOnly, completeInput: completeInput);
        TestMultiplexedConnection connection = new(control);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using (IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator())
        {
            (await enumerator.MoveNextAsync()).ShouldBeFalse();
        }

        await connection.ConnectionClosed.WhenCancelledAsync();
        return connection;
    }

    private static async Task WaitForPriorityAsync(Http3ConnectionContext context, long streamId)
    {
        DateTime deadline = DateTime.UtcNow + _timeout;

        while (!context.TryGetRequestStreamPriority(streamId, out _))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No PRIORITY_UPDATE was recorded for stream {streamId}.");
            }

            await Task.Delay(10);
        }
    }
}
