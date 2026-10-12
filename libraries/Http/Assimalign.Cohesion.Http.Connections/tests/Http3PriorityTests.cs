using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// RFC 9218 §7.2 tests for the HTTP/3 PRIORITY_UPDATE handling: payload parsing,
/// the frame-type constants, application of a request-stream update to the
/// engine's observable priority state, rejection of a push update, and parsing
/// of the request Priority header.
/// </summary>
public class Http3PriorityTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3: PRIORITY_UPDATE payload decodes element id and priority")]
    [InlineData(0L, "u=1, i", 0L, 1, true)]
    [InlineData(4L, "u=7", 4L, 7, false)]
    [InlineData(8L, "", 8L, 3, false)]
    public void Http3PriorityUpdate_TryParse_ShouldDecodeElementIdAndPriority(
        long elementId, string fieldValue, long expectedId, int expectedUrgency, bool expectedIncremental)
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(elementId, fieldValue);

        Http3PriorityUpdate.TryParse(payload, out long parsedId, out HttpPriority priority).ShouldBeTrue();
        parsedId.ShouldBe(expectedId);
        priority.Urgency.ShouldBe(expectedUrgency);
        priority.Incremental.ShouldBe(expectedIncremental);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: PRIORITY_UPDATE with an empty payload fails to parse")]
    public void Http3PriorityUpdate_TryParse_EmptyPayload_ShouldFail()
    {
        Http3PriorityUpdate.TryParse(Array.Empty<byte>(), out _, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: PRIORITY_UPDATE frame types match RFC 9218")]
    public void Http3FrameType_PriorityUpdate_ShouldMatchRfc9218()
    {
        ((long)Http3FrameType.PriorityUpdateRequest).ShouldBe(0xF0700L);
        ((long)Http3FrameType.PriorityUpdatePush).ShouldBe(0xF0701L);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should set the effective priority from the request Priority header")]
    public async Task Http3_OnPriorityHeader_ShouldSetEffectivePriority()
    {
        // RFC 9218 §4 — the request's Priority header initialises the effective
        // priority, observable on the yielded HTTP/3 context.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request(
            "GET", "/p", "https", "a",
            headers: new Dictionary<string, string> { ["priority"] = "u=2, i" });
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        Http3Context http3Context = httpContext.ShouldBeOfType<Http3Context>();
        http3Context.EffectivePriority.ShouldBe(new HttpPriority(2, incremental: true));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should apply a control-stream PRIORITY_UPDATE to the referenced stream")]
    public async Task Http3_OnControlPriorityUpdateRequest_ShouldRecordStreamPriority()
    {
        // RFC 9218 §7.2 — a request-stream PRIORITY_UPDATE (0xF0700) on the
        // control stream is parsed and applied to the referenced stream's
        // observable engine priority. A request stream is queued after the
        // control stream so the background drain has time to process the frame.
        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
                new (long, long)[] { (0x01, 0) },
                ((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(0, "u=1, i"))),
            ConnectionDirection.ReadOnly, completeInput: false);
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/g", "https", "a"));
        TestMultiplexedConnection connection = new(control, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/g");

        Http3ConnectionContext context = httpConnectionContext.ShouldBeOfType<Http3ConnectionContext>();

        // Draining runs on a background task — poll until the frame is applied.
        HttpPriority recorded = default;
        bool found = false;
        for (int attempt = 0; attempt < 100 && !found; attempt++)
        {
            found = context.TryGetRequestStreamPriority(0, out recorded);
            if (!found)
            {
                await Task.Delay(10);
            }
        }

        found.ShouldBeTrue();
        recorded.ShouldBe(new HttpPriority(1, incremental: true));

        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: A push PRIORITY_UPDATE on the control stream should close the connection with H3_ID_ERROR")]
    public async Task Http3_OnControlPriorityUpdatePush_ShouldAbortConnectionWithIdError()
    {
        // Arrange — RFC 9218 §7.2: the server promises no pushes, so a push PRIORITY_UPDATE (0xF0701)
        // references a push ID it never sent. The control stream stays open, so only the frame can end
        // the connection.
        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
                new (long, long)[] { (0x01, 0) },
                ((long)Http3FrameType.PriorityUpdatePush, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(0, "u=1"))),
            ConnectionDirection.ReadOnly,
            completeInput: false);
        TestMultiplexedConnection connection = new(control);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        // Act
        (await enumerator.MoveNextAsync()).ShouldBeFalse();
        await connection.ConnectionClosed.WhenCancelledAsync();

        // Assert
        connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.IdError);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3: PRIORITY_UPDATE below the transport stream limit should be accepted without overflow")]
    [InlineData(1L, 0L)]
    [InlineData(2L, 4L)]
    [InlineData(1L << 60, (1L << 62) - 4)]
    [InlineData(long.MaxValue, (1L << 62) - 4)]
    public async Task ReceiveAsync_OnPriorityUpdateBelowStreamLimit_ShouldRecordPriority(long limit, long streamId)
    {
        // Arrange — a generic transport facet reports cumulative inbound stream credit. Large credit
        // must not overflow a multiplication by four while checking the largest legal request ID.
        LimitedMultiplexedConnection? limited = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            decorate: connection => limited = new LimitedMultiplexedConnection(connection, limit));
        await using IAsyncEnumerator<IHttpContext> requests = peer.ConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        Task<bool> receive = requests.MoveNextAsync().AsTask();

        try
        {
            Connection control = await peer.OpenUnidirectionalStreamAsync();

            // Act
            await control.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
                [], ((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(streamId, "u=1"))));
            Http3ConnectionContext context = peer.ConnectionContext.ShouldBeOfType<Http3ConnectionContext>();
            await WaitForUrgencyAsync(context, streamId, urgency: 1);

            // Assert — a normal request remains serviceable after the accepted priority update.
            limited.ShouldNotBeNull().AbortReason.ShouldBeNull();
            Connection request = await peer.OpenRequestStreamAsync();
            request.Output.Write(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/accepted", "https", "a"));
            await request.Output.CompleteAsync();
            (await receive.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
            requests.Current.Request.Path.Value.ShouldBe("/accepted");
            await peer.ConnectionContext.SendAsync(requests.Current);
        }
        finally
        {
            // A failing assertion must settle the pending move before enumerator disposal, preserving
            // its original failure instead of replacing it with DisposeAsync's concurrent-move error.
            limited?.Abort();
            await ((Task)receive).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3: PRIORITY_UPDATE at or beyond the transport stream limit should close with H3_ID_ERROR")]
    [InlineData(0L, 0L, false)]
    [InlineData(1L, 4L, false)]
    [InlineData(2L, 8L, false)]
    [InlineData(2L, 12L, false)]
    [InlineData(2L, (1L << 62) - 4, false)]
    [InlineData((1L << 60) - 1, (1L << 62) - 4, false)]
    [InlineData(2L, 8L, true)]
    [InlineData(0L, 0L, true)]
    public async Task ReceiveAsync_OnPriorityUpdateBeyondStreamLimit_ShouldAbortWithIdError(long limit, long streamId, bool oversized)
    {
        // Arrange — oversized field values must not bypass validation of the leading element ID.
        LimitedMultiplexedConnection? limited = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            decorate: connection => limited = new LimitedMultiplexedConnection(connection, limit));
        await using IAsyncEnumerator<IHttpContext> requests = peer.ConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        Task<bool> receive = requests.MoveNextAsync().AsTask();

        try
        {
            Connection control = await peer.OpenUnidirectionalStreamAsync();
            string priority = oversized ? "u=1, " + new string('x', 17 * 1024) : "u=1";

            // Act
            await control.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
                [], ((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(streamId, priority))));

            // Assert — no request was dispatched, and the whole connection carries the RFC error.
            (await receive.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeFalse();
            limited.ShouldNotBeNull().AbortReason
                .ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.IdError);
        }
        finally
        {
            limited?.Abort();
            await ((Task)receive).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: PRIORITY_UPDATE frames for unopened streams should be buffered for a bounded number of streams")]
    public async Task Http3_OnPriorityUpdateFlood_ShouldBoundTheBufferedUpdates()
    {
        // Arrange — RFC 9218 §7: an update for a stream not yet opened is buffered, which costs the server
        // memory, so how many streams it buffers for is local policy. This driver exposes no stream-limit
        // facet, so the 128-entry backstop remains necessary. The client names 1,000 distinct future
        // streams in roughly 9-octet frames, then updates stream 0 again, which marks the end of the flood.
        List<(long FrameType, byte[] Payload)> frames = new();
        for (long index = 0; index < 1000; index++)
        {
            frames.Add(((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(4 * index, "u=5")));
        }

        frames.Add(((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(0, "u=1")));

        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(new (long, long)[] { (0x01, 0) }, frames.ToArray()),
            ConnectionDirection.ReadOnly,
            completeInput: false);
        TestMultiplexedConnection connection = new(control);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeFalse();
        Http3ConnectionContext context = httpConnectionContext.ShouldBeOfType<Http3ConnectionContext>();

        // Act
        await WaitForUrgencyAsync(context, streamId: 0, urgency: 1);

        // Assert — the first 128 streams named are buffered, the rest dropped without closing the connection.
        context.TrackedRequestStreamPriorityCount.ShouldBe(128);
        context.TryGetRequestStreamPriority(4 * 127, out _).ShouldBeTrue();
        context.TryGetRequestStreamPriority(4 * 128, out _).ShouldBeFalse();
        connection.AbortReason.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: A PRIORITY_UPDATE for a request stream that has ended should be discarded")]
    public async Task Http3_OnPriorityUpdateAfterStreamEnded_ShouldDiscardIt()
    {
        // Arrange — request stream 0 is answered, and its exchange ends.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Task<IHttpContext> dispatch = peer.NextContextAsync();

        Connection control = await peer.OpenUnidirectionalStreamAsync();
        await control.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x01, 0)));

        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/ended", "https", "a"));
        request.Output.Complete();
        IHttpContext exchange = await dispatch;
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Http3InMemoryPeer.ReadToEndAsync(request);
        Http3ConnectionContext context = peer.ConnectionContext.ShouldBeOfType<Http3ConnectionContext>();

        // Act — an update for the ended stream 0, then one for stream 4, not yet opened, which shows the first
        // was read.
        byte[] updates =
        [
            .. HttpProtocolPayloadFactory.CreateHttp3Frame((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(0, "u=1")),
            .. HttpProtocolPayloadFactory.CreateHttp3Frame((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(4, "u=2")),
        ];
        await control.Output.WriteAsync(updates);
        await WaitForUrgencyAsync(context, streamId: 4, urgency: 2);

        // Assert — the ended stream holds no entry, so the state stays bounded by the live and unopened streams.
        context.TryGetRequestStreamPriority(0, out _).ShouldBeFalse();
        context.TrackedRequestStreamPriorityCount.ShouldBe(1);
    }

    private static async Task WaitForUrgencyAsync(Http3ConnectionContext context, long streamId, int urgency)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!context.TryGetRequestStreamPriority(streamId, out HttpPriority recorded) || recorded.Urgency != urgency)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No PRIORITY_UPDATE with urgency {urgency} was recorded for stream {streamId}.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private sealed class LimitedMultiplexedConnection : MultiplexedConnection, IMultiplexedConnectionStreamLimits
    {
        private readonly MultiplexedConnection _inner;

        public LimitedMultiplexedConnection(MultiplexedConnection inner, long limit)
        {
            _inner = inner;
            InboundBidirectionalStreamLimit = limit;
        }

        public long InboundBidirectionalStreamLimit { get; }

        public Exception? AbortReason { get; private set; }

        public override ConnectionId Id => _inner.Id;

        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

        public override ConnectionCapabilities Capabilities => _inner.Capabilities;

        public override ConnectionState State => _inner.State;

        public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

        public override ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default)
            => _inner.AcceptStreamAsync(cancellationToken);

        public override ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
            => _inner.OpenStreamAsync(direction, cancellationToken);

        public override void Abort(Exception? reason = null)
        {
            AbortReason = reason;
            _inner.Abort(reason);
        }

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
