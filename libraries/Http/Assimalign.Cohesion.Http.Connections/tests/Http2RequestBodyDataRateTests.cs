using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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

        // The application's own response is discarded: the transport already answered the stream. The
        // exchange reports the 408 that went on the wire, not the 500 a host's fault boundary staged for
        // the failed read, so the host's telemetry records what the client got.
        exchange.Response.StatusCode = HttpStatusCode.InternalServerError;
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.RequestTimeout);

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

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate: A wait the connection's receive window causes should not be charged within the grace period")]
    public async Task ReadBody_OnConnectionWindowHeldByAnotherStream_ShouldNotChargeTheWait()
    {
        // Arrange — stream 1's unread body holds three quarters of the connection's 65,535-octet receive
        // window, leaving less than one 16,384-octet frame, so stream 3's peer has almost no window left to
        // send in (RFC 9113 §6.9). Unexcused, stream 3 would be answered after its 500 ms grace period;
        // excused, its waits are charged only once the window has excused 500 ms of them.
        HttpMinDataRate rate = new(bytesPerSecond: 100, gracePeriod: TimeSpan.FromMilliseconds(500));
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MinRequestBodyDataRate = rate);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/held"));
        IHttpContext held = await peer.ReceiveContextAsync();
        for (int frame = 0; frame < 3; frame++)
        {
            await peer.SendDataAsync(1, new byte[16 * 1024], endStream: false);
        }

        await peer.SendHeadersAsync(3, endStream: false, Http2TestPeer.Request("POST", "/waiting"));
        IHttpContext waiting = await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        // Act — stream 3's reader waits one and a half grace periods while the window is held.
        Task<int> read = waiting.Request.Body.ReadAsync(new byte[16]).AsTask();
        await Task.Delay(rate.GracePeriod * 1.5);

        // Assert — the wait was not charged in full: stream 3 is neither answered nor reset.
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

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate: A body trickled while an unread stream pins the receive window should be answered 408 once its exemption is spent")]
    public async Task ReadBody_OnConnectionWindowPinnedByUnreadStream_ShouldRespond408OnceExemptionIsSpent()
    {
        // Arrange — stream 1's handler never reads its body, and the peer fills the connection's receive
        // window with it, leaving less than one frame of window for as long as stream 1 lives (RFC 9113
        // §6.9). Stream 3 then trickles its body at one octet every 100 ms, a tenth of the rate.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MinRequestBodyDataRate = _rate);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/unread"));
        IHttpContext unread = await peer.ReceiveContextAsync();
        for (int frame = 0; frame < 3; frame++)
        {
            await peer.SendDataAsync(1, new byte[16 * 1024], endStream: false);
        }

        await peer.SendDataAsync(1, new byte[8 * 1024], endStream: false);
        await peer.SendHeadersAsync(3, endStream: false, Http2TestPeer.Request("POST", "/trickled"));
        IHttpContext trickled = await peer.ReceiveContextAsync();
        Task<Exception> failure = ReadUntilFailureAsync(trickled.Request.Body);

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!failure.IsCompleted && elapsed.Elapsed < _timeout)
        {
            await peer.SendDataAsync(3, [0x61], endStream: false);
            await Task.WhenAny(failure, Task.Delay(100));
        }

        // Assert — the pinned window excuses stream 3's waits for its grace period at most, then every wait
        // is charged: stream 3 is answered 408 while stream 1 still holds the window.
        (await failure.WaitAsync(_timeout)).ShouldBeOfType<IOException>();
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 3), "the reset of stream 3");
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(peer.Output.ForStream(3).Single(frame => frame.IsHeaders).Payload)[":status"].ShouldBe("408");
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsHeaders || frame.IsRstStream);

        await peer.ConnectionContext.SendAsync(trickled).AsTask().WaitAsync(_timeout);
        await peer.ConnectionContext.SendAsync(unread).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate: A wait should be charged for the part of it the receive window was not low")]
    public async Task ReadBody_OnReceiveWindowRecoveringMidWait_ShouldChargeTheRestOfTheWait()
    {
        // Arrange — the connection's receive window is low when the read begins and recovers 100 ms in. The
        // reader's 300 ms allowance is then spent 400 ms in, so the wait it began must end in a 408 by 600 ms
        // (its allowance plus the re-check-bounded exemption), not be excused whole because it began low.
        ManualTimeProvider clock = new();
        bool windowLow = true;
        long lowTicks = 0;
        long lowSince = clock.GetTimestamp();
        TaskCompletionSource<long> rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Http2RequestBodyDataRate dataRate = new(
            _rate,
            clock,
            () => windowLow,
            () => windowLow ? lowTicks + (clock.GetTimestamp() - lowSince) : lowTicks,
            _ =>
            {
                rejected.TrySetResult(clock.GetTimestamp());
                return ValueTask.CompletedTask;
            });
        Channel<Http2DataChunk> pipe = Channel.CreateUnbounded<Http2DataChunk>();
        using Http2RequestBodyStream body = new(pipe.Reader, (_, _, _) => ValueTask.CompletedTask, 1, CancellationToken.None, () => { }, dataRate);
        long start = clock.GetTimestamp();

        Task<int> read = body.ReadAsync(new byte[16]).AsTask();
        await clock.WaitForTimersAsync(1).WaitAsync(_timeout);

        // Act — the window recovers after 100 ms, and the clock runs on to 600 ms.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        lowTicks += clock.GetTimestamp() - lowSince;
        windowLow = false;
        clock.Advance(TimeSpan.FromMilliseconds(500));

        // Assert — only the 100 ms the window was low were excused: the body is rejected at 600 ms.
        await Should.ThrowAsync<IOException>(() => read.WaitAsync(_timeout));
        TimeSpan.FromTicks((await rejected.Task) - start).ShouldBe(TimeSpan.FromMilliseconds(600));
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

    private static async Task<Exception> ReadUntilFailureAsync(Stream body)
    {
        byte[] buffer = new byte[16];

        try
        {
            while (await body.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The request body ended cleanly instead of failing.");
    }
}
