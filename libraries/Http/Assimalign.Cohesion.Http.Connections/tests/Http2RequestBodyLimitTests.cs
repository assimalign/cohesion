using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies the HTTP/2 request-body cap (issue #1048, RFC 9110 §15.5.14): a declared content-length
/// over the cap is answered <c>413</c> before a body octet is read, a body that grows past the cap is
/// answered <c>413</c> when the response has not started, and every rejection ends with the stream
/// reset per RFC 9113 §8.1 — <c>NO_ERROR</c> after the complete <c>413</c>, <c>CANCEL</c> when the
/// application's response was already under way — while the connection keeps serving other streams.
/// </summary>
public class Http2RequestBodyLimitTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Body Cap: A declared content-length over the cap should be answered 413 without dispatching the request")]
    public async Task Dispatch_OnDeclaredContentLengthOverCap_ShouldRespond413AndResetWithoutDispatch()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MaxRequestBodySize = 16);

        // Act — stream 1 declares 100 octets against a 16-octet cap; stream 3 is an ordinary request.
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "100")));
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));

        // Assert — stream 1 never reaches the application; the next dispatched request is stream 3.
        IHttpContext next = await peer.ReceiveContextAsync();
        next.Request.Path.Value.ShouldBe("/next");

        await peer.SyncAsync();
        AssertRejectedWith413(peer, streamId: 1, Http2ErrorCode.NoError);

        // The connection keeps serving.
        next.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("ok"));
        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsData && frame.StreamId == 3 && frame.EndStream), "the response on stream 3");
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Body Cap: A body growing past the cap before the response starts should be answered 413 and reset with NO_ERROR")]
    public async Task Receive_OnBodyExceedingCapBeforeResponse_ShouldRespond413AndResetWithNoError()
    {
        // Arrange — no content-length, so only the running total can catch the overrun.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MaxRequestBodySize = 16);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act — 10 octets fit; the next 10 cross the cap while the peer is still sending.
        await peer.SendDataAsync(1, new byte[10], endStream: false);
        await peer.SendDataAsync(1, new byte[10], endStream: false);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");

        // Assert — a complete 413, then RST_STREAM(NO_ERROR) (RFC 9113 §8.1).
        AssertRejectedWith413(peer, streamId: 1, Http2ErrorCode.NoError);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        await ShouldFailToReadBodyAsync(exchange.Request.Body);

        // The application's own response is discarded: the transport already answered the stream.
        exchange.Response.StatusCode = HttpStatusCode.Ok;
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("too late"));
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();
        peer.Output.ForStream(1).Count(frame => frame.IsHeaders).ShouldBe(1);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsData);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Body Cap: A body crossing the cap with END_STREAM should be answered 413 and close the stream without a reset")]
    public async Task Receive_OnFinalDataExceedingCap_ShouldRespond413WithoutReset()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MaxRequestBodySize = 16);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act — the whole oversized body arrives in one final frame.
        await peer.SendDataAsync(1, new byte[64], endStream: true);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsHeaders && frame.StreamId == 1), "the 413 on stream 1");
        await peer.SyncAsync();

        // Assert — both halves are closed by the 413's END_STREAM, so no RST_STREAM follows, and the
        // handler still learns its exchange is over.
        Http2WireFrame head = peer.Output.ForStream(1).Single(frame => frame.IsHeaders);
        head.EndStream.ShouldBeTrue();
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload)[":status"].ShouldBe("413");
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsRstStream);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);

        // A handler that reacts by cancelling sends nothing more: the 413 was the stream's final
        // response, and no frame may follow it on the closed stream.
        exchange.Cancel();
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsRstStream);
        peer.Output.ForStream(1).Count(frame => frame.IsHeaders).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Body Cap: A body crossing the cap after the response started should reset the stream with CANCEL")]
    public async Task Receive_OnBodyExceedingCapAfterResponseStarted_ShouldResetWithCancel()
    {
        // Arrange — a 5-octet stream window parks the application's response after its first DATA
        // frame, so the response is under way when the request body crosses the cap.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            configure: http2 => http2.Limits.MaxRequestBodySize = 16,
            initialWindowSize: 5);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(100));

        Task send = peer.ConnectionContext.SendAsync(exchange).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 5, "the first window of response DATA");

        // Act
        await peer.SendDataAsync(1, new byte[32], endStream: false);

        // Assert — no 413 is possible any more: the stream is reset with CANCEL, which also releases
        // the writer parked on flow control.
        await send.WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");
        await peer.SyncAsync();

        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        frames.Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frames.Single(frame => frame.IsHeaders).Payload)[":status"].ShouldBe("200");
        peer.Output.DataLength(1).ShouldBe(5);
        frames.ShouldNotContain(frame => frame.EndStream);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Body Cap: A body exactly at the cap should be delivered in full")]
    public async Task Receive_OnBodyAtCap_ShouldDeliverWholeBody()
    {
        // Arrange
        byte[] body = Http2TestPeer.CreateBody(64);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MaxRequestBodySize = 64);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "64")));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act
        await peer.SendDataAsync(1, body[..32], endStream: false);
        await peer.SendDataAsync(1, body[32..], endStream: true);
        byte[] received = await ReadToEndAsync(exchange.Request.Body);
        await RespondOkAsync(peer, exchange);

        // Assert — delivered in full and answered normally: no 413 and no reset.
        received.ShouldBe(body);
        AssertAnsweredOk(peer, streamId: 1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Body Cap: An extended CONNECT tunnel should not be subject to the body cap")]
    public async Task Receive_OnExtendedConnectBeyondCap_ShouldNotApplyCap()
    {
        // Arrange — RFC 9110 §9.3.6: a CONNECT's post-head octets are tunnel traffic, not content.
        byte[] tunnelBytes = Http2TestPeer.CreateBody(64);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MaxRequestBodySize = 16);
        await peer.SendHeadersAsync(
            1,
            endStream: false,
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "api.test"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act
        await peer.SendDataAsync(1, tunnelBytes, endStream: true);
        byte[] received = await ReadToEndAsync(exchange.Request.Body);
        await RespondOkAsync(peer, exchange);

        // Assert — the tunnel bytes were delivered in full; nothing was rejected.
        received.ShouldBe(tunnelBytes);
        AssertAnsweredOk(peer, streamId: 1);
    }

    private static async Task RespondOkAsync(Http2TestPeer peer, IHttpContext exchange)
    {
        exchange.Response.StatusCode = HttpStatusCode.Ok;
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();
    }

    private static void AssertAnsweredOk(Http2TestPeer peer, int streamId)
    {
        // The stream's frames are the consumption WINDOW_UPDATEs and the 200 — never a 413 or a reset.
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(streamId);
        frames.ShouldNotContain(frame => frame.IsRstStream);
        Http2WireFrame head = frames.Single(frame => frame.IsHeaders);
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload)[":status"].ShouldBe("200");
    }

    private static void AssertRejectedWith413(Http2TestPeer peer, int streamId, Http2ErrorCode resetCode)
    {
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(streamId);

        Http2WireFrame head = frames.Single(frame => frame.IsHeaders);
        head.EndStream.ShouldBeTrue();
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload);
        headers[":status"].ShouldBe("413");
        headers["content-length"].ShouldBe("0");
        frames.ShouldNotContain(frame => frame.IsData);

        // Only the first reset is asserted: DATA the peer had already sent draws follow-up
        // RST_STREAM(STREAM_CLOSED)s on the retired stream.
        Http2WireFrame reset = frames.First(frame => frame.IsRstStream);
        reset.GetRstStreamErrorCode().ShouldBe(resetCode);
        frames.ToList().IndexOf(reset).ShouldBeGreaterThan(frames.ToList().IndexOf(head));
    }

    private static async Task ShouldFailToReadBodyAsync(Stream body)
    {
        // The read fails either with the 413 condition itself or — once the stream reset landed — as
        // the request abort; both end the handler's view of the body.
        Exception? failure = null;
        try
        {
            await ReadToEndAsync(body);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            failure = exception;
        }

        failure.ShouldNotBeNull("reading a request body rejected for its size must fail");
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream)
    {
        using MemoryStream copy = new();
        byte[] buffer = new byte[1024];

        while (true)
        {
            int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(_timeout);
            if (read == 0)
            {
                return copy.ToArray();
            }

            copy.Write(buffer, 0, read);
        }
    }
}
