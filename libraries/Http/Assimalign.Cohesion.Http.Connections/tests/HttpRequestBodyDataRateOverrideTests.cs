using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies the per-request minimum request-body data rate (#1383) on HTTP/1.1, HTTP/2 and HTTP/3: the
/// transport seeds the listener's rate into the request's parse context, a request may lower, raise or
/// disable it through <see cref="IHttpMinRequestBodyDataRateFeature"/> until the first read of its body,
/// and the first read freezes it. A request that disables the rate may idle; one that keeps the default is
/// still answered <c>408</c> when it trickles.
/// </summary>
public class HttpRequestBodyDataRateOverrideTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly HttpMinDataRate _rate = new(bytesPerSecond: 100, gracePeriod: TimeSpan.FromMilliseconds(300));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Data Rate Override: A body whose request disabled the rate should be served after idling")]
    public async Task Http1_OnRateDisabledForRequest_ShouldServeIdleBody()
    {
        // Arrange
        TestConnection connection = new(Encoding.ASCII.GetBytes("POST /stream HTTP/1.1\r\nHost: api.test\r\nContent-Length: 4\r\n\r\n"), completeInput: false);
        await using HttpConnectionListener listener = CreateHttp1Listener(connection, _rate);
        await using IAsyncEnumerator<IHttpContext> receive = (await (await listener.AcceptAsync()).OpenAsync()).ReceiveAsync().GetAsyncEnumerator();
        (await receive.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = receive.Current;
        context.MinRequestBodyDataRate!.MinDataRate.ShouldBeSameAs(_rate);

        // Act — the endpoint disables the rate, and the peer idles for three grace periods before it sends.
        context.MinRequestBodyDataRate.MinDataRate = null;
        Task<byte[]> received = ReadToEndAsync(context.Request.Body);
        await Task.Delay(_rate.GracePeriod * 3);
        received.IsCompleted.ShouldBeFalse();
        await connection.WriteInputAsync(Encoding.ASCII.GetBytes("ping"));

        // Assert
        Encoding.ASCII.GetString(await received.WaitAsync(_timeout)).ShouldBe("ping");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Data Rate Override: A body whose request kept the default rate should still be rejected when it idles")]
    public async Task Http1_OnDefaultRateKept_ShouldRejectIdleBody()
    {
        // Arrange
        TestConnection connection = new(Encoding.ASCII.GetBytes("POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 4\r\n\r\n"), completeInput: false);
        await using HttpConnectionListener listener = CreateHttp1Listener(connection, _rate);
        await using IAsyncEnumerator<IHttpContext> receive = (await (await listener.AcceptAsync()).OpenAsync()).ReceiveAsync().GetAsyncEnumerator();
        (await receive.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = receive.Current;

        // Act and assert — RFC 9110 §15.5.9: the read fails once the allowance is spent.
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[4]).AsTask().WaitAsync(_timeout));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Data Rate Override: A body whose request kept the default rate should still be answered 408 when it trickles below the rate")]
    public async Task Http1_OnDefaultRateKeptAndBodyTrickled_ShouldAnswer408()
    {
        // Arrange — the rate comes from the parse context, and the peer delivers octets, but one every
        // 50 ms: 20 B/s against 100 B/s. Each octet extends the allowance by 10 ms; each gap spends 50.
        TestConnection connection = new(Encoding.ASCII.GetBytes("POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 100\r\n\r\na"), completeInput: false);
        await using HttpConnectionListener listener = CreateHttp1Listener(connection, _rate);
        IHttpConnectionContext connectionContext = await (await listener.AcceptAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> receive = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await receive.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = receive.Current;
        context.MinRequestBodyDataRate!.MinDataRate.ShouldBeSameAs(_rate);
        using CancellationTokenSource trickle = new();
        Task sending = TrickleAsync(() => connection.WriteInputAsync(Encoding.ASCII.GetBytes("a")), trickle.Token);

        // Act
        int delivered = await ReadUntilRejectedAsync(context.Request.Body);
        await trickle.CancelAsync();
        await sending;
        await connectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert — the octets were credited, and the trickle still fell below the rate: RFC 9110 §15.5.9.
        delivered.ShouldBeGreaterThan(0);
        Encoding.ASCII.GetString(await connection.ReadOutputAsync()).ShouldStartWith("HTTP/1.1 408");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Data Rate Override: A rate set by a request on a listener without one should reject a body that idles")]
    public async Task Http1_OnRateRaisedFromDisabledListener_ShouldRejectIdleBody()
    {
        // Arrange — the listener holds bodies to no rate; this request sets one for itself.
        TestConnection connection = new(Encoding.ASCII.GetBytes("POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 4\r\n\r\n"), completeInput: false);
        await using HttpConnectionListener listener = CreateHttp1Listener(connection, rate: null);
        await using IAsyncEnumerator<IHttpContext> receive = (await (await listener.AcceptAsync()).OpenAsync()).ReceiveAsync().GetAsyncEnumerator();
        (await receive.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = receive.Current;
        context.MinRequestBodyDataRate!.MinDataRate.ShouldBeNull();

        // Act
        context.MinRequestBodyDataRate.MinDataRate = _rate;

        // Assert
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[4]).AsTask().WaitAsync(_timeout));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Data Rate Override: The rate should be frozen at the first body read")]
    public async Task Http1_OnRateChangedAfterFirstRead_ShouldThrow()
    {
        // Arrange
        TestConnection connection = new(HttpProtocolPayloadFactory.CreateHttp1Request("POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 4\r\n\r\nping"));
        await using HttpConnectionListener listener = CreateHttp1Listener(connection, _rate);
        await using IAsyncEnumerator<IHttpContext> receive = (await (await listener.AcceptAsync()).OpenAsync()).ReceiveAsync().GetAsyncEnumerator();
        (await receive.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = receive.Current;
        IHttpMinRequestBodyDataRateFeature feature = context.MinRequestBodyDataRate!;
        feature.IsReadOnly.ShouldBeFalse();

        // Act
        await context.Request.Body.ReadAsync(new byte[1]);

        // Assert
        feature.IsReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => feature.MinDataRate = null);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate Override: A body whose request disabled the rate should be served after idling")]
    public async Task Http2_OnRateDisabledForRequest_ShouldServeIdleBody()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(), http2 => http2.Limits.MinRequestBodyDataRate = _rate);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/stream"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.MinRequestBodyDataRate!.MinDataRate.ShouldBeSameAs(_rate);

        // Act — the endpoint disables the rate, and the peer idles for three grace periods before it sends.
        exchange.MinRequestBodyDataRate.MinDataRate = null;
        Task<byte[]> received = ReadToEndAsync(exchange.Request.Body);
        await Task.Delay(_rate.GracePeriod * 3);
        await peer.SyncAsync();
        received.IsCompleted.ShouldBeFalse();
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("ping"), endStream: true);

        // Assert — delivered, and the transport neither answered nor reset the stream.
        Encoding.ASCII.GetString(await received.WaitAsync(_timeout)).ShouldBe("ping");
        await peer.SyncAsync();
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsHeaders || frame.IsRstStream);
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate Override: A body whose request kept the default rate should still be answered 408 when it idles")]
    public async Task Http2_OnDefaultRateKept_ShouldAnswer408()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(), http2 => http2.Limits.MinRequestBodyDataRate = _rate);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act
        await Should.ThrowAsync<IOException>(() => exchange.Request.Body.ReadAsync(new byte[16]).AsTask().WaitAsync(_timeout));

        // Assert — RFC 9110 §15.5.9 and RFC 9113 §8.1: a 408, then RST_STREAM(NO_ERROR).
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(peer.Output.ForStream(1).Single(frame => frame.IsHeaders).Payload)[":status"].ShouldBe("408");
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate Override: A body whose request kept the default rate should still be answered 408 when it trickles below the rate")]
    public async Task Http2_OnDefaultRateKeptAndBodyTrickled_ShouldAnswer408()
    {
        // Arrange — the rate comes from the parse context, and the peer delivers one-octet DATA frames every
        // 50 ms: 20 B/s against 100 B/s.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(), http2 => http2.Limits.MinRequestBodyDataRate = _rate);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.MinRequestBodyDataRate!.MinDataRate.ShouldBeSameAs(_rate);
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("a"), endStream: false);
        using CancellationTokenSource trickle = new();
        Task sending = TrickleAsync(() => peer.SendDataAsync(1, Encoding.ASCII.GetBytes("a"), endStream: false), trickle.Token);

        // Act
        int delivered = await ReadUntilRejectedAsync(exchange.Request.Body);
        await trickle.CancelAsync();
        await sending;

        // Assert — RFC 9110 §15.5.9 and RFC 9113 §8.1: a 408, then RST_STREAM(NO_ERROR) to stop the body.
        delivered.ShouldBeGreaterThan(0);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(peer.Output.ForStream(1).Single(frame => frame.IsHeaders).Payload)[":status"].ShouldBe("408");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.NoError);
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate Override: A rate set by a request on a listener without one should answer a body that idles with 408")]
    public async Task Http2_OnRateRaisedFromDisabledListener_ShouldAnswer408()
    {
        // Arrange — the listener holds bodies to no rate, so before #1383 the connection had no rate policy
        // at all; this request sets a rate for itself.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(), http2 => http2.Limits.MinRequestBodyDataRate = null);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.MinRequestBodyDataRate!.MinDataRate.ShouldBeNull();

        // Act
        exchange.MinRequestBodyDataRate.MinDataRate = _rate;
        await Should.ThrowAsync<IOException>(() => exchange.Request.Body.ReadAsync(new byte[16]).AsTask().WaitAsync(_timeout));

        // Assert
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(peer.Output.ForStream(1).Single(frame => frame.IsHeaders).Payload)[":status"].ShouldBe("408");
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Data Rate Override: The rate should stay adjustable after dispatch and be frozen at the first body read")]
    public async Task Http2_OnRateChangedAfterFirstRead_ShouldThrow()
    {
        // Arrange — HTTP/2 freezes the body-size cap at dispatch, but the rate only at the first read.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(), http2 => http2.Limits.MinRequestBodyDataRate = _rate);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("ping"), endStream: true);
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpMinRequestBodyDataRateFeature feature = exchange.MinRequestBodyDataRate!;
        exchange.MaxRequestBodySize!.IsReadOnly.ShouldBeTrue();
        feature.IsReadOnly.ShouldBeFalse();

        // Act
        await exchange.Request.Body.ReadAsync(new byte[1]);

        // Assert
        feature.IsReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => feature.MinDataRate = null);
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate Override: A body whose request disabled the rate should be served after idling")]
    public async Task Http3_OnRateDisabledForRequest_ShouldServeIdleBody()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MinRequestBodyDataRate = _rate, AddRequestLimits);
        IDuplexConnection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/stream", "https", "a"));
        IHttpContext context = await peer.NextContextAsync();
        context.MinRequestBodyDataRate!.MinDataRate.ShouldBeSameAs(_rate);

        // Act — the endpoint disables the rate, and the peer idles for three grace periods before it sends.
        context.MinRequestBodyDataRate.MinDataRate = null;
        Task<byte[]> received = ReadToEndAsync(context.Request.Body);
        await Task.Delay(_rate.GracePeriod * 3);
        received.IsCompleted.ShouldBeFalse();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("ping")));
        request.Output.Complete();

        // Assert
        Encoding.ASCII.GetString(await received.WaitAsync(_timeout)).ShouldBe("ping");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate Override: A body whose request kept the default rate should still be rejected when it idles")]
    public async Task Http3_OnDefaultRateKept_ShouldRejectIdleBody()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MinRequestBodyDataRate = _rate, AddRequestLimits);
        IDuplexConnection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        IHttpContext context = await peer.NextContextAsync();

        // Act and assert — the request stream is stopped and the exchange will be answered 408.
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[16]).AsTask().WaitAsync(_timeout));
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"].ShouldBe("408");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate Override: A body whose request kept the default rate should still be answered 408 when it trickles below the rate")]
    public async Task Http3_OnDefaultRateKeptAndBodyTrickled_ShouldStopSendingAndAnswer408()
    {
        // Arrange — the rate comes from the parse context, and the peer delivers one-octet DATA frames every
        // 50 ms: 20 B/s against 100 B/s.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MinRequestBodyDataRate = _rate, AddRequestLimits);
        IDuplexConnection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("a")));
        IHttpContext context = await peer.NextContextAsync();
        context.MinRequestBodyDataRate!.MinDataRate.ShouldBeSameAs(_rate);
        using CancellationTokenSource trickle = new();
        Task sending = TrickleAsync(
            async () => await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("a"))),
            trickle.Token);

        // Act
        int delivered = await ReadUntilRejectedAsync(context.Request.Body);
        await trickle.CancelAsync();
        await sending;

        // Assert — the peer is asked to stop sending with H3_NO_ERROR (RFC 9114 §4.1), and the exchange is
        // answered 408.
        delivered.ShouldBeGreaterThan(0);
        ConnectionResetException stop = await Should.ThrowAsync<ConnectionResetException>(() => request.Output.WriteAsync(new byte[1]).AsTask());
        stop.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.NoError);
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"].ShouldBe("408");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate Override: The rate should be frozen at the first body read")]
    public async Task Http3_OnRateChangedAfterFirstRead_ShouldThrow()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MinRequestBodyDataRate = _rate, AddRequestLimits);
        IDuplexConnection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("ping")));
        IHttpContext context = await peer.NextContextAsync();
        IHttpMinRequestBodyDataRateFeature feature = context.MinRequestBodyDataRate!;
        feature.IsReadOnly.ShouldBeFalse();

        // Act
        await context.Request.Body.ReadAsync(new byte[1]).AsTask().WaitAsync(_timeout);

        // Assert
        feature.IsReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => feature.MinDataRate = null);
    }

    private static HttpConnectionListener CreateHttp1Listener(TestConnection connection, HttpMinDataRate? rate)
    {
        HttpConnectionListenerOptions options = CreateOptions();
        options.UseHttp1(new TestConnectionListener(connection), http1 => http1.Limits.MinRequestBodyDataRate = rate);
        return new HttpConnectionListener(options);
    }

    private static HttpConnectionListenerOptions CreateOptions()
    {
        HttpConnectionListenerOptions options = new();
        AddRequestLimits(options);
        return options;
    }

    private static void AddRequestLimits(HttpConnectionListenerOptions options)
    {
        options.Interceptors.Add(HttpRequestLimitsInterceptor.Create());
    }

    private static async Task<byte[]> ReadToEndAsync(Stream body)
    {
        using MemoryStream received = new();
        await body.CopyToAsync(received);
        return received.ToArray();
    }

    /// <summary>
    /// Reads the body until the transport rejects it, asserting the rejection is an <see cref="IOException"/>
    /// within the test timeout, and returns the octets delivered before it.
    /// </summary>
    private static async Task<int> ReadUntilRejectedAsync(Stream body)
    {
        int delivered = 0;
        byte[] buffer = new byte[16];

        async Task ReadAsync()
        {
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                delivered += read;
            }
        }

        await Should.ThrowAsync<IOException>(() => ReadAsync().WaitAsync(_timeout));
        return delivered;
    }

    /// <summary>
    /// Sends one more piece of the body every 50 ms until cancelled: a peer that keeps sending, but below
    /// the rate. The peer stops once the server refuses the rest of the body.
    /// </summary>
    private static async Task TrickleAsync(Func<Task> send, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                await send();
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ConnectionException or InvalidOperationException)
        {
            // Cancelled by the test, or the server refused the rest of the body.
        }
    }
}
