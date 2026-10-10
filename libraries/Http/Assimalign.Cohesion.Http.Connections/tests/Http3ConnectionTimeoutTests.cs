using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies the HTTP/3 connection deadlines (#1085): a connection with no request stream in flight for
/// <see cref="HttpConnectionListenerLimits.KeepAliveTimeout"/> is closed gracefully — <c>GOAWAY</c>, and
/// the receive enumeration ends so the host closes it with <c>H3_NO_ERROR</c> (RFC 9114 §5.2) — and a
/// request stream whose head does not arrive within <see cref="HttpConnectionListenerLimits.RequestHeadersTimeout"/>
/// is reset with <c>H3_REQUEST_REJECTED</c> (RFC 9114 §4.1.1) while the connection keeps serving.
/// </summary>
public class Http3ConnectionTimeoutTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _shortTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan _longTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Timeouts: A connection that never opens a request stream should be closed with GOAWAY")]
    public async Task ReceiveAsync_OnIdleConnection_ShouldGoAwayAndEndEnumeration()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 =>
        {
            http3.Limits.KeepAliveTimeout = _shortTimeout;
            http3.Limits.RequestHeadersTimeout = _longTimeout;
        });

        // Act
        bool dispatched = await peer.MoveNextAsync();

        // Assert — the enumeration ended, and the GOAWAY announces that no request stream was processed.
        dispatched.ShouldBeFalse();
        (await peer.ReadGoAwayAsync()).ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Timeouts: A connection should stay open while an exchange runs and close once it has been idle")]
    public async Task ReceiveAsync_OnExchangeOutlivingKeepAlive_ShouldCloseOnlyOnceIdle()
    {
        // Arrange — one request is dispatched, and its handler runs for three keep-alive periods.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 =>
        {
            http3.Limits.KeepAliveTimeout = _shortTimeout;
            http3.Limits.RequestHeadersTimeout = _longTimeout;
        });
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/slow", "https", "a"));
        request.Output.Complete();
        IHttpContext exchange = await peer.NextContextAsync();
        Task<bool> next = peer.MoveNextAsync();

        // Act
        await Task.Delay(_shortTimeout * 3);

        // Assert — the connection is busy, so it is not closed.
        next.IsCompleted.ShouldBeFalse();

        // Once the exchange ends the connection is idle, and is closed after the keep-alive deadline.
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("done"));
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        (await next).ShouldBeFalse();
        (await peer.ReadGoAwayAsync()).ShouldBe(4L);
        (await Http3InMemoryPeer.ReadToEndAsync(request)).ShouldNotBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Timeouts: A request stream whose head never arrives should be reset with H3_REQUEST_REJECTED")]
    public async Task ReceiveAsync_OnRequestHeadNeverArriving_ShouldResetWithRequestRejected()
    {
        // Arrange — the keep-alive deadline is far away, so only the request-headers deadline can end this.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 =>
        {
            http3.Limits.KeepAliveTimeout = _longTimeout;
            http3.Limits.RequestHeadersTimeout = _shortTimeout;
        });
        Task<IHttpContext> dispatch = peer.NextContextAsync();

        // Act — one stream sends nothing, another only the first octets of its HEADERS frame.
        Connection silent = await peer.OpenRequestStreamAsync();
        Connection trickled = await peer.OpenRequestStreamAsync();
        byte[] head = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/trickled", "https", "a");
        await trickled.Output.WriteAsync(head[..3]);

        // Assert — both are rejected unprocessed, so their clients may retry them.
        ConnectionResetException silentReset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(silent));
        silentReset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestRejected);
        ConnectionResetException trickledReset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(trickled));
        trickledReset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestRejected);

        // The connection keeps serving.
        Connection ready = await peer.OpenRequestStreamAsync();
        await ready.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/ready", "https", "a"));
        ready.Output.Complete();
        (await dispatch).Request.Path.Value.ShouldBe("/ready");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Timeouts: A malformed request stream on an idle connection should not restart the keep-alive deadline")]
    public async Task ReceiveAsync_OnMalformedRequestStreamWhileIdle_ShouldKeepTheKeepAliveDeadline()
    {
        // Arrange — the connection is idle from the start of its receive loop under a one-second keep-alive.
        TimeSpan keepAlive = TimeSpan.FromSeconds(1);
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 =>
        {
            http3.Limits.KeepAliveTimeout = keepAlive;
            http3.Limits.RequestHeadersTimeout = _longTimeout;
        });
        Task<bool> next = peer.MoveNextAsync();

        // Act — at 0.8 × the keep-alive, a request stream whose head lacks :method, which the server resets
        // as malformed (RFC 9114 §4.1.2) without dispatching it.
        await Task.Delay(keepAlive * 0.8);
        Connection malformed = await peer.OpenRequestStreamAsync();
        await malformed.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw((":scheme", "https"), (":path", "/malformed"), (":authority", "a")));
        malformed.Output.Complete();
        Stopwatch sinceMalformed = Stopwatch.StartNew();
        await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(malformed));
        bool dispatched = await next;
        TimeSpan closedAfter = sinceMalformed.Elapsed;

        // Assert — the connection closed gracefully at about 1 × the keep-alive from its start, not a whole
        // keep-alive after the malformed stream.
        dispatched.ShouldBeFalse();
        closedAfter.ShouldBeLessThan(keepAlive * 0.6);
        (await peer.ReadGoAwayAsync()).ShouldBe(4L);
    }
}
