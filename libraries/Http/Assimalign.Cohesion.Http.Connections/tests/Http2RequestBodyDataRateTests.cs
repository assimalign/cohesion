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
/// Verifies the HTTP/2 minimum request-body data rate (#1085): a body the peer holds back past the
/// grace period is answered <c>408</c> and reset with <c>NO_ERROR</c> when no response has started
/// (RFC 9110 §15.5.9, RFC 9113 §8.1), or reset with <c>CANCEL</c> when one has, while the connection
/// keeps serving its other streams. A peer held back by the connection's receive window is not charged,
/// a peer that keeps the rate is never rejected, and a CONNECT tunnel may idle.
/// </summary>
public class Http2RequestBodyDataRateTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly HttpMinDataRate _rate = new(bytesPerSecond: 100, gracePeriod: TimeSpan.FromMilliseconds(300));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate: A body held back before the response starts should be answered 408 and reset with NO_ERROR")]
    public async Task ReadBody_OnBodyBelowRateBeforeResponse_ShouldRespond408AndResetWithNoError()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MinRequestBodyDataRate = _rate);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act — the application reads the body; the peer sends none of it.
        await Should.ThrowAsync<IOException>(() => exchange.Request.Body.ReadAsync(new byte[16]).AsTask().WaitAsync(_timeout));
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");

        // Assert — a complete 408, then RST_STREAM(NO_ERROR) (RFC 9113 §8.1).
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        Http2WireFrame head = frames.Single(frame => frame.IsHeaders);
        head.EndStream.ShouldBeTrue();
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload)[":status"].ShouldBe("408");
        frames.Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.NoError);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();

        // The application's own response is discarded: the transport already answered the stream.
        exchange.Response.StatusCode = HttpStatusCode.InternalServerError;
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);

        // The connection keeps serving.
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();
        next.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("ok"));
        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(observed => observed.Any(frame => frame.IsData && frame.StreamId == 3 && frame.EndStream), "the response on stream 3");
        peer.Output.ForStream(1).Count(frame => frame.IsHeaders).ShouldBe(1);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate: A body held back after the response started should reset the stream with CANCEL")]
    public async Task ReadBody_OnBodyBelowRateAfterResponseStarted_ShouldResetWithCancel()
    {
        // Arrange — a 5-octet stream window parks the application's response after its first DATA frame,
        // so the response is under way when the body read runs out of allowance.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            configure: http2 => http2.Limits.MinRequestBodyDataRate = _rate,
            initialWindowSize: 5);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(100));

        Task send = peer.ConnectionContext.SendAsync(exchange).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 5, "the first window of response DATA");

        // Act
        await Should.ThrowAsync<IOException>(() => exchange.Request.Body.ReadAsync(new byte[16]).AsTask().WaitAsync(_timeout));

        // Assert — no 408 is possible any more: the stream is reset with CANCEL, which also releases the
        // writer parked on flow control.
        await send.WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");
        await peer.SyncAsync();

        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        frames.Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frames.Single(frame => frame.IsHeaders).Payload)[":status"].ShouldBe("200");
        frames.ShouldNotContain(frame => frame.EndStream);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate: A wait the connection's receive window causes should not be charged")]
    public async Task ReadBody_OnConnectionWindowHeldByAnotherStream_ShouldNotChargeTheWait()
    {
        // Arrange — stream 1's unread body holds three quarters of the connection's 65,535-octet receive
        // window, so stream 3's peer has almost no window left to send in (RFC 9113 §6.9).
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MinRequestBodyDataRate = _rate);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/held"));
        IHttpContext held = await peer.ReceiveContextAsync();
        for (int frame = 0; frame < 3; frame++)
        {
            await peer.SendDataAsync(1, new byte[16 * 1024], endStream: false);
        }

        await peer.SendHeadersAsync(3, endStream: false, Http2TestPeer.Request("POST", "/waiting"));
        IHttpContext waiting = await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        // Act — stream 3's reader waits for twice the grace period while the window is held.
        Task<int> read = waiting.Request.Body.ReadAsync(new byte[16]).AsTask();
        await Task.Delay(_rate.GracePeriod * 2);

        // Assert — the wait was not charged: stream 3 is neither answered nor reset.
        await peer.SyncAsync();
        read.IsCompleted.ShouldBeFalse();
        peer.Output.ForStream(3).ShouldNotContain(frame => frame.IsHeaders || frame.IsRstStream);

        // Once stream 1's body is consumed the window is free, and stream 3's peer is held to the rate.
        byte[] buffer = new byte[16 * 1024];
        int consumed = 0;
        while (consumed < 3 * 16 * 1024)
        {
            consumed += await held.Request.Body.ReadAsync(buffer).AsTask().WaitAsync(_timeout);
        }

        await Should.ThrowAsync<IOException>(() => read.WaitAsync(_timeout));
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 3), "the reset of stream 3");
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(peer.Output.ForStream(3).Single(frame => frame.IsHeaders).Payload)[":status"].ShouldBe("408");

        await peer.ConnectionContext.SendAsync(waiting).AsTask().WaitAsync(_timeout);
        await peer.ConnectionContext.SendAsync(held).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate: A body that keeps the rate should be read in full")]
    public async Task ReadBody_OnBodyAboveRate_ShouldDeliverWholeBody()
    {
        // Arrange — 50 octets every 100 ms is 500 octets per second, five times the rate, for longer than
        // the grace period.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MinRequestBodyDataRate = _rate);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        byte[] body = Http2TestPeer.CreateBody(500);
        Task<byte[]> received = ReadToEndAsync(exchange.Request.Body);

        // Act
        for (int offset = 0; offset < body.Length; offset += 50)
        {
            await Task.Delay(100);
            await peer.SendDataAsync(1, body[offset..(offset + 50)], endStream: offset + 50 == body.Length);
        }

        // Assert — delivered in full; the stream was neither answered nor reset by the transport.
        (await received.WaitAsync(_timeout)).ShouldBe(body);
        await peer.SyncAsync();
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsHeaders || frame.IsRstStream);

        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate: An idle extended CONNECT tunnel should not be held to the rate")]
    public async Task ReadBody_OnIdleExtendedConnectTunnel_ShouldNotApplyRate()
    {
        // Arrange — RFC 9110 §9.3.6: a CONNECT's DATA is tunnel traffic, which may idle indefinitely.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MinRequestBodyDataRate = _rate);
        await peer.SendHeadersAsync(
            1,
            endStream: false,
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "api.test"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act — the tunnel idles for twice the grace period.
        Task<int> read = exchange.Request.Body.ReadAsync(new byte[16]).AsTask();
        await Task.Delay(_rate.GracePeriod * 2);
        await peer.SyncAsync();

        // Assert — nothing was rejected, and the tunnel still carries the peer's octets.
        read.IsCompleted.ShouldBeFalse();
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsHeaders || frame.IsRstStream);
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("ping"), endStream: false);
        (await read.WaitAsync(_timeout)).ShouldBe(4);

        exchange.Cancel();
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    private static async Task<byte[]> ReadToEndAsync(Stream body)
    {
        using MemoryStream received = new();
        await body.CopyToAsync(received);
        return received.ToArray();
    }
}
