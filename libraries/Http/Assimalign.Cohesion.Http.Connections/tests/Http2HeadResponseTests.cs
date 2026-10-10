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
/// Verifies HEAD responses on HTTP/2 (issue #1048, RFC 9110 §9.3.2): the header section a GET would
/// produce, carried by a HEADERS frame with END_STREAM and never followed by DATA — on the buffered path
/// and through the streaming sink.
/// </summary>
public class Http2HeadResponseTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 HEAD: A buffered body should become the content-length of a HEADERS-only response")]
    public async Task SendAsync_OnHeadWithBufferedBody_ShouldSendHeadersOnlyWithContentLength()
    {
        // Arrange — a handler that produces the same representation it would for GET.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("HEAD", "/resource"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello world"));

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — HEADERS with END_STREAM, the GET content-length, and no DATA at all.
        Dictionary<string, string> headers = AssertHeadersOnly(peer, streamId: 1);
        headers[":status"].ShouldBe("200");
        headers["content-type"].ShouldBe("text/plain");
        headers["content-length"].ShouldBe("11");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 HEAD: An application-set content-length should be preserved")]
    public async Task SendAsync_OnHeadWithExplicitContentLength_ShouldPreserveContentLength()
    {
        // Arrange — the handler declares the GET length without producing the body.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("HEAD", "/resource"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Headers[HttpHeaderKey.ContentLength] = "1234";

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        Dictionary<string, string> headers = AssertHeadersOnly(peer, streamId: 1);
        headers["content-length"].ShouldBe("1234");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 HEAD: An empty body should not synthesize a content-length")]
    public async Task SendAsync_OnHeadWithoutBody_ShouldNotSynthesizeContentLength()
    {
        // Arrange — RFC 9110 §8.6: a HEAD response's content-length must equal what GET would send,
        // which the transport cannot know when the handler produced nothing.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("HEAD", "/resource"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        Dictionary<string, string> headers = AssertHeadersOnly(peer, streamId: 1);
        headers.ContainsKey("content-length").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 HEAD: A streamed response should end the stream on its HEADERS and discard body writes")]
    public async Task StreamingWrite_OnHead_ShouldEndStreamOnHeadersAndDiscardBody()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("HEAD", "/events"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Headers[HttpHeaderKey.ContentType] = "text/event-stream";

        // Act — the handler streams exactly as it would for GET.
        await exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("data: ignored\n\n"));
        await exchange.Response.Streaming.FlushAsync();
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — HEADERS-only, and the completed stream is reaped from the connection.
        Dictionary<string, string> headers = AssertHeadersOnly(peer, streamId: 1);
        headers["content-type"].ShouldBe("text/event-stream");
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }

    private static Dictionary<string, string> AssertHeadersOnly(Http2TestPeer peer, int streamId)
    {
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(streamId);
        frames.ShouldNotContain(frame => frame.IsData);
        frames.ShouldNotContain(frame => frame.IsRstStream);

        Http2WireFrame head = frames.Single(frame => frame.IsHeaders);
        head.EndStream.ShouldBeTrue();
        return HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload);
    }
}
