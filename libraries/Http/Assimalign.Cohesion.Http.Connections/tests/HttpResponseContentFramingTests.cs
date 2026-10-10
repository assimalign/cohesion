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
/// Responses without content (#1073): a response to HEAD (RFC 9110 §9.3.2), a 204 (§15.3.5), and a 304
/// (§15.4.5) end with their header section on every protocol. A HEAD response keeps the Content-Length
/// the application set and gets one synthesized only from a body the handler staged; a 204 never carries
/// one (§8.6), not even one the application set; a 304 carries only the application's. A body or trailer
/// section staged or streamed for a 204 or 304 never reaches the wire.
/// </summary>
public class HttpResponseContentFramingTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------ HTTP/1.1

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/1.1 HEAD response with no staged body should not synthesize a Content-Length")]
    public async Task Http1SendAsync_OnHeadWithoutBody_ShouldNotSynthesizeContentLength()
    {
        // Arrange — RFC 9110 §8.6: a HEAD response's length is the one GET would send, which nothing
        // states when the handler produced no body.
        // Act
        string response = await RespondHttp1Async("HEAD", static _ => { });

        // Assert
        response.ShouldStartWith("HTTP/1.1 200");
        response.ShouldNotContain("Content-Length", Case.Insensitive);
        response.ShouldEndWith("\r\n\r\n");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/1.1 HEAD response should keep the Content-Length the application set")]
    public async Task Http1SendAsync_OnHeadWithApplicationContentLength_ShouldKeepIt()
    {
        // Act
        string response = await RespondHttp1Async("HEAD", static context => context.Response.Headers[HttpHeaderKey.ContentLength] = "1234");

        // Assert
        response.ShouldContain("Content-Length: 1234\r\n");
        response.ShouldEndWith("\r\n\r\n");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/1.1 HEAD response should state a staged body's length without sending the body")]
    public async Task Http1SendAsync_OnHeadWithStagedBody_ShouldSendItsLengthWithoutTheBody()
    {
        // Act — the handler produces what it would for GET.
        string response = await RespondHttp1Async("HEAD", static context => context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello world")));

        // Assert
        response.ShouldContain("Content-Length: 11\r\n");
        response.ShouldEndWith("\r\n\r\n");
        response.ShouldNotContain("hello world");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/1.1 204 should carry no Content-Length and no body")]
    [InlineData(null)]
    [InlineData("5")]
    public async Task Http1SendAsync_OnNoContent_ShouldSendNoContentLengthAndNoBody(string? applicationLength)
    {
        // Act — a 204 with a staged body, and with or without a length the application set.
        string response = await RespondHttp1Async("GET", context =>
        {
            context.Response.StatusCode = HttpStatusCode.NoContent;
            context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("stale"));

            if (applicationLength is not null)
            {
                context.Response.Headers[HttpHeaderKey.ContentLength] = applicationLength;
            }
        });

        // Assert — RFC 9110 §8.6: a server MUST NOT send Content-Length in a 204; RFC 9112 §6.3: it ends with its head.
        response.ShouldStartWith("HTTP/1.1 204");
        response.ShouldNotContain("Content-Length", Case.Insensitive);
        response.ShouldEndWith("\r\n\r\n");
        response.ShouldNotContain("stale");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/1.1 304 should carry only the Content-Length the application set, and no body")]
    [InlineData(null)]
    [InlineData("1234")]
    public async Task Http1SendAsync_OnNotModified_ShouldSendOnlyTheApplicationContentLength(string? applicationLength)
    {
        // Act
        string response = await RespondHttp1Async("GET", context =>
        {
            context.Response.StatusCode = HttpStatusCode.NotModified;
            context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("stale"));

            if (applicationLength is not null)
            {
                context.Response.Headers[HttpHeaderKey.ContentLength] = applicationLength;
            }
        });

        // Assert — RFC 9110 §15.4.5: the length is the selected representation's, which only the application knows.
        response.ShouldStartWith("HTTP/1.1 304");
        response.ShouldEndWith("\r\n\r\n");
        response.ShouldNotContain("stale");

        if (applicationLength is null)
        {
            response.ShouldNotContain("Content-Length", Case.Insensitive);
        }
        else
        {
            response.ShouldContain($"Content-Length: {applicationLength}\r\n");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/1.1 204 should not send a Transfer-Encoding the application set, and a 304 should")]
    [InlineData(204, false)]
    [InlineData(304, true)]
    public async Task Http1SendAsync_OnContentlessStatusWithApplicationTransferEncoding_ShouldSendItOnlyOnNotModified(int status, bool sent)
    {
        // Act
        string response = await RespondHttp1Async("GET", context =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers[HttpHeaderKey.TransferEncoding] = "chunked";
            context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("stale"));
        });

        // Assert — RFC 9112 §6.1: a server MUST NOT send Transfer-Encoding in a 204, and MAY in a 304 to name the
        // coding a 200 would have had. Either ends with its head (RFC 9112 §6.3).
        response.ShouldStartWith($"HTTP/1.1 {status}");
        response.ShouldEndWith("\r\n\r\n");
        response.ShouldNotContain("stale");

        if (sent)
        {
            response.ShouldContain("Transfer-Encoding: chunked\r\n");
        }
        else
        {
            response.ShouldNotContain("Transfer-Encoding", Case.Insensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: A streamed HTTP/1.1 204 should not send a Transfer-Encoding the application set")]
    public async Task Http1StreamingWrite_OnNoContentWithApplicationTransferEncoding_ShouldNotSendIt()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request("GET /events HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleHttp1ContextAsync(connectionContext);
        context.Response.StatusCode = HttpStatusCode.NoContent;
        context.Response.Headers[HttpHeaderKey.TransferEncoding] = "chunked";

        // Act
        await context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("ignored"));
        await context.Response.Streaming.FlushAsync();
        await connectionContext.SendAsync(context);
        string response = Encoding.ASCII.GetString(await connection.ReadOutputAsync());

        // Assert — RFC 9112 §6.1: no Transfer-Encoding on a 204, an application-set one included.
        response.ShouldStartWith("HTTP/1.1 204");
        response.ShouldNotContain("Transfer-Encoding", Case.Insensitive);
        response.IndexOf("\r\n\r\n", StringComparison.Ordinal).ShouldBe(response.Length - 4);
        context.Response.Headers[HttpHeaderKey.TransferEncoding].Value.ShouldBe("chunked");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/1.1 204 with a staged body should leave the next keep-alive response framed")]
    public async Task Http1SendAsync_OnNoContentWithBodyOnKeepAlive_ShouldFrameTheNextResponse()
    {
        // Arrange — two pipelined requests on one connection. A client ends the 204 at its blank line
        // (RFC 9112 §6.3), so any octet after it is read as the start of the next response.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "DELETE /one HTTP/1.1\r\nHost: api.test\r\n\r\n"
            + "GET /two HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> exchanges = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        // Act
        (await exchanges.MoveNextAsync()).ShouldBeTrue();
        exchanges.Current.Response.StatusCode = HttpStatusCode.NoContent;
        exchanges.Current.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nX-Injected: 1\r\n\r\n"));
        await connectionContext.SendAsync(exchanges.Current);

        (await exchanges.MoveNextAsync()).ShouldBeTrue();
        exchanges.Current.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("two"));
        await connectionContext.SendAsync(exchanges.Current);

        string output = Encoding.ASCII.GetString(await connection.ReadOutputAsync());

        // Assert — the 204's head is followed directly by the second response.
        output.ShouldNotContain("X-Injected");
        int secondResponse = output.IndexOf("HTTP/1.1 ", "HTTP/1.1 ".Length, StringComparison.Ordinal);
        secondResponse.ShouldBeGreaterThan(0);
        output[..secondResponse].ShouldEndWith("\r\n\r\n");
        output[secondResponse..].ShouldStartWith("HTTP/1.1 200");
        output.ShouldEndWith("\r\n\r\ntwo");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: A streamed HTTP/1.1 204 should send its head alone, without chunked coding")]
    public async Task Http1StreamingWrite_OnNoContent_ShouldSendHeadOnly()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request("GET /events HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleHttp1ContextAsync(connectionContext);
        context.Response.StatusCode = HttpStatusCode.NoContent;

        // Act — the handler streams as it would for a 200.
        await context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("ignored"));
        await context.Response.Streaming.FlushAsync();
        await connectionContext.SendAsync(context);
        string response = Encoding.ASCII.GetString(await connection.ReadOutputAsync());

        // Assert — RFC 9112 §6.1: no Transfer-Encoding on a 204; no chunk, and no terminating chunk.
        response.ShouldStartWith("HTTP/1.1 204");
        response.ShouldNotContain("Transfer-Encoding", Case.Insensitive);
        response.ShouldNotContain("Content-Length", Case.Insensitive);
        response.ShouldEndWith("\r\n\r\n");
        response.IndexOf("\r\n\r\n", StringComparison.Ordinal).ShouldBe(response.Length - 4);
    }

    // ------------------------------------------------------------ HTTP/2

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/2 204 should end its stream on a HEADERS frame without content-length")]
    [InlineData(null)]
    [InlineData("5")]
    public async Task Http2SendAsync_OnNoContent_ShouldSendHeadersOnlyWithoutContentLength(string? applicationLength)
    {
        // Arrange — a staged body and trailers, and with or without a length the application set.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("DELETE", "/resource"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.StatusCode = HttpStatusCode.NoContent;
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("stale"));
        exchange.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";

        if (applicationLength is not null)
        {
            exchange.Response.Headers[HttpHeaderKey.ContentLength] = applicationLength;
        }

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        Dictionary<string, string> headers = AssertHttp2HeadersOnly(peer, streamId: 1);
        headers[":status"].ShouldBe("204");
        headers.ShouldNotContainKey("content-length");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/2 304 should carry only the content-length the application set")]
    [InlineData(null)]
    [InlineData("1234")]
    public async Task Http2SendAsync_OnNotModified_ShouldSendOnlyTheApplicationContentLength(string? applicationLength)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/resource"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.StatusCode = HttpStatusCode.NotModified;
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("stale"));

        if (applicationLength is not null)
        {
            exchange.Response.Headers[HttpHeaderKey.ContentLength] = applicationLength;
        }

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        Dictionary<string, string> headers = AssertHttp2HeadersOnly(peer, streamId: 1);
        headers[":status"].ShouldBe("304");

        if (applicationLength is null)
        {
            headers.ShouldNotContainKey("content-length");
        }
        else
        {
            headers["content-length"].ShouldBe(applicationLength);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: A streamed HTTP/2 204 should end its stream on its HEADERS and discard body writes")]
    public async Task Http2StreamingWrite_OnNoContent_ShouldEndStreamOnHeaders()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/events"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.StatusCode = HttpStatusCode.NoContent;

        // Act
        await exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("ignored"));
        await exchange.Response.Streaming.FlushAsync();
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — HEADERS-only, and the completed stream is reaped from the connection.
        AssertHttp2HeadersOnly(peer, streamId: 1)[":status"].ShouldBe("204");
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }

    // ------------------------------------------------------------ HTTP/3

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/3 204 should send one HEADERS frame without content-length")]
    [InlineData(null)]
    [InlineData("5")]
    public async Task Http3SendAsync_OnNoContent_ShouldSendHeadersOnlyWithoutContentLength(string? applicationLength)
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("DELETE", "/resource", "https", "api.test"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveSingleHttp3Async(stream);
        context.Response.StatusCode = HttpStatusCode.NoContent;
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("stale"));
        context.Response.Trailers[new HttpHeaderKey("x-checksum")] = "abc123";

        if (applicationLength is not null)
        {
            context.Response.Headers[HttpHeaderKey.ContentLength] = applicationLength;
        }

        // Act
        await connectionContext.SendAsync(context);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());

        // Assert — no DATA frame and no trailer section.
        (long frameType, byte[] headerBlock) = frames.ShouldHaveSingleItem();
        frameType.ShouldBe((long)Http3FrameType.Headers);
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(headerBlock);
        headers[":status"].ShouldBe("204");
        headers.ShouldNotContainKey("content-length");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: An HTTP/3 304 should carry only the content-length the application set")]
    [InlineData(null)]
    [InlineData("1234")]
    public async Task Http3SendAsync_OnNotModified_ShouldSendOnlyTheApplicationContentLength(string? applicationLength)
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/resource", "https", "api.test"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveSingleHttp3Async(stream);
        context.Response.StatusCode = HttpStatusCode.NotModified;
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("stale"));

        if (applicationLength is not null)
        {
            context.Response.Headers[HttpHeaderKey.ContentLength] = applicationLength;
        }

        // Act
        await connectionContext.SendAsync(context);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());

        // Assert
        (long frameType, byte[] headerBlock) = frames.ShouldHaveSingleItem();
        frameType.ShouldBe((long)Http3FrameType.Headers);
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(headerBlock);
        headers[":status"].ShouldBe("304");

        if (applicationLength is null)
        {
            headers.ShouldNotContainKey("content-length");
        }
        else
        {
            headers["content-length"].ShouldBe(applicationLength);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Framing: A streamed HTTP/3 204 should send its HEADERS frame and no DATA frame")]
    public async Task Http3StreamingWrite_OnNoContent_ShouldOmitDataFrames()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/events", "https", "api.test"));
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveSingleHttp3Async(
            stream,
            static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        context.Response.StatusCode = HttpStatusCode.NoContent;

        // Act
        await context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("ignored"));
        await context.Response.Streaming.FlushAsync();
        await connectionContext.SendAsync(context);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());

        // Assert
        (long frameType, byte[] headerBlock) = frames.ShouldHaveSingleItem();
        frameType.ShouldBe((long)Http3FrameType.Headers);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(headerBlock)[":status"].ShouldBe("204");
    }

    // ------------------------------------------------------------ helpers

    private static async Task<string> RespondHttp1Async(string method, Action<IHttpContext> respond)
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request($"{method} /resource HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleHttp1ContextAsync(connectionContext);

        respond(context);
        await connectionContext.SendAsync(context);

        return Encoding.ASCII.GetString(await connection.ReadOutputAsync());
    }

    private static async Task<IHttpContext> ReadSingleHttp1ContextAsync(IHttpConnectionContext connectionContext)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private static Dictionary<string, string> AssertHttp2HeadersOnly(Http2TestPeer peer, int streamId)
    {
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(streamId);
        frames.ShouldNotContain(frame => frame.IsData);
        frames.ShouldNotContain(frame => frame.IsRstStream);

        Http2WireFrame head = frames.Single(frame => frame.IsHeaders);
        head.EndStream.ShouldBeTrue();
        return HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload);
    }

    private static async Task<(IHttpConnectionContext ConnectionContext, IHttpContext Context)> ReceiveSingleHttp3Async(
        TestConnection stream,
        Action<HttpConnectionListenerOptions>? configureListener = null)
    {
        HttpConnectionListenerOptions options = new();
        configureListener?.Invoke(options);
        options.UseHttp3(new TestMultiplexedConnectionListener(new TestMultiplexedConnection(stream)), static _ => { });

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return (connectionContext, enumerator.Current);
    }
}
