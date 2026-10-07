using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Response trailers (#1315, decision 18): on HTTP/2 and HTTP/3 the fields staged on
/// <c>Response.Trailers</c> go out as a HEADERS frame after the body (RFC 9113 §8.1, RFC 9114 §4.1) —
/// on the buffered and the streaming path — and a response without trailers goes out unchanged. A
/// response to HEAD carries none. Fields a trailer section cannot carry are refused when added.
/// HTTP/1.1 reports the collection unsupported.
/// </summary>
public class HttpResponseTrailerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------ HTTP/2

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/2 buffered response should end its stream with a trailing HEADERS frame")]
    public async Task Http2SendAsync_OnBufferedResponseWithTrailers_ShouldEndStreamWithTrailingHeaders()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/report"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        exchange.Response.Trailers.IsSupported.ShouldBeTrue();
        exchange.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";
        exchange.Response.Trailers.Add(new HttpHeaderKey("Server-Timing"), "total;dur=12");

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — HEADERS, then DATA, then the trailer section, which alone carries END_STREAM.
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        frames.Select(frame => frame.Type).ShouldBe(new[] { Http2WireFrame.HeadersType, Http2WireFrame.DataType, Http2WireFrame.HeadersType });
        frames.Select(frame => frame.EndStream).ShouldBe(new[] { false, false, true });
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frames[0].Payload)["content-length"].ShouldBe("5");
        Encoding.ASCII.GetString(frames[1].Payload).ShouldBe("hello");

        Dictionary<string, string> trailers = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frames[2].Payload);
        trailers.Count.ShouldBe(2);
        trailers["x-checksum"].ShouldBe("abc123");
        trailers["server-timing"].ShouldBe("total;dur=12");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/2 response with trailers and no body should send its HEADERS then the trailer section")]
    public async Task Http2SendAsync_OnTrailersWithoutBody_ShouldSendHeadersThenTrailers()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/status"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Trailers[new HttpHeaderKey("x-outcome")] = "done";

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — no DATA frame: the head stays open for the trailer section, which ends the stream.
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        frames.Select(frame => frame.Type).ShouldBe(new[] { Http2WireFrame.HeadersType, Http2WireFrame.HeadersType });
        frames.Select(frame => frame.EndStream).ShouldBe(new[] { false, true });
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frames[1].Payload)["x-outcome"].ShouldBe("done");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/2 response without trailers should go out unchanged")]
    public async Task Http2SendAsync_OnResponseWithoutTrailers_ShouldEndStreamOnLastDataFrame()
    {
        // Arrange — the handler reads the collection and stages, then removes, a field: nothing is left.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/report"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        exchange.Response.Trailers.Add(new HttpHeaderKey("x-checksum"), "abc123");
        exchange.Response.Trailers.Remove(new HttpHeaderKey("x-checksum"));

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — HEADERS, then DATA carrying END_STREAM, as before trailers existed.
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        frames.Select(frame => frame.Type).ShouldBe(new[] { Http2WireFrame.HeadersType, Http2WireFrame.DataType });
        frames.Select(frame => frame.EndStream).ShouldBe(new[] { false, true });
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/2 streamed response should end with the trailer section instead of an empty DATA frame")]
    public async Task Http2StreamingWrite_OnTrailers_ShouldEndStreamWithTrailingHeaders()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/events"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("chunk"));
        await exchange.Response.Streaming.FlushAsync();

        // Act — the trailers are known only once the body has been written.
        exchange.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — no empty END_STREAM DATA frame: the trailer section's HEADERS frame ends the stream.
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        frames.Select(frame => frame.Type).ShouldBe(new[] { Http2WireFrame.HeadersType, Http2WireFrame.DataType, Http2WireFrame.HeadersType });
        frames.Select(frame => frame.EndStream).ShouldBe(new[] { false, false, true });
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frames[2].Payload)["x-checksum"].ShouldBe("abc123");
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/2 streamed response without trailers should still end with an empty DATA frame")]
    public async Task Http2StreamingWrite_OnNoTrailers_ShouldEndStreamWithEmptyDataFrame()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/events"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("chunk"));

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        frames.Select(frame => frame.Type).ShouldBe(new[] { Http2WireFrame.HeadersType, Http2WireFrame.DataType, Http2WireFrame.DataType });
        frames[^1].EndStream.ShouldBeTrue();
        frames[^1].Payload.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: HTTP/2 trailers should follow a body that waited for flow-control credit")]
    public async Task Http2SendAsync_OnBodyBeyondSendWindow_ShouldSendTrailersAfterLastDataFrame()
    {
        // Arrange — the client's 16-octet stream window holds back a 40-octet body.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(initialWindowSize: 16);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/report"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        byte[] body = Http2TestPeer.CreateBody(40);
        exchange.Response.Body = new MemoryStream(body);
        exchange.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";

        // Act — the send parks after 16 octets until the stream window is credited.
        Task send = peer.ConnectionContext.SendAsync(exchange).AsTask();
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsData && frame.StreamId == 1), "the first DATA frame");
        await peer.SendWindowUpdateAsync(1, 1024);
        await send.WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — every DATA frame precedes the trailer section, and only the trailer section ends the stream.
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        peer.Output.DataPayload(1).ShouldBe(body);
        frames[^1].IsHeaders.ShouldBeTrue();
        frames[^1].EndStream.ShouldBeTrue();
        frames.Where(frame => frame.IsData).ShouldAllBe(frame => !frame.EndStream);
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frames[^1].Payload)["x-checksum"].ShouldBe("abc123");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/2 HEAD response should carry no trailer section")]
    public async Task Http2SendAsync_OnHeadWithTrailers_ShouldSendHeadersOnly()
    {
        // Arrange — a handler shared with GET stages trailers; HEAD has no content for them to follow.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("HEAD", "/report"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        exchange.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — the HEADERS-only response RFC 9110 §9.3.2 describes, as before trailers existed.
        Http2WireFrame head = peer.Output.ForStream(1).ShouldHaveSingleItem();
        head.IsHeaders.ShouldBeTrue();
        head.EndStream.ShouldBeTrue();
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload).ShouldNotContainKey("x-checksum");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/2 streamed HEAD response should carry no trailer section")]
    public async Task Http2StreamingWrite_OnHeadWithTrailers_ShouldSendHeadersOnly()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("HEAD", "/events"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("ignored"));
        exchange.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        Http2WireFrame head = peer.Output.ForStream(1).ShouldHaveSingleItem();
        head.IsHeaders.ShouldBeTrue();
        head.EndStream.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: A field a trailer section cannot carry should be refused when added")]
    [InlineData(":status")]
    [InlineData("Content-Length")]
    [InlineData("Content-Type")]
    [InlineData("Trailer")]
    [InlineData("Set-Cookie")]
    [InlineData("Connection")]
    [InlineData("Keep-Alive")]
    public async Task Http2Trailers_OnFieldThatCannotBeTrailer_ShouldThrowWhenAdded(string fieldName)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/report"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpTrailerCollection trailers = exchange.Response.Trailers;
        HttpHeaderKey key = new(fieldName);

        // Act and Assert — through Add and through the indexer, and nothing is staged.
        Should.Throw<ArgumentException>(() => trailers.Add(key, "value"));
        Should.Throw<ArgumentException>(() => trailers[key] = "value");
        trailers.Count.ShouldBe(0);

        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/2 CONNECT exchange should report response trailers unsupported")]
    public async Task Http2Trailers_OnConnectExchange_ShouldBeUnsupported()
    {
        // Arrange — RFC 9113 §8.5: once the tunnel is up, the stream carries only DATA.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, (":method", "CONNECT"), (":authority", "upstream.test:443"));
        IHttpContext tunnel = await peer.ReceiveContextAsync();

        // Act and Assert
        tunnel.Response.Trailers.IsSupported.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => tunnel.Response.Trailers.Add(new HttpHeaderKey("x-checksum"), "abc123"));

        tunnel.Cancel();
        await peer.ConnectionContext.SendAsync(tunnel).AsTask().WaitAsync(_timeout);
    }

    // ------------------------------------------------------------ HTTP/3

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/3 buffered response should send a trailing HEADERS frame before its FIN")]
    public async Task Http3SendAsync_OnBufferedResponseWithTrailers_ShouldSendTrailingHeadersFrame()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/report", "https", "a"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveHttp3Async(stream);
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        context.Response.Trailers.IsSupported.ShouldBeTrue();
        context.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";

        // Act
        await connectionContext.SendAsync(context);

        // Assert — HEADERS, DATA, then the trailer section with no pseudo-header field.
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        frames.Select(frame => frame.FrameType).ShouldBe(new[] { (long)Http3FrameType.Headers, (long)Http3FrameType.Data, (long)Http3FrameType.Headers });
        Encoding.ASCII.GetString(frames[1].Payload).ShouldBe("hello");

        Dictionary<string, string> trailers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[2].Payload);
        trailers.ShouldHaveSingleItem().Key.ShouldBe("x-checksum");
        trailers["x-checksum"].ShouldBe("abc123");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/3 streamed response should send a trailing HEADERS frame before its FIN")]
    public async Task Http3StreamingWrite_OnTrailers_ShouldSendTrailingHeadersFrame()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/events", "https", "a"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveHttp3Async(
            stream,
            static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        await context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("chunk"));
        await context.Response.Streaming.FlushAsync();

        // Act
        context.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";
        await connectionContext.SendAsync(context);

        // Assert
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        frames.Select(frame => frame.FrameType).ShouldBe(new[] { (long)Http3FrameType.Headers, (long)Http3FrameType.Data, (long)Http3FrameType.Headers });
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[2].Payload)["x-checksum"].ShouldBe("abc123");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/3 response without trailers should go out unchanged")]
    public async Task Http3SendAsync_OnResponseWithoutTrailers_ShouldSendHeadersAndDataOnly()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/report", "https", "a"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveHttp3Async(stream);
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        context.Response.Trailers.IsSupported.ShouldBeTrue();

        // Act
        await connectionContext.SendAsync(context);

        // Assert
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        frames.Select(frame => frame.FrameType).ShouldBe(new[] { (long)Http3FrameType.Headers, (long)Http3FrameType.Data });
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/3 HEAD response should carry no trailer section")]
    public async Task Http3SendAsync_OnHeadWithTrailers_ShouldSendHeadersOnly()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("HEAD", "/report", "https", "a"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveHttp3Async(stream);
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        context.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";

        // Act
        await connectionContext.SendAsync(context);

        // Assert
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        frames.ShouldHaveSingleItem().FrameType.ShouldBe((long)Http3FrameType.Headers);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/3 413 that replaces the staged response should drop its trailers")]
    public async Task Http3SendAsync_OnRejectedBodyWithStagedTrailers_ShouldAnswer413WithoutTrailers()
    {
        // Arrange — the transport rejected the body for its size; the handler staged a 500 with trailers.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a")
            .Concat(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[64]))
            .ToArray();
        TestConnection stream = new(payload);
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveHttp3Async(
            stream,
            configure: static http3 => http3.Limits.MaxRequestBodySize = 16);
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[128]).AsTask());
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
        context.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";

        // Act
        await connectionContext.SendAsync(context);

        // Assert — the bodyless 413 is the whole response.
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames.ShouldHaveSingleItem().Payload)[":status"].ShouldBe("413");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/3 response should refuse a field a trailer section cannot carry")]
    public async Task Http3Trailers_OnFieldProhibitedInTrailers_ShouldThrowWhenAdded()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/report", "https", "a"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveHttp3Async(stream);

        // Act and Assert
        Should.Throw<ArgumentException>(() => context.Response.Trailers.Add(HttpHeaderKey.ContentLength, "5"));
        context.Response.Trailers.Count.ShouldBe(0);

        await connectionContext.SendAsync(context);
    }

    // ------------------------------------------------------------ HTTP/1.1

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Trailers: An HTTP/1.1 response should report trailers unsupported")]
    public async Task Http1Trailers_OnAnyResponse_ShouldBeUnsupported()
    {
        // Arrange — decision 18: HTTP/1.1 sends no response trailers, chunked or not.
        TestConnection connection = new(HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: api.test\r\n\r\n"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> exchanges = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await exchanges.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = exchanges.Current;

        // Act and Assert — adding fails loudly instead of dropping the field on the wire.
        context.Response.Trailers.IsSupported.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => context.Response.Trailers.Add(new HttpHeaderKey("x-checksum"), "abc123"));
    }

    private static async Task<(IHttpConnectionContext ConnectionContext, IHttpContext Context)> ReceiveHttp3Async(
        TestConnection stream,
        Action<HttpConnectionListenerOptions>? configureListener = null,
        Action<Http3ConnectionListenerOptions>? configure = null)
    {
        HttpConnectionListenerOptions options = new();
        configureListener?.Invoke(options);
        options.UseHttp3(new TestMultiplexedConnectionListener(new TestMultiplexedConnection(stream)), configure ?? (static _ => { }));

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return (connectionContext, enumerator.Current);
    }
}
