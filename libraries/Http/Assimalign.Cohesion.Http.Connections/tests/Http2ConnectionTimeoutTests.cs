using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies the HTTP/2 connection deadlines (#1085): a connection that never sends its preface, or that
/// carries no stream for <see cref="HttpConnectionListenerLimits.KeepAliveTimeout"/>, is closed — with
/// <c>GOAWAY(NO_ERROR)</c> once the preface was exchanged (RFC 9113 §6.8, §9.1) — however chatty its peer
/// is, and a field block that does not end within <see cref="HttpConnectionListenerLimits.RequestHeadersTimeout"/>
/// ends the connection with <c>GOAWAY(ENHANCE_YOUR_CALM)</c> (RFC 9113 §6.10, §10.5). Either way the
/// receive enumeration ends, so the host releases the connection's slot.
/// </summary>
public class Http2ConnectionTimeoutTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _shortTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan _longTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Timeouts: A connection that never sends its preface should be closed without a frame")]
    public async Task ReceiveAsync_OnPrefaceNeverSent_ShouldEndWithoutWritingAFrame()
    {
        // Arrange — the peer connects and sends nothing.
        TestConnection transport = new(completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(transport), http2 =>
        {
            http2.Limits.KeepAliveTimeout = _shortTimeout;
            http2.Limits.RequestHeadersTimeout = _longTimeout;
        });

        await using HttpConnectionListener listener = new(options);
        IHttpConnection connection = await listener.AcceptOrListenAsync();
        IHttpConnectionContext context = await connection.OpenAsync();
        await using IAsyncEnumerator<IHttpContext> receive = context.ReceiveAsync().GetAsyncEnumerator();

        // Act
        bool dispatched = await receive.MoveNextAsync().AsTask().WaitAsync(_timeout);
        await connection.DisposeAsync().AsTask().WaitAsync(_timeout);

        // Assert — the enumeration ended, and the server wrote nothing: its SETTINGS must be its first
        // frame (RFC 9113 §3.4), and no HTTP/2 connection was ever established.
        dispatched.ShouldBeFalse();
        (await transport.ReadOutputAsync().WaitAsync(_timeout)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Timeouts: An idle connection should be closed with GOAWAY(NO_ERROR) however often its peer pings")]
    public async Task ReceiveAsync_OnIdleConnectionWithPings_ShouldGoAwayWithNoError()
    {
        // Arrange — the preface and SETTINGS are exchanged, and no stream is ever opened.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 =>
        {
            http2.Limits.KeepAliveTimeout = _shortTimeout;
            http2.Limits.RequestHeadersTimeout = _longTimeout;
        });
        Task receiveEnded = peer.WaitForReceiveEndAsync();

        // Act — the peer keeps the connection chatty: a PING every 100 ms, well inside the flood limit.
        Stopwatch elapsed = Stopwatch.StartNew();
        long ping = 0;
        while (!receiveEnded.IsCompleted && elapsed.Elapsed < _timeout)
        {
            byte[] opaque = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(opaque, ++ping);
            await peer.SendAsync(Http2TestSettings.RawFrame(0x6, 0, 0, opaque));
            await Task.WhenAny(receiveEnded, Task.Delay(100));
        }

        await receiveEnded;

        // Assert — PING traffic does not move the deadline: the connection is closed gracefully, and the
        // GOAWAY announces that no stream was accepted.
        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(observed => observed.Any(frame => frame.IsGoAway), "the idle GOAWAY");
        Http2WireFrame goAway = frames.Single(frame => frame.IsGoAway);
        goAway.GetGoAwayErrorCode().ShouldBe(Http2ErrorCode.NoError);
        GetLastStreamId(goAway).ShouldBe(0);
        frames.ShouldContain(frame => frame.Type == Http2WireFrame.PingType && (frame.Flags & 0x1) != 0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Timeouts: A connection should stay open while a stream runs and close once it has been idle")]
    public async Task ReceiveAsync_OnStreamOutlivingKeepAlive_ShouldCloseOnlyOnceIdle()
    {
        // Arrange — one request is dispatched, and its handler runs for three keep-alive periods.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 =>
        {
            http2.Limits.KeepAliveTimeout = _shortTimeout;
            http2.Limits.RequestHeadersTimeout = _longTimeout;
        });
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/slow"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        Task receiveEnded = peer.WaitForReceiveEndAsync();

        // Act
        await Task.Delay(_shortTimeout * 3);

        // Assert — the connection is busy, so it is still served (the PING round-trips) and not closed.
        await peer.SyncAsync();
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
        receiveEnded.IsCompleted.ShouldBeFalse();

        // Once the response is sent the connection is idle, and is closed after the keep-alive deadline.
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("done"));
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await receiveEnded;

        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(observed => observed.Any(frame => frame.IsGoAway), "the idle GOAWAY");
        Http2WireFrame goAway = frames.Single(frame => frame.IsGoAway);
        goAway.GetGoAwayErrorCode().ShouldBe(Http2ErrorCode.NoError);
        GetLastStreamId(goAway).ShouldBe(1);
        frames.ShouldContain(frame => frame.IsData && frame.StreamId == 1 && frame.EndStream);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Timeouts: A header block that never ends should close the connection with GOAWAY(ENHANCE_YOUR_CALM)")]
    public async Task ReceiveAsync_OnHeaderBlockWithoutEndHeaders_ShouldGoAwayWithEnhanceYourCalm()
    {
        // Arrange — stream 1 is a complete request; stream 3 opens a header block it never ends.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 =>
        {
            http2.Limits.KeepAliveTimeout = _longTimeout;
            http2.Limits.RequestHeadersTimeout = _shortTimeout;
        });
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/first"));
        IHttpContext first = await peer.ReceiveContextAsync();

        // Act — HEADERS without END_HEADERS, and no CONTINUATION ever follows (RFC 9113 §6.10).
        await peer.SendAsync(HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(3, 0x1, Http2TestPeer.Get("/stalled")));
        await peer.WaitForReceiveEndAsync();

        // Assert
        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(observed => observed.Any(frame => frame.IsGoAway), "the GOAWAY");
        Http2WireFrame goAway = frames.Single(frame => frame.IsGoAway);
        goAway.GetGoAwayErrorCode().ShouldBe(Http2ErrorCode.EnhanceYourCalm);
        GetLastStreamId(goAway).ShouldBe(3);

        // The request received in full before the stall is still answered.
        first.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("answered"));
        await peer.ConnectionContext.SendAsync(first).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(observed => observed.Any(frame => frame.IsData && frame.StreamId == 1 && frame.EndStream), "the response on stream 1");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Timeouts: A HEADERS frame trickled on an idle connection should close it with GOAWAY(ENHANCE_YOUR_CALM)")]
    public async Task ReceiveAsync_OnHeadersFramePayloadNeverCompleted_ShouldGoAwayWithEnhanceYourCalm()
    {
        // Arrange — the keep-alive deadline is far away, so only the request-headers deadline can end this.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 =>
        {
            http2.Limits.KeepAliveTimeout = _longTimeout;
            http2.Limits.RequestHeadersTimeout = _shortTimeout;
        });
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(1, 0x5, Http2TestPeer.Get("/trickled"));

        // Act — the frame header and two octets of the field block arrive; the rest never does.
        await peer.SendAsync(headers[..11]);
        await peer.WaitForReceiveEndAsync();

        // Assert
        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(observed => observed.Any(frame => frame.IsGoAway), "the GOAWAY");
        frames.Single(frame => frame.IsGoAway).GetGoAwayErrorCode().ShouldBe(Http2ErrorCode.EnhanceYourCalm);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Timeouts: A malformed stream on an idle connection should not restart the keep-alive deadline")]
    public async Task ReceiveAsync_OnMalformedStreamWhileIdle_ShouldKeepTheKeepAliveDeadline()
    {
        // Arrange — the connection is idle from acceptance under a one-second keep-alive.
        TimeSpan keepAlive = TimeSpan.FromSeconds(1);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 =>
        {
            http2.Limits.KeepAliveTimeout = keepAlive;
            http2.Limits.RequestHeadersTimeout = _longTimeout;
        });
        Task receiveEnded = peer.WaitForReceiveEndAsync();

        // Act — at 0.8 × the keep-alive, a HEADERS frame without :method opens stream 1, which the server
        // resets as malformed (RFC 9113 §8.1.1) without handing it to the host.
        await Task.Delay(keepAlive * 0.8);
        await peer.SendHeadersAsync(1, endStream: true, (":scheme", "https"), (":path", "/malformed"), (":authority", "api.test"));
        Stopwatch sinceMalformed = Stopwatch.StartNew();
        await receiveEnded;
        TimeSpan closedAfter = sinceMalformed.Elapsed;

        // Assert — the stream was reset, and the connection closed at about 1 × the keep-alive from its
        // acceptance, not a whole keep-alive after the malformed stream.
        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(observed => observed.Any(frame => frame.IsGoAway), "the idle GOAWAY");
        frames.ShouldContain(frame => frame.IsRstStream && frame.StreamId == 1);
        frames.Single(frame => frame.IsGoAway).GetGoAwayErrorCode().ShouldBe(Http2ErrorCode.NoError);
        closedAfter.ShouldBeLessThan(keepAlive * 0.6);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Timeouts: A request-headers deadline that fires while a received head is processed should not close the connection")]
    public async Task ReceiveAsync_OnRequestHeadersDeadlineFiringDuringProcessing_ShouldKeepServing()
    {
        // Arrange — a request-parse interceptor takes twice the request-headers timeout, and runs inline in
        // the frame pump, between two reads.
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new SlowHeadInterceptor(_shortTimeout * 2));
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options, http2 =>
        {
            http2.Limits.KeepAliveTimeout = _longTimeout;
            http2.Limits.RequestHeadersTimeout = _shortTimeout;
        });

        // Act — the whole head arrives at once, so the deadline fires while the interceptor runs.
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/slow-hook"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        // Assert — the head arrived in time and the connection is busy with it, so the deadline no longer
        // applies: no GOAWAY, and the request is answered.
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("served"));
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(observed => observed.Any(frame => frame.IsData && frame.StreamId == 1 && frame.EndStream), "the response on stream 1");
        await peer.SyncAsync();
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    private static int GetLastStreamId(Http2WireFrame goAway)
    {
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload) & 0x7FFFFFFF);
    }

    /// <summary>A request-parse interceptor whose head hook blocks the frame pump for a set time.</summary>
    private sealed class SlowHeadInterceptor : HttpExchangeInterceptor
    {
        private readonly TimeSpan _delay;

        public SlowHeadInterceptor(TimeSpan delay)
        {
            _delay = delay;
        }

        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            Thread.Sleep(_delay);
        }
    }
}
