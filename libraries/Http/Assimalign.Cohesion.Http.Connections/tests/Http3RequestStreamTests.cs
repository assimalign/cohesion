using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The HTTP/3 request-stream reader (#1066): requests are dispatched at their HEADERS frame and their
/// bodies read lazily, streams are processed concurrently, the body-size cap answers 413 and stops
/// reading, HEAD responses carry no DATA, and malformed frames map to the RFC 9114 §8 stream or
/// connection error instead of an exception. Live streams run over the in-memory multiplexed driver
/// (<see cref="Http3InMemoryPeer"/>); fixed wire sequences use the pre-filled
/// <see cref="TestMultiplexedConnection"/>.
/// </summary>
public class Http3RequestStreamTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------ incremental reads and concurrency

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: Should dispatch at HEADERS and read a large body incrementally as it arrives")]
    public async Task ReceiveAsync_OnLargeBody_ShouldDispatchAtHeadersAndReadBodyIncrementally()
    {
        // Arrange — only the HEADERS frame is on the wire: no DATA and no FIN yet.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));

        // Act — the request is dispatched before a single body octet exists.
        IHttpContext context = await peer.NextContextAsync();
        context.Request.Path.Value.ShouldBe("/upload");

        // Assert — 1 MiB streams through in 64 KiB DATA frames; each frame is read back before the next
        // is even sent, so the body is never accumulated ahead of the reader.
        const int chunkSize = 64 * 1024;
        byte[] chunk = new byte[chunkSize];
        byte[] received = new byte[chunkSize];

        for (int index = 0; index < 16; index++)
        {
            Array.Fill(chunk, (byte)index);
            await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, chunk));

            await context.Request.Body.ReadExactlyAsync(received).AsTask().WaitAsync(_timeout);
            received.ShouldBe(chunk);
        }

        request.Output.Complete();
        (await context.Request.Body.ReadAsync(new byte[1]).AsTask().WaitAsync(_timeout)).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: Should dispatch a second stream while the first request's body is still in flight")]
    public async Task ReceiveAsync_OnSecondStreamWhileFirstBodyInFlight_ShouldDispatchSecondRequest()
    {
        // Arrange — the first request sends its head and part of its body, then pauses.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection upload = await peer.OpenRequestStreamAsync();
        await upload.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("part-1;"))));

        IHttpContext first = await peer.NextContextAsync();
        byte[] partOne = new byte[7];
        await first.Request.Body.ReadExactlyAsync(partOne).AsTask().WaitAsync(_timeout);
        Encoding.ASCII.GetString(partOne).ShouldBe("part-1;");

        // Act — a second request arrives while the first body has neither finished nor been drained.
        Connection other = await peer.OpenRequestStreamAsync();
        await other.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/second", "https", "a"));
        other.Output.Complete();

        IHttpContext second = await peer.NextContextAsync();

        // Assert — it is dispatched now, and the first body then completes independently.
        second.Request.Path.Value.ShouldBe("/second");

        await upload.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("part-2")));
        upload.Output.Complete();

        using StreamReader reader = new(first.Request.Body);
        (await reader.ReadToEndAsync().WaitAsync(_timeout)).ShouldBe("part-2");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A stream stalled before its HEADERS should not hold back later streams")]
    public async Task ReceiveAsync_OnStreamStalledBeforeHeaders_ShouldStillDispatchLaterStreams()
    {
        // Arrange — stream 0 is opened but sends nothing; stream 4 sends a complete request.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection stalled = await peer.OpenRequestStreamAsync();
        Connection ready = await peer.OpenRequestStreamAsync();
        await ready.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/ready", "https", "a"));
        ready.Output.Complete();

        // Act
        IHttpContext context = await peer.NextContextAsync();

        // Assert — the ready request was dispatched past the stalled one. When the receive enumeration
        // ends, the never-dispatched stream is rejected so its client may retry it (RFC 9114 §4.1.1).
        context.Request.Path.Value.ShouldBe("/ready");

        await peer.StopReceivingAsync();

        ConnectionResetException rejection = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(stalled));
        rejection.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestRejected);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A blocking request hook should stall only its own stream")]
    public async Task ReceiveAsync_OnBlockingRequestHook_ShouldStillDispatchOtherStreams()
    {
        // The seam contract forbids blocking in a parse-path hook, but the transport must not let one that
        // does anyway stall acceptance: hooks run off the accept loop.
        using GatedHeadInterceptor gate = new();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            configureListener: options => options.Interceptors.Add(gate));

        // The first wait starts the server's receive loop; the slow request's hook then blocks.
        Task<IHttpContext> firstDispatch = peer.NextContextAsync();

        Connection slow = await peer.OpenRequestStreamAsync();
        await slow.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/slow", "https", "a"));
        slow.Output.Complete();
        await gate.Entered.Task.WaitAsync(_timeout);

        Connection fast = await peer.OpenRequestStreamAsync();
        await fast.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/fast", "https", "a"));
        fast.Output.Complete();

        try
        {
            // Act / Assert — the fast request is dispatched while the slow hook is still blocked.
            (await firstDispatch).Request.Path.Value.ShouldBe("/fast");
        }
        finally
        {
            gate.Release();
        }

        (await peer.NextContextAsync()).Request.Path.Value.ShouldBe("/slow");
    }

    // ------------------------------------------------------------ exchanges outliving the receive enumeration

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: An exchange should read its body after the receive enumeration has ended")]
    public async Task ReadBody_AfterReceiveEnumerationEnded_ShouldReadWholeBody()
    {
        // The server may dispatch each exchange on its own task, so an exchange keeps reading its body
        // after ReceiveAsync has returned and its teardown has run.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("part-1;"))));

        IHttpContext context = await peer.NextContextAsync();

        // Act — the enumeration ends; only then does the rest of the body arrive.
        await peer.StopReceivingAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("part-2")));
        request.Output.Complete();

        // Assert — the whole body reads, and the response still goes out on the stream.
        using (StreamReader reader = new(context.Request.Body))
        {
            (await reader.ReadToEndAsync().WaitAsync(_timeout)).ShouldBe("part-1;part-2");
        }

        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("stored"));
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        Encoding.ASCII.GetString(frames.Single(frame => frame.FrameType == (long)Http3FrameType.Data).Payload).ShouldBe("stored");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A trailer section blocked on a QPACK insertion should decode after the receive enumeration has ended")]
    public async Task ReadBody_OnDynamicTrailersAfterEnumerationEnded_ShouldWaitForEncoderInsertion()
    {
        // Arrange — the dynamic table is enabled, and the peer's QPACK encoder stream is accepted while
        // the enumeration still runs.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(static http3 =>
        {
            http3.QPack.MaxTableCapacity = 4096;
            http3.QPack.MaxBlockedStreams = 16;
        });
        Task<IHttpContext> dispatch = peer.NextContextAsync();

        Connection encoder = await peer.OpenUnidirectionalStreamAsync();
        await encoder.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3QPackEncoderStream(
            HttpProtocolPayloadFactory.QPackSetCapacity(4096)));

        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello"))));

        IHttpContext context = await dispatch;
        await peer.StopReceivingAsync();

        // The trailer section references dynamic entry 0, which the encoder has not inserted yet
        // (encoded Required Insert Count 2 → 1, Delta Base 0, relative index 0 → absolute 0).
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3DynamicRequest(
            encodedRequiredInsertCount: 2,
            deltaBaseByte: 0x00,
            literalFields: [],
            0));
        request.Output.Complete();

        using StreamReader reader = new(context.Request.Body);
        Task<string> body = reader.ReadToEndAsync();

        // The decode blocks (RFC 9204 §2.1.2) rather than failing: the encoder drain outlives the
        // enumeration.
        await Task.Delay(100);
        body.IsCompleted.ShouldBeFalse();

        // Act — the insertion arrives only now.
        await encoder.Output.WriteAsync(HttpProtocolPayloadFactory.QPackInsertWithLiteralName("x-checksum", "abc123"));

        // Assert
        (await body.WaitAsync(_timeout)).ShouldBe("hello");
        context.Request.Trailers[new HttpHeaderKey("x-checksum")].Value.ShouldBe("abc123");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A body read should fail with a stream error when the connection closes underneath it")]
    public async Task ReadBody_WhenConnectionCloses_ShouldFailWithStreamError()
    {
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));

        IHttpContext context = await peer.NextContextAsync();
        await peer.StopReceivingAsync();
        Task<int> pending = context.Request.Body.ReadAsync(new byte[16]).AsTask();

        // Act — the QUIC connection goes away while the read waits for DATA.
        await peer.Server.DisposeAsync();

        // Assert — a clean stream error, promptly, rather than a hang or a cancellation nobody requested.
        IOException failure = await Should.ThrowAsync<IOException>(() => pending.WaitAsync(_timeout));
        failure.ShouldNotBeOfType<Http3ConnectionException>();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: SendAsync after CancelAsync on a started response should reset promptly")]
    public async Task SendAsync_AfterCancelOnStartedResponse_ShouldResetPromptly()
    {
        // Arrange — a streamed response is on the wire while the request body is still open, and the
        // exchange then faults: the server resets it through CancelAsync followed by SendAsync.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            configureListener: static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/stream", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[16])));

        IHttpContext context = await peer.NextContextAsync();
        IHttpResponseStreamingFeature streaming = context.Response.Streaming;
        await streaming.WriteAsync(Encoding.ASCII.GetBytes("partial"));
        await streaming.FlushAsync();

        // Act — nothing may park here: not the unread request body, not the response finalization.
        await context.CancelAsync();
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert — the stream is reset with H3_REQUEST_CANCELLED (RFC 9114 §4.1.1).
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(request));
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestCancelled);
    }

    // ------------------------------------------------------------ body-size cap (413) and stop-sending

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A body over MaxRequestBodySize should fail the read, answer 413, and stop reading with H3_NO_ERROR")]
    public async Task ReadBody_OnBodyOverMaxRequestBodySize_ShouldAnswer413AndStopSending()
    {
        // Arrange — a 128 KiB DATA frame against a 1 KiB cap; the client keeps its side open.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MaxRequestBodySize = 1024);
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[128 * 1024])));

        IHttpContext context = await peer.NextContextAsync();

        // Act — the frame is rejected before any of its octets are delivered (RFC 9110 §15.5.14).
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[8192]).AsTask().WaitAsync(_timeout));
        await peer.ConnectionContext.SendAsync(context);

        // Assert — the response had not started, so the exchange is answered 413 with no content …
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        frames.Count.ShouldBe(1);
        frames[0].FrameType.ShouldBe((long)Http3FrameType.Headers);
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload);
        headers[":status"].ShouldBe("413");
        headers["content-length"].ShouldBe("0");

        // … and, the complete response sent, the server stops reading: STOP_SENDING(H3_NO_ERROR),
        // RFC 9114 §4.1.
        ConnectionResetException stop = await Should.ThrowAsync<ConnectionResetException>(() => request.Output.WriteAsync(new byte[1]).AsTask());
        stop.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.NoError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: 413 should replace a staged application response that is not already a 413")]
    public async Task SendAsync_OnRejectedBodyWithStagedResponse_ShouldAnswer413()
    {
        // An exception boundary that caught the body failure stages a 500; the body was rejected by the
        // transport for its size, so the exchange is still answered 413 while the head is uncommitted.
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[64]));
        TestConnection stream = new(payload);
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveSingleAsync(
            stream,
            http3 => http3.Limits.MaxRequestBodySize = 16);

        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[128]).AsTask());
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
        context.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("boundary problem document"));

        await connectionContext.SendAsync(context);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        frames.Count.ShouldBe(1);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"].ShouldBe("413");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A Content-Length over the cap should be rejected before any DATA is read")]
    public async Task ReadBody_OnContentLengthOverCap_ShouldRejectBeforeReadingData()
    {
        // The declared length alone decides: no DATA frame is ever sent, and the read still fails at once.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MaxRequestBodySize = 16);
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "a", headers: new Dictionary<string, string> { ["content-length"] = "4096" }));

        IHttpContext context = await peer.NextContextAsync();

        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[64]).AsTask().WaitAsync(_timeout));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A body a request hook reads over the cap should be answered 413 without dispatch")]
    public async Task ReceiveAsync_OnHookReadingBodyOverCap_ShouldAnswer413WithoutDispatch()
    {
        // A hook that reads the whole body before dispatch (as Content-Digest verification does) hits the
        // cap; no exchange exists, so the transport answers the rejection itself.
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[64]));
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new EagerBodyReadingInterceptor());
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), static http3 => http3.Limits.MaxRequestBodySize = 16);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeFalse();

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        frames.Count.ShouldBe(1);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"].ShouldBe("413");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: An unread body should be stopped with H3_NO_ERROR once the complete response is sent")]
    public async Task SendAsync_OnUnreadBody_ShouldStopSendingWithNoError()
    {
        // Arrange — the handler answers without reading the 1 MiB body the client is still sending.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request(
                "POST", "/upload", "https", "a", headers: new Dictionary<string, string> { ["content-length"] = "1048576" }),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[512])));

        IHttpContext context = await peer.NextContextAsync();
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("done"));

        // Act
        await peer.ConnectionContext.SendAsync(context);

        // Assert — the complete response arrives (HEADERS, DATA, FIN), then STOP_SENDING(H3_NO_ERROR).
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        frames.Select(frame => frame.FrameType).ToArray().ShouldBe(new[] { (long)Http3FrameType.Headers, (long)Http3FrameType.Data });
        Encoding.ASCII.GetString(frames[1].Payload).ShouldBe("done");

        ConnectionResetException stop = await Should.ThrowAsync<ConnectionResetException>(() => request.Output.WriteAsync(new byte[1]).AsTask());
        stop.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.NoError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A small unread body should be stopped at once, without waiting for the rest of the upload")]
    public async Task SendAsync_OnSmallUnreadBody_ShouldStopSendingWithoutWaiting()
    {
        // Arrange — the handler ignores a small body that is still arriving; the client keeps its side open.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[512])));

        IHttpContext context = await peer.NextContextAsync();
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("done"));

        // Act — the send ends the response without holding it for the rest of the upload (#1080: it used to
        // drain up to 64 KiB for up to 5 s, because the stop could not carry H3_NO_ERROR on the QUIC driver).
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert — the complete response arrives, then STOP_SENDING(H3_NO_ERROR), RFC 9114 §4.1.
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        Encoding.ASCII.GetString(frames.Single(frame => frame.FrameType == (long)Http3FrameType.Data).Payload).ShouldBe("done");

        ConnectionResetException stop = await Should.ThrowAsync<ConnectionResetException>(() => request.Output.WriteAsync(new byte[1]).AsTask());
        stop.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.NoError);

        // The rest of the body was never delivered, so it is no longer readable as if it ended.
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[1]).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A body read left in flight past the response should fail, and not hold back STOP_SENDING(H3_NO_ERROR)")]
    public async Task SendAsync_OnBodyReadInFlight_ShouldFailTheReadAndStopSending()
    {
        // Arrange — the handler starts a body read that waits for octets the client never sends, and answers
        // without awaiting it.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));

        IHttpContext context = await peer.NextContextAsync();
        Task<int> leakedRead = context.Request.Body.ReadAsync(new byte[64]).AsTask();
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("done"));

        // Act
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert — the client is told to stop at once, the response arrives whole, and the read fails rather
        // than waiting for octets that will never come.
        ConnectionResetException stop = await Should.ThrowAsync<ConnectionResetException>(() => request.Output.WriteAsync(new byte[1]).AsTask());
        stop.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.NoError);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        Encoding.ASCII.GetString(frames.Single(frame => frame.FrameType == (long)Http3FrameType.Data).Payload).ShouldBe("done");

        await Should.ThrowAsync<IOException>(() => leakedRead.WaitAsync(_timeout));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A read cancelled inside the trailer section should still stop the stream with H3_NO_ERROR")]
    public async Task SendAsync_AfterReadCancelledInsideTrailers_ShouldStopSendingWithNoError()
    {
        // Arrange — the body, then a trailing HEADERS frame whose field section is only partly sent: the
        // two-octet QPACK prefix of a declared eight.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello")),
            HttpProtocolPayloadFactory.CreateHttp3FrameHeader(0x1, 8),
            new byte[] { 0x00, 0x00 }));

        IHttpContext context = await peer.NextContextAsync();
        byte[] body = new byte[5];
        await context.Request.Body.ReadExactlyAsync(body).AsTask().WaitAsync(_timeout);

        // The next read waits inside the trailer section until it is cancelled: the frame header is
        // consumed but its field section is not, so the stream is left at no frame boundary.
        using (CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100)))
        {
            await Should.ThrowAsync<OperationCanceledException>(
                () => context.Request.Body.ReadAsync(new byte[1], cancellation.Token).AsTask().WaitAsync(_timeout));
        }

        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("done"));

        // Act — the send must not read on (the octets after the consumed frame header are not a frame), so
        // it neither misreads them nor waits for the rest of the upload.
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert — the complete response arrives, then STOP_SENDING(H3_NO_ERROR), RFC 9114 §4.1.
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        Encoding.ASCII.GetString(frames.Single(frame => frame.FrameType == (long)Http3FrameType.Data).Payload).ShouldBe("done");

        ConnectionResetException stop = await Should.ThrowAsync<ConnectionResetException>(() => request.Output.WriteAsync(new byte[1]).AsTask());
        stop.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.NoError);

        // The cancelled trailer read left the body unreadable.
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[1]).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A bodiless request whose FIN has arrived should not be stopped after its response")]
    public async Task SendAsync_OnBodilessRequestWhoseFinArrived_ShouldNotStopSending()
    {
        // Arrange — a GET whose FIN follows its HEADERS frame; the handler never reads its (empty) body.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/resource", "https", "a"));
        request.Output.Complete();

        IHttpContext context = await peer.NextContextAsync();
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("done"));

        // Act
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert — the complete response, and no STOP_SENDING: the request had already ended, so there is
        // nothing to refuse (RFC 9114 §4.1). A stop would fire the client's ConnectionClosed on this driver.
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        Encoding.ASCII.GetString(frames.Single(frame => frame.FrameType == (long)Http3FrameType.Data).Payload).ShouldBe("done");
        request.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A bodiless request whose FIN has arrived should not owe a QPACK Stream Cancellation")]
    public async Task SendAsync_OnBodilessRequestWhoseFinArrivedWithDynamicTable_ShouldNotEmitStreamCancellation()
    {
        // Arrange — with the dynamic table enabled, a request stream whose reading is abandoned owes a Stream
        // Cancellation on the server's decoder stream (RFC 9204 §4.4.2). A GET whose FIN has arrived (the
        // pre-filled stream ends after its HEADERS frame) is not abandoned.
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/resource", "https", "a"));
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), static http3 =>
        {
            http3.QPack.MaxTableCapacity = 4096;
            http3.QPack.MaxBlockedStreams = 16;
        });

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();

        // Act — the response goes out while the decoder stream is still open.
        await connectionContext.SendAsync(enumerator.Current).AsTask().WaitAsync(_timeout);

        // Assert — the decoder stream (the second stream the server opened) carries its type prefix only.
        byte[] decoderOutput = await connection.OpenedStreams[1].ReadOutputAsync().WaitAsync(_timeout);
        decoderOutput.ShouldBe(new byte[] { 0x03 });
    }

    // ------------------------------------------------------------ HEAD

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A HEAD response should carry its headers but no DATA frame")]
    public async Task SendAsync_OnHeadRequest_ShouldOmitDataFrame()
    {
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("HEAD", "/resource", "https", "a"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveSingleAsync(stream);

        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        await connectionContext.SendAsync(context);

        // RFC 9110 §9.3.2 — the header section a GET would carry (its Content-Length included), no content.
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        frames.Count.ShouldBe(1);
        frames[0].FrameType.ShouldBe((long)Http3FrameType.Headers);
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload);
        headers[":status"].ShouldBe("200");
        headers["content-length"].ShouldBe("5");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A HEAD response with no staged body should not synthesize a Content-Length")]
    public async Task SendAsync_OnHeadRequestWithoutBody_ShouldNotSynthesizeContentLength()
    {
        // RFC 9110 §8.6 — a HEAD response may carry Content-Length only when it equals what GET would
        // send. A handler that staged no body leaves that unknown, so none is synthesized (as on HTTP/2).
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("HEAD", "/resource", "https", "a"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveSingleAsync(stream);

        await connectionContext.SendAsync(context);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames.ShouldHaveSingleItem().Payload);
        headers[":status"].ShouldBe("200");
        headers.ShouldNotContainKey("content-length");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A HEAD response should keep the Content-Length the application set")]
    public async Task SendAsync_OnHeadRequestWithApplicationContentLength_ShouldKeepIt()
    {
        // The application knows the GET representation's length; the transport forwards it unchanged.
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("HEAD", "/resource", "https", "a"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveSingleAsync(stream);

        context.Response.Headers[HttpHeaderKey.ContentLength] = "1234";
        await connectionContext.SendAsync(context);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames.ShouldHaveSingleItem().Payload)["content-length"].ShouldBe("1234");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A streamed HEAD response should carry its headers but no DATA frame")]
    public async Task SendAsync_OnStreamedHeadResponse_ShouldOmitDataFrames()
    {
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("HEAD", "/feed", "https", "a"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveSingleAsync(
            stream,
            configureListener: static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));

        IHttpResponseStreamingFeature streaming = context.Response.Streaming;
        await streaming.WriteAsync(Encoding.ASCII.GetBytes("chunk"));
        await streaming.FlushAsync();
        await connectionContext.SendAsync(context);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        frames.Count.ShouldBe(1);
        frames[0].FrameType.ShouldBe((long)Http3FrameType.Headers);
    }

    // ------------------------------------------------------------ frames: skipped, trailers, Content-Length

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: Frames of unknown or reserved type should be skipped wherever they appear")]
    public async Task ReadBody_OnUnknownAndReservedFrames_ShouldSkipThem()
    {
        // RFC 9114 §9 — 0x21 and 0x40 are reserved (0x1f × N + 0x21) grease types; ignored before the
        // HEADERS frame and between DATA frames alike.
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x21, Encoding.ASCII.GetBytes("noise")),
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hel")),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x40, new byte[300]),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("lo")));
        (_, IHttpContext context) = await ReceiveSingleAsync(new TestConnection(payload));

        using StreamReader reader = new(context.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A trailing HEADERS frame should surface as the request's trailers")]
    public async Task ReadBody_OnTrailingHeaders_ShouldSurfaceTrailers()
    {
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello")),
            HttpProtocolPayloadFactory.CreateHttp3HeadersFrame(("x-checksum", "abc123")));
        (_, IHttpContext context) = await ReceiveSingleAsync(new TestConnection(payload));

        using StreamReader reader = new(context.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");

        context.Request.Trailers.IsSupported.ShouldBeTrue();
        context.Request.Trailers[new HttpHeaderKey("x-checksum")].Value.ShouldBe("abc123");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A trailer field prohibited in trailers should reset the stream with H3_MESSAGE_ERROR")]
    [InlineData("content-length")]
    [InlineData("content-type")]
    [InlineData("authorization")]
    [InlineData("trailer")]
    public async Task ReadBody_OnTrailerFieldProhibitedInTrailers_ShouldResetWithMessageError(string fieldName)
    {
        // Arrange — the rules HTTP/2 applies (RFC 9110 §6.5.1): a trailer section carries none of the
        // framing, routing, authentication, or content-processing fields.
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello")),
            HttpProtocolPayloadFactory.CreateHttp3HeadersFrame((fieldName, "value")));
        TestConnection stream = new(payload);
        (_, IHttpContext context) = await ReceiveSingleAsync(stream);

        // Act
        using StreamReader reader = new(context.Request.Body);
        await Should.ThrowAsync<IOException>(() => reader.ReadToEndAsync());

        // Assert
        stream.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.MessageError);
        context.Request.Trailers.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A Content-Length the DATA frames contradict should reset the stream with H3_MESSAGE_ERROR")]
    public async Task ReadBody_OnContentLengthMismatch_ShouldResetWithMessageError()
    {
        // RFC 9114 §4.1.2 — Content-Length 10, but only 5 DATA octets before the stream ends.
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request(
                "POST", "/upload", "https", "a", headers: new Dictionary<string, string> { ["content-length"] = "10" }),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello")));
        TestConnection stream = new(payload);
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveSingleAsync(stream);

        using StreamReader reader = new(context.Request.Body);
        await Should.ThrowAsync<IOException>(() => reader.ReadToEndAsync());

        stream.IsAborted.ShouldBeTrue();
        stream.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.MessageError);

        // The stream is gone, so the send path writes nothing.
        await connectionContext.SendAsync(context);
    }

    // ------------------------------------------------------------ malformed frames and requests

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A HEADERS frame truncated by the stream's end should close the connection with H3_FRAME_ERROR")]
    public async Task ReceiveAsync_OnTruncatedHeadersFrame_ShouldAbortConnectionWithFrameError()
    {
        // RFC 9114 §7.1 — the frame declares 100 octets; the stream ends cleanly after 10.
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3FrameHeader(0x1, 100),
            new byte[10]);

        (bool yielded, TestConnection _, TestMultiplexedConnection connection) = await DriveAsync(payload);

        yielded.ShouldBeFalse();
        connection.State.ShouldBe(ConnectionState.Aborted);
        connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.FrameError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A DATA frame truncated by the stream's end should fail the read and close the connection with H3_FRAME_ERROR")]
    public async Task ReadBody_OnTruncatedDataFrame_ShouldAbortConnectionWithFrameError()
    {
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3FrameHeader(0x0, 100),
            new byte[10]);
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        (_, IHttpContext context) = await ReceiveSingleAsync(connection);

        using StreamReader reader = new(context.Request.Body);
        await Should.ThrowAsync<IOException>(() => reader.ReadToEndAsync());

        connection.State.ShouldBe(ConnectionState.Aborted);
        connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.FrameError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A HEADERS frame over the limit should reset its stream with H3_FRAME_ERROR while other streams are served")]
    public async Task ReceiveAsync_OnHeadersFrameOverLimit_ShouldResetStreamWithFrameErrorAndServeOthers()
    {
        TestConnection oversized = new(HttpProtocolPayloadFactory.CreateHttp3Request(
            "GET", "/big", "https", "a", headers: new Dictionary<string, string> { ["x-big"] = new string('v', 300) }));
        TestConnection sibling = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/ok", "https", "a"));
        TestMultiplexedConnection connection = new(oversized, sibling);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), static http3 => http3.Limits.MaxRequestHeadersFrameSize = 128);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/ok");
        (await enumerator.MoveNextAsync()).ShouldBeFalse();

        oversized.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.FrameError);
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A DATA frame before HEADERS should close the connection with H3_FRAME_UNEXPECTED")]
    public async Task ReceiveAsync_OnDataBeforeHeaders_ShouldAbortConnectionWithFrameUnexpected()
    {
        // RFC 9114 §4.1 — an invalid frame sequence is a connection error.
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("early")),
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));

        (bool yielded, TestConnection _, TestMultiplexedConnection connection) = await DriveAsync(payload);

        yielded.ShouldBeFalse();
        connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.UnexpectedFrame);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A control frame on a request stream should close the connection with H3_FRAME_UNEXPECTED")]
    public async Task ReceiveAsync_OnSettingsFrameOnRequestStream_ShouldAbortConnectionWithFrameUnexpected()
    {
        // RFC 9114 §7.2.4 — SETTINGS on any stream other than the control stream.
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x4, []),
            HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a"));

        (bool yielded, TestConnection _, TestMultiplexedConnection connection) = await DriveAsync(payload);

        yielded.ShouldBeFalse();
        connection.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.UnexpectedFrame);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A stream that ends before its HEADERS frame should be reset with H3_REQUEST_INCOMPLETE")]
    public async Task ReceiveAsync_OnStreamEndingBeforeHeaders_ShouldResetWithRequestIncomplete()
    {
        (bool yielded, TestConnection stream, TestMultiplexedConnection connection) = await DriveAsync([]);

        yielded.ShouldBeFalse();
        stream.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.RequestIncomplete);
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A malformed request should reset its stream with H3_MESSAGE_ERROR while other streams are served")]
    public async Task ReceiveAsync_OnMalformedRequest_ShouldResetWithMessageErrorAndServeOthers()
    {
        // RFC 9114 §4.2 — an uppercase field name makes the request malformed (§4.1.2).
        TestConnection malformed = new(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "a"),
            ("X-Bad", "v")));
        TestConnection sibling = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/ok", "https", "a"));
        TestMultiplexedConnection connection = new(malformed, sibling);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/ok");

        malformed.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.MessageError);
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Streams: A cancelled exchange should reset its stream with H3_REQUEST_CANCELLED")]
    public async Task SendAsync_OnCancelledExchange_ShouldResetWithRequestCancelled()
    {
        // RFC 9114 §4.1.1 — the application saw the request, so abandoning it is a cancellation, never a
        // rejection (which would tell the client the request was not processed).
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveSingleAsync(stream);

        context.Cancel();
        await connectionContext.SendAsync(context);

        stream.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.RequestCancelled);
    }

    // ------------------------------------------------------------ helpers

    private static Task<(IHttpConnectionContext ConnectionContext, IHttpContext Context)> ReceiveSingleAsync(
        TestConnection stream,
        Action<Http3ConnectionListenerOptions>? configure = null,
        Action<HttpConnectionListenerOptions>? configureListener = null)
        => ReceiveSingleAsync(new TestMultiplexedConnection(stream), configure, configureListener);

    private static async Task<(IHttpConnectionContext ConnectionContext, IHttpContext Context)> ReceiveSingleAsync(
        TestMultiplexedConnection connection,
        Action<Http3ConnectionListenerOptions>? configure = null,
        Action<HttpConnectionListenerOptions>? configureListener = null)
    {
        HttpConnectionListenerOptions options = new();
        configureListener?.Invoke(options);
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), configure ?? (static _ => { }));

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return (connectionContext, enumerator.Current);
    }

    private static async Task<(bool Yielded, TestConnection Stream, TestMultiplexedConnection Connection)> DriveAsync(byte[] payload)
    {
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        bool yielded;
        await using (IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator())
        {
            yielded = await enumerator.MoveNextAsync();
        }

        return (yielded, stream, connection);
    }

    private static byte[] Combine(params byte[][] segments)
    {
        return segments.SelectMany(segment => segment).ToArray();
    }

    /// <summary>
    /// A head hook that blocks the request for <c>/slow</c> until released — a seam-contract violation
    /// the transport has to survive without stalling other streams.
    /// </summary>
    private sealed class GatedHeadInterceptor : HttpExchangeInterceptor, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(initialState: false);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public void Release() => _gate.Set();

        public void Dispose() => _gate.Dispose();

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            if (context.Path.Value == "/slow")
            {
                Entered.TrySetResult();
                _gate.Wait(TimeSpan.FromSeconds(10));
            }
        }
    }

    /// <summary>
    /// A body hook that reads the whole request body before dispatch and replays it, the way an eager
    /// Content-Digest verifier does.
    /// </summary>
    private sealed class EagerBodyReadingInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
        {
            MemoryStream copy = new();
            body.CopyTo(copy);
            copy.Position = 0;
            return copy;
        }
    }
}
