using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
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
/// <see cref="HttpExchangeInterceptorRequestContext.CanHaveBody"/> on every version (#1378): the transport
/// tells the request-parse hooks whether content may follow the head, from the framing only it sees. On
/// HTTP/1.1 that is a <c>Transfer-Encoding</c> or a non-zero <c>Content-Length</c>; on HTTP/2 a HEADERS
/// frame without <c>END_STREAM</c>, whatever <c>content-length</c> it declares; on HTTP/3 a request stream
/// whose end had not arrived with its HEADERS frame. A CONNECT never has one. Over a stream-backed input,
/// which the QUIC driver's is, an HTTP/3 FIN that arrived with the head is not seen until a read, so such a
/// request still reports a body; the tests pin that production behavior.
/// </summary>
public class HttpRequestCanHaveBodyTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Can have body: An HTTP/1.1 request should report a body only when its framing declares one")]
    [InlineData("GET / HTTP/1.1\r\nHost: api.test\r\n\r\n", false)]
    [InlineData("POST / HTTP/1.1\r\nHost: api.test\r\nContent-Length: 0\r\n\r\n", false)]
    [InlineData("POST / HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello", true)]
    [InlineData("POST / HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", true)]
    [InlineData("CONNECT upstream.test:443 HTTP/1.1\r\nHost: upstream.test:443\r\n\r\n", false)]
    public async Task CanHaveBody_OnHttp11Request_ShouldFollowTheFraming(string request, bool expected)
    {
        // Arrange
        CanHaveBodyRecorder recorder = new();
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(recorder);
        options.UseHttp1(new TestConnectionListener(new TestConnection(HttpProtocolPayloadFactory.CreateHttp1Request(request))));

        // Act
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connection = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> exchanges = connection.ReceiveAsync().GetAsyncEnumerator();
        (await exchanges.MoveNextAsync().AsTask().WaitAsync(_timeout)).ShouldBeTrue();

        // Assert
        recorder.CanHaveBody.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Can have body: An HTTP/2 request should report a body unless its head ends the stream")]
    [InlineData("POST", null, false, true)]
    [InlineData("POST", "0", false, true)]
    [InlineData("POST", "5", false, true)]
    [InlineData("GET", null, true, false)]
    [InlineData("POST", "0", true, false)]
    public async Task CanHaveBody_OnHttp2Request_ShouldFollowEndStream(string method, string? contentLength, bool endStream, bool expected)
    {
        // Arrange — without END_STREAM, DATA or a trailer section may follow, whatever the head declares.
        CanHaveBodyRecorder recorder = new();
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(recorder);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        (string Name, string Value)[] fields = contentLength is null
            ? Http2TestPeer.Request(method, "/")
            : Http2TestPeer.Request(method, "/", ("content-length", contentLength));

        // Act
        await peer.SendHeadersAsync(1, endStream, fields);
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Assert
        recorder.CanHaveBody.ShouldBe(expected);

        exchange.Cancel();
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Can have body: An HTTP/2 CONNECT should report no body")]
    public async Task CanHaveBody_OnHttp2Connect_ShouldBeFalse()
    {
        // Arrange — RFC 9113 §8.5: the stream stays open, but it carries tunnel traffic, not content.
        CanHaveBodyRecorder recorder = new();
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(recorder);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);

        // Act
        await peer.SendHeadersAsync(1, endStream: false, (":method", "CONNECT"), (":authority", "upstream.test:443"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Assert
        recorder.CanHaveBody.ShouldBe(false);

        exchange.Cancel();
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Can have body: An HTTP/3 request whose stream ended with its HEADERS frame should report no body")]
    [InlineData("GET", null)]
    [InlineData("POST", "0")]
    public async Task CanHaveBody_OnHttp3RequestEndedWithItsHead_ShouldBeFalse(string method, string? contentLength)
    {
        // Arrange — the request stream's FIN is already in when the request is dispatched.
        CanHaveBodyRecorder recorder = new();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: options => options.Interceptors.Add(recorder));
        Connection request = await peer.OpenRequestStreamAsync();
        Dictionary<string, string>? headers = contentLength is null ? null : new() { ["content-length"] = contentLength };

        // Act — the head is written unflushed and the stream completed, so its FIN arrives with it.
        request.Output.Write(HttpProtocolPayloadFactory.CreateHttp3Request(method, "/", "https", "a", headers));
        await request.Output.CompleteAsync();
        IHttpContext context = await peer.NextContextAsync();

        // Assert
        recorder.CanHaveBody.ShouldBe(false);

        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Can have body: An HTTP/3 request whose stream had not ended with its HEADERS frame should report a body")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanHaveBody_OnHttp3RequestStillOpen_ShouldBeTrue(bool dataFollows)
    {
        // Arrange — the stream stays open, with or without a DATA frame behind the head, and declares no
        // Content-Length.
        CanHaveBodyRecorder recorder = new();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: options => options.Interceptors.Add(recorder));
        Connection request = await peer.OpenRequestStreamAsync();
        byte[] head = HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a");

        // Act
        await request.Output.WriteAsync(dataFollows
            ? Combine(head, HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello")))
            : head);
        IHttpContext context = await peer.NextContextAsync();

        // Assert
        recorder.CanHaveBody.ShouldBe(true);

        await request.Output.CompleteAsync();
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Can have body: A stream-backed HTTP/3 request stream should not see a FIN that came with its HEADERS frame until a read returns nothing")]
    public async Task IsEndBuffered_OnStreamBackedInputAfterTheHead_ShouldNotSeeTheEnd()
    {
        // Arrange — the QUIC driver reads a request stream through PipeReader.Create over the QuicStream, which
        // learns of the FIN only from a read that returns no octets. A stream that holds the head and then ends
        // is the same: its end is in hand, but nothing has read it yet.
        using MemoryStream requestStream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a"));
        PipeReader input = PipeReader.Create(requestStream);
        Http3RequestStreamReader reader = new(input);
        byte[]? head = await reader.ReadHeaderSectionAsync(maxHeadersFrameSize: 16 * 1024, CancellationToken.None);

        // Act
        bool endBuffered = reader.IsEndBuffered();
        Http3FrameHeader? next = await reader.ReadFrameHeaderAsync(CancellationToken.None);

        // Assert — the probe the transport answers CanHaveBody and the automatic 100 Continue with misses the
        // end (production behavior over the QUIC driver, pinned until the driver reports a FIN it already
        // holds); the next read finds it.
        head.ShouldNotBeNull();
        endBuffered.ShouldBeFalse();
        next.ShouldBeNull();
        reader.IsCompleted.ShouldBeTrue();

        await input.CompleteAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Can have body: An HTTP/3 request read through a stream-backed input should report a body even when its FIN came with its HEADERS frame")]
    public async Task CanHaveBody_OnHttp3RequestEndedWithItsHeadOverStreamBackedInput_ShouldBeTrue()
    {
        // Arrange — the server reads its request streams the way it reads the QUIC driver's: through a
        // PipeReader over a stream.
        CanHaveBodyRecorder recorder = new();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            configureListener: options => options.Interceptors.Add(recorder),
            decorate: static server => new StreamInputMultiplexedConnection(server));
        Connection request = await peer.OpenRequestStreamAsync();

        // Act — as in CanHaveBody_OnHttp3RequestEndedWithItsHead_ShouldBeFalse, the FIN arrives with the head.
        request.Output.Write(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a"));
        await request.Output.CompleteAsync();
        IHttpContext context = await peer.NextContextAsync();

        // Assert — production behavior over the QUIC driver, pinned: the transport cannot see the FIN without
        // a read, so even a GET reports that it can have a body, and a host gate on it takes the response
        // phase. A driver that reports a FIN it already holds turns this to false.
        recorder.CanHaveBody.ShouldBe(true);

        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Can have body: An HTTP/3 CONNECT should report no body")]
    public async Task CanHaveBody_OnHttp3Connect_ShouldBeFalse()
    {
        // Arrange — RFC 9114 §4.4: the stream stays open, but it carries tunnel traffic, not content.
        CanHaveBodyRecorder recorder = new();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: options => options.Interceptors.Add(recorder));
        Connection request = await peer.OpenRequestStreamAsync();

        // Act
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":authority", "upstream.test:443")));
        IHttpContext context = await peer.NextContextAsync();

        // Assert
        recorder.CanHaveBody.ShouldBe(false);

        context.Cancel();
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    private static byte[] Combine(params byte[][] buffers)
    {
        using MemoryStream stream = new();

        foreach (byte[] buffer in buffers)
        {
            stream.Write(buffer, 0, buffer.Length);
        }

        return stream.ToArray();
    }

    /// <summary>Records what the head hook saw for <see cref="HttpExchangeInterceptorRequestContext.CanHaveBody"/>.</summary>
    private sealed class CanHaveBodyRecorder : HttpExchangeInterceptor
    {
        public bool? CanHaveBody { get; private set; }

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            CanHaveBody = context.CanHaveBody;
        }
    }
}
