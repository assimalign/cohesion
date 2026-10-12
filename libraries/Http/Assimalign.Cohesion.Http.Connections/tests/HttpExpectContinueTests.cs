using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
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
/// The automatic <c>Expect: 100-continue</c> handshake on HTTP/2 and HTTP/3 (#1385, RFC 9110 §10.1.1),
/// as HTTP/1.1 has it: a client that declares the expectation withholds the content until it sees
/// <c>100 Continue</c>, which the transport sends at the first read of the request body — an interim
/// HEADERS block without <c>END_STREAM</c> on HTTP/2 (RFC 9113 §8.1), an interim HEADERS frame on the
/// request stream on HTTP/3 (RFC 9114 §4.1) — and never when the application answers without reading the
/// body, or when the head left no content to solicit. On HTTP/3 the <c>100</c> and a streamed response's
/// head take the request stream's writer in turn, and a <c>100</c> the client's <c>STOP_SENDING</c> fails
/// surfaces as the exchange's cancellation.
/// </summary>
public class HttpExpectContinueTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------------ HTTP/2

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Expect 100-continue/Http2: The first body read should solicit the withheld content with 100 Continue")]
    [InlineData("100-continue")]
    [InlineData("100-Continue")]
    public async Task Http2_FirstBodyRead_ShouldSend100ContinueBeforeTheContent(string expectation)
    {
        // Arrange — the client sends the head with the expectation and withholds the content.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "5"), ("expect", expectation)));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await peer.SyncAsync();
        bool solicitedBeforeRead = peer.Output.ForStream(1).Any(frame => frame.IsHeaders);

        // Act — the first read solicits the content; the client sends it once it sees 100 Continue.
        Task<string> read = ReadToEndAsync(exchange.Request.Body);
        Http2WireFrame interim = (await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsHeaders && frame.StreamId == 1),
            "the 100 Continue on stream 1")).First(frame => frame.IsHeaders && frame.StreamId == 1);
        bool readWaitedForContent = !read.IsCompleted;
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: true);
        string body = await read;

        exchange.Response.StatusCode = HttpStatusCode.Ok;
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — nothing before the read; then exactly one 100, without END_STREAM, ahead of the 200.
        solicitedBeforeRead.ShouldBeFalse();
        readWaitedForContent.ShouldBeTrue();
        body.ShouldBe("hello");
        interim.EndStream.ShouldBeFalse();
        string[] statuses = StatusesOf(peer, streamId: 1);
        statuses.ShouldBe(["100", "200"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Expect 100-continue/Http2: An application that answers without reading the body should send no 100 Continue")]
    public async Task Http2_AnswerWithoutReadingTheBody_ShouldNotSend100Continue()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "5"), ("expect", "100-continue")));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act — the handler declines the upload from its head alone (RFC 9110 §10.1.1).
        exchange.Response.StatusCode = HttpStatusCode.Unauthorized;
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — the final response alone; the body was never invited.
        StatusesOf(peer, streamId: 1).ShouldBe(["401"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Expect 100-continue/Http2: A head that ends the stream should not be answered with 100 Continue")]
    public async Task Http2_HeadEndingTheStream_ShouldNotSend100Continue()
    {
        // Arrange — END_STREAM on the head: there is no content to solicit.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("POST", "/upload", ("expect", "100-continue")));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act
        string body = await ReadToEndAsync(exchange.Request.Body);
        exchange.Response.StatusCode = HttpStatusCode.Ok;
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        body.ShouldBeEmpty();
        StatusesOf(peer, streamId: 1).ShouldBe(["200"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Expect 100-continue/Http2: A request without the expectation should observe no interim response")]
    public async Task Http2_WithoutExpectation_ShouldNotSend100Continue()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "5")));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act
        Task<string> read = ReadToEndAsync(exchange.Request.Body);
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: true);
        await read;
        exchange.Response.StatusCode = HttpStatusCode.Ok;
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        StatusesOf(peer, streamId: 1).ShouldBe(["200"]);
    }

    // ------------------------------------------------------------------ HTTP/3

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Expect 100-continue/Http3: The first body read should solicit the withheld content with 100 Continue")]
    public async Task Http3_FirstBodyRead_ShouldSend100ContinueBeforeTheContent()
    {
        // Arrange — the client sends the head with the expectation and withholds the content.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "a", headers: new Dictionary<string, string> { ["content-length"] = "5", ["expect"] = "100-continue" }));
        IHttpContext context = await peer.NextContextAsync();
        bool solicitedBeforeRead = false;

        if (request.Input.TryRead(out ReadResult early))
        {
            solicitedBeforeRead = !early.Buffer.IsEmpty;
            request.Input.AdvanceTo(early.Buffer.Start);
        }

        // Act — the first read solicits the content; the client sends it once it sees 100 Continue.
        Task<string> read = ReadToEndAsync(context.Request.Body);
        (long FrameType, byte[] Payload) interim = await ReadFrameAsync(request);
        bool readWaitedForContent = !read.IsCompleted;
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello")));
        await request.Output.CompleteAsync();
        string body = await read;

        context.Response.StatusCode = HttpStatusCode.Ok;
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<(long FrameType, byte[] Payload)> rest =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));

        // Assert — nothing before the read; then the 100 ahead of the 200.
        solicitedBeforeRead.ShouldBeFalse();
        readWaitedForContent.ShouldBeTrue();
        body.ShouldBe("hello");
        interim.FrameType.ShouldBe((long)Http3FrameType.Headers);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(interim.Payload)[":status"].ShouldBe("100");
        rest.First().FrameType.ShouldBe((long)Http3FrameType.Headers);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(rest.First().Payload)[":status"].ShouldBe("200");
        rest.Where(frame => frame.FrameType == (long)Http3FrameType.Headers)
            .Select(frame => HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frame.Payload)[":status"])
            .ShouldNotContain("100");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Expect 100-continue/Http3: An application that answers without reading the body should send no 100 Continue")]
    public async Task Http3_AnswerWithoutReadingTheBody_ShouldNotSend100Continue()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "a", headers: new Dictionary<string, string> { ["content-length"] = "5", ["expect"] = "100-continue" }));
        IHttpContext context = await peer.NextContextAsync();

        // Act — the handler declines the upload from its head alone.
        context.Response.StatusCode = HttpStatusCode.Unauthorized;
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));

        // Assert — the final response alone.
        frames.Where(frame => frame.FrameType == (long)Http3FrameType.Headers)
            .Select(frame => HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frame.Payload)[":status"])
            .ShouldBe(["401"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Expect 100-continue/Http3: A request stream that ended with its head should not be answered with 100 Continue")]
    public async Task Http3_StreamEndedWithItsHead_ShouldNotSend100Continue()
    {
        // Arrange — the FIN arrives with the head: there is no content to solicit.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        request.Output.Write(HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "a", headers: new Dictionary<string, string> { ["expect"] = "100-continue" }));
        await request.Output.CompleteAsync();
        IHttpContext context = await peer.NextContextAsync();

        // Act
        string body = await ReadToEndAsync(context.Request.Body);
        context.Response.StatusCode = HttpStatusCode.Ok;
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));

        // Assert
        body.ShouldBeEmpty();
        frames.Where(frame => frame.FrameType == (long)Http3FrameType.Headers)
            .Select(frame => HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frame.Payload)[":status"])
            .ShouldBe(["200"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Expect 100-continue/Http3: A streamed response started while the first read writes the 100 Continue should wait for it")]
    public async Task Http3_StreamedHeadRacingTheFirstRead_ShouldFollowThe100WithoutSharingTheWriter()
    {
        // Arrange — a full-duplex handler: one task makes the first body read, which writes the 100 Continue,
        // while another starts the streamed response. The request stream takes no octets, so the 100 is
        // still being written when the head commit begins.
        StallingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            configureListener: options => options.Interceptors.Add(new SinkCapturingInterceptor()),
            decorate: inner => server = new StallingMultiplexedConnection(inner));
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "a", headers: new Dictionary<string, string> { ["content-length"] = "5", ["expect"] = "100-continue" }));
        IHttpContext context = await peer.NextContextAsync();
        Stream sink = context.Features.Get<CapturedSinkFeature>().ShouldNotBeNull().Sink;
        StallingPipeWriter writer = server.ShouldNotBeNull().RequestStreams.Single().Writer;
        writer.Stall();

        // Act — the read stalls in the 100's flush; the head commit starts behind it, and the stream then takes
        // everything.
        Task<string> read = Task.Run(() => ReadToEndAsync(context.Request.Body));
        (await WaitUntilAsync(() => writer.IsFlushWaiting)).ShouldBeTrue();
        Task head = Task.Run(() => sink.FlushAsync());
        (await WaitUntilAsync(() => context.HasResponseStarted)).ShouldBeTrue();
        await Task.Delay(100);
        writer.Grant(1 << 20);
        await head.WaitAsync(_timeout);
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello")));
        await request.Output.CompleteAsync();
        string body = await read.WaitAsync(_timeout);
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));

        // Assert — one writer at a time, and the 100 whole and ahead of the final head (RFC 9114 §4.1).
        writer.ConcurrentUseObserved.ShouldBeFalse();
        body.ShouldBe("hello");
        frames.Where(frame => frame.FrameType == (long)Http3FrameType.Headers)
            .Select(frame => HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frame.Payload)[":status"])
            .ShouldBe(["100", "200"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Expect 100-continue/Http3: A first read whose 100 Continue meets the client's STOP_SENDING should fail as the exchange's cancellation")]
    public async Task Http3_FirstReadAfterTheClientStoppedTheResponse_ShouldFailAsCancelled()
    {
        // Arrange — the client refuses the response direction (STOP_SENDING) before the handler reads the body,
        // so the 100 Continue's write is the first to meet it.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request(
            "POST", "/upload", "https", "a", headers: new Dictionary<string, string> { ["content-length"] = "5", ["expect"] = "100-continue" }));
        IHttpContext context = await peer.NextContextAsync();
        request!.DirectionalAbort!.AbortRead((long)Http3ErrorCode.RequestCancelled);

        // Act
        Exception? failure = null;

        try
        {
            await ReadToEndAsync(context.Request.Body).WaitAsync(_timeout);
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            failure = exception;
        }

        // Assert — the client cancelled its request (RFC 9114 §4.1.1), and the read says so as a read meeting
        // its RESET_STREAM would: never a raw driver failure.
        failure.ShouldBeAssignableTo<OperationCanceledException>();
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();

        context.Cancel();
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(_timeout);

        while (!condition())
        {
            if (timeout.IsCancellationRequested)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    private static string[] StatusesOf(Http2TestPeer peer, int streamId)
    {
        return peer.Output.ForStream(streamId)
            .Where(frame => frame.IsHeaders)
            .Select(frame => HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frame.Payload)[":status"])
            .ToArray();
    }

    private static async Task<string> ReadToEndAsync(Stream body)
    {
        using StreamReader reader = new(body, leaveOpen: true);
        return await reader.ReadToEndAsync().WaitAsync(_timeout);
    }

    /// <summary>
    /// Reads one whole frame the server wrote on <paramref name="request"/>, waiting for it, and consumes
    /// only that frame.
    /// </summary>
    private static async Task<(long FrameType, byte[] Payload)> ReadFrameAsync(Connection request)
    {
        using CancellationTokenSource timeout = new(_timeout);

        while (true)
        {
            ReadResult result = await request.Input.ReadAsync(timeout.Token);
            byte[] buffered = result.Buffer.ToArray();

            if (TryParseFrame(buffered, out long type, out byte[] payload, out int consumed))
            {
                request.Input.AdvanceTo(result.Buffer.GetPosition(consumed));
                return (type, payload);
            }

            if (result.IsCompleted)
            {
                throw new EndOfStreamException("The server ended the request stream before a whole frame arrived.");
            }

            request.Input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }

    private static bool TryParseFrame(byte[] buffer, out long type, out byte[] payload, out int consumed)
    {
        type = 0;
        payload = [];
        consumed = 0;

        int index = 0;
        if (!TryReadVarint(buffer, ref index, out type) || !TryReadVarint(buffer, ref index, out long length) || buffer.Length - index < length)
        {
            return false;
        }

        payload = buffer.AsSpan(index, (int)length).ToArray();
        consumed = index + (int)length;
        return true;
    }

    private static bool TryReadVarint(byte[] buffer, ref int index, out long value)
    {
        value = 0;

        if (index >= buffer.Length)
        {
            return false;
        }

        int length = 1 << (buffer[index] >> 6);
        if (buffer.Length - index < length)
        {
            return false;
        }

        value = buffer[index] & 0x3F;
        for (int offset = 1; offset < length; offset++)
        {
            value = (value << 8) | buffer[index + offset];
        }

        index += length;
        return true;
    }

    /// <summary>
    /// Takes every exchange into the response phase and publishes its raw response body sink as a feature,
    /// so a test can start a streamed response the way a response feature does.
    /// </summary>
    private sealed class SinkCapturingInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
        {
            context.Features.Set(new CapturedSinkFeature(context.ResponseBody));
        }
    }

    private sealed class CapturedSinkFeature : IHttpFeature
    {
        public CapturedSinkFeature(Stream sink)
        {
            Sink = sink;
        }

        public string Name => nameof(CapturedSinkFeature);

        public Stream Sink { get; }
    }
}
