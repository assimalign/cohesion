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
/// whose end had not arrived with its HEADERS frame, or whose positive content length still requires
/// validation. A CONNECT never has one. A generic transport facet
/// exposes a FIN already received by a stream-backed input, even before its pipe reports completion.
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

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Can have body: A stream-backed input should expose the FIN already known to its transport")]
    public async Task IsEndBuffered_OnStreamBackedInputAfterTheHead_ShouldSeeTheEnd()
    {
        // Arrange — the transport has delivered all octets and knows the FIN, while PipeReader.Create
        // does not report completion until its next read returns zero.
        using MemoryStream requestStream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a"));
        PipeReader input = PipeReader.Create(requestStream);
        await using TestConnection completion = new() { InputCompleted = true };
        Http3RequestStreamReader reader = new(input, completion);
        byte[]? head = await reader.ReadHeaderSectionAsync(maxHeadersFrameSize: 16 * 1024, CancellationToken.None);

        // Act
        bool endBuffered = reader.IsEndBuffered();
        Http3FrameHeader? next = await reader.ReadFrameHeaderAsync(CancellationToken.None);

        // Assert — the non-reading probe and the subsequent read agree, without consuming any octets.
        head.ShouldNotBeNull();
        endBuffered.ShouldBeTrue();
        next.ShouldBeNull();
        reader.IsCompleted.ShouldBeTrue();

        await input.CompleteAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Can have body: A stream-backed HTTP/3 head and FIN should retain the bodiless fast path")]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task CanHaveBody_OnHttp3RequestEndedWithItsHeadOverStreamBackedInput_ShouldBeFalse(string method)
    {
        // Arrange — the stream-backed fixture publishes the same completion fact as the QUIC driver.
        CanHaveBodyRecorder recorder = new();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            configureListener: options => options.Interceptors.Add(recorder),
            decorate: static server => new StreamInputMultiplexedConnection(server));
        Connection request = await peer.OpenRequestStreamAsync();

        // Act — the FIN arrives with the head, including for a POST without content.
        request.Output.Write(HttpProtocolPayloadFactory.CreateHttp3Request(method, "/", "https", "a"));
        await request.Output.CompleteAsync();
        IHttpContext context = await peer.NextContextAsync();

        // Assert
        recorder.CanHaveBody.ShouldBe(false);

        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Can have body: A received FIN must not hide DATA still buffered after the head")]
    public async Task IsEndBuffered_WithCompletedTransportAndBufferedData_ShouldKeepDataReadable()
    {
        // Arrange — the transport delivered both frames and FIN in one read, but the pipe still holds DATA.
        byte[] payload = Encoding.ASCII.GetBytes("hello");
        using MemoryStream requestStream = new(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/", "https", "a", body: payload));
        PipeReader input = PipeReader.Create(requestStream);
        await using TestConnection completion = new() { InputCompleted = true };
        Http3RequestStreamReader reader = new(input, completion);
        await reader.ReadHeaderSectionAsync(maxHeadersFrameSize: 16 * 1024, CancellationToken.None);

        // Act
        bool endBuffered = reader.IsEndBuffered();
        Http3FrameHeader? frame = await reader.ReadFrameHeaderAsync(CancellationToken.None);
        byte[] received = new byte[payload.Length];
        int count = await reader.ReadDataAsync(received, CancellationToken.None);

        // Assert — the probe neither consumes nor mistakes the buffered body for an empty request.
        endBuffered.ShouldBeFalse();
        frame.ShouldNotBeNull();
        frame.Value.Type.ShouldBe(0L);
        frame.Value.Length.ShouldBe(payload.Length);
        count.ShouldBe(payload.Length);
        received.ShouldBe(payload);
        reader.IsEndBuffered().ShouldBeTrue();

        await input.CompleteAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Can have body: Completion after an empty pipe probe must not hide newly buffered DATA")]
    public async Task IsEndBuffered_WithTransportCompletingAfterEmptyProbe_ShouldKeepDataReadable()
    {
        // Arrange — publish the final DATA and FIN immediately after the probe sees an empty pipe.
        Pipe pipe = new();
        await using TestConnection completion = new();
        byte[] payload = Encoding.ASCII.GetBytes("hello");
        CompletingInputReader input = new(pipe.Reader, () =>
        {
            pipe.Writer.Write(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, payload));
            pipe.Writer.Complete();
            completion.InputCompleted = true;
        });
        Http3RequestStreamReader reader = new(input, completion);
        await pipe.Writer.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/", "https", "a"));
        await reader.ReadHeaderSectionAsync(maxHeadersFrameSize: 16 * 1024, CancellationToken.None);

        // Act — the completion answer changes during the empty probe, so its prior value must remain conservative.
        bool endBuffered = reader.IsEndBuffered();
        Http3FrameHeader? frame = await reader.ReadFrameHeaderAsync(CancellationToken.None);
        byte[] received = new byte[payload.Length];
        int count = await reader.ReadDataAsync(received, CancellationToken.None);

        // Assert — completion is visible now, but the first probe must not have hidden the DATA.
        completion.IsInputCompleted.ShouldBeTrue();
        endBuffered.ShouldBeFalse();
        frame.ShouldNotBeNull();
        frame.Value.Type.ShouldBe(0L);
        frame.Value.Length.ShouldBe(payload.Length);
        count.ShouldBe(payload.Length);
        received.ShouldBe(payload);
        reader.IsEndBuffered().ShouldBeTrue();

        await input.CompleteAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Can have body: A transport without a received FIN should remain open")]
    public async Task IsEndBuffered_WithOpenTransport_ShouldNotReportTheEnd()
    {
        // Arrange
        Pipe pipe = new();
        await using TestConnection completion = new();
        Http3RequestStreamReader reader = new(pipe.Reader, completion);

        // Act / Assert — the completion member does not imply that completion has happened.
        reader.IsEndBuffered().ShouldBeFalse();

        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Can have body: Cancelled or failed input should never be mistaken for a clean FIN")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsEndBuffered_WithCancelledOrFailedInput_ShouldNotReportTheEnd(bool fail)
    {
        // Arrange — even a previously completed transport cannot override failure of the input itself.
        Pipe pipe = new();
        await using TestConnection completion = new() { InputCompleted = true };
        Http3RequestStreamReader reader = new(pipe.Reader, completion);
        if (fail)
        {
            await pipe.Writer.CompleteAsync(new IOException("The peer reset its sending direction."));
        }
        else
        {
            pipe.Reader.CancelPendingRead();
        }

        // Act / Assert
        reader.IsEndBuffered().ShouldBeFalse();

        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
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

    /// <summary>Publishes a transport's final input once, immediately after an empty pipe probe.</summary>
    private sealed class CompletingInputReader : PipeReader
    {
        private readonly PipeReader _inner;
        private Action? _completeInput;

        public CompletingInputReader(PipeReader inner, Action completeInput)
        {
            _inner = inner;
            _completeInput = completeInput;
        }

        public override void AdvanceTo(SequencePosition consumed) => _inner.AdvanceTo(consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => _inner.AdvanceTo(consumed, examined);

        public override void CancelPendingRead() => _inner.CancelPendingRead();

        public override void Complete(Exception? exception = null) => _inner.Complete(exception);

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => _inner.ReadAsync(cancellationToken);

        public override bool TryRead(out ReadResult result)
        {
            bool available = _inner.TryRead(out result);
            if (!available)
            {
                Action? complete = _completeInput;
                _completeInput = null;
                complete?.Invoke();
            }

            return available;
        }
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
