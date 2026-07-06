using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal.Http2;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Exercises the transport's raw response body sink through the <see cref="IHttpResponseInterceptor"/>
/// seam. Streaming is opt-in — the tests register the <c>Http.Streaming</c> feature's interceptor
/// (a test-only reference; the transport library never depends on it) and drive
/// <c>context.Response.Streaming</c>, proving each protocol frames incremental writes and lets a
/// client observe bytes before the response completes.
/// </summary>
public class HttpResponseStreamingTests
{
    private static void EnableStreaming(HttpConnectionListenerOptions options)
        => options.ResponseInterceptors.Add(HttpResponseStreaming.CreateInterceptor());

    // ------------------------------------------------------------------ HTTP/1.1

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Streaming/Http1: Should stream chunked body and let the client observe bytes before completion")]
    public async Task Http1_Streaming_ShouldChunkAndObserveBeforeComplete()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /sse HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        EnableStreaming(options);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.Headers[HttpHeaderKey.ContentType] = "text/event-stream";
        IHttpResponseStreamingFeature streaming = context.Response.Streaming;

        // Write + flush the first chunk before the response is completed.
        await streaming.WriteAsync(Encoding.UTF8.GetBytes("event-1"));
        await streaming.FlushAsync();

        string firstObservation = Encoding.ASCII.GetString(await connection.ReadOutputAsync());

        firstObservation.ShouldContain("HTTP/1.1 200 OK");
        firstObservation.ShouldContain("Transfer-Encoding: chunked", Case.Insensitive);
        firstObservation.ShouldNotContain("Content-Length");
        firstObservation.ShouldContain("7\r\nevent-1\r\n");

        // Completion (driven by the connection loop's SendAsync) writes the terminating chunk.
        await connectionContext.SendAsync(context);
        string completion = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
        completion.ShouldContain("0\r\n\r\n");

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Streaming/Http1: Should expose no streaming feature when the interceptor is not registered")]
    public async Task Http1_WithoutInterceptor_ShouldNotExposeStreaming()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        // Streaming is opt-in: with no interceptor registered the feature is absent and the buffered
        // response path is used.
        context.Response.SupportsStreaming.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Streaming/Http1: A buffered response still works when the streaming interceptor is registered but unused")]
    public async Task Http1_InterceptorRegisteredButUnused_ShouldWriteBufferedResponse()
    {
        // The streaming interceptor installs the feature and creates the raw sink, but a handler that
        // ignores it (writing to the buffered Response.Body instead) must still get a normal
        // Content-Length response — the sink was never written, so SendAsync takes the buffered path.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        EnableStreaming(options);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        // Streaming is available, but the handler uses the buffered body.
        context.Response.SupportsStreaming.ShouldBeTrue();
        context.Response.Body = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("buffered"));
        await connectionContext.SendAsync(context);

        string responseText = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
        responseText.ShouldContain("HTTP/1.1 200 OK");
        responseText.ShouldContain("Content-Length: 8");
        responseText.ShouldNotContain("Transfer-Encoding: chunked");
        responseText.ShouldContain("buffered");

        await context.DisposeAsync();
    }

    // -------------------------------------------------------------------- HTTP/2

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Streaming/Http2: Should emit incremental DATA frames without a Content-Length")]
    public async Task Http2_Streaming_ShouldEmitDataFramesBeforeComplete()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/sse", "https", "api.test");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        EnableStreaming(options);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.Headers[HttpHeaderKey.ContentType] = "text/event-stream";
        IHttpResponseStreamingFeature streaming = context.Response.Streaming;

        await streaming.WriteAsync(Encoding.UTF8.GetBytes("chunk-1"));
        await streaming.FlushAsync();

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = await ReadFramesUntilAsync(
            connection, fs => fs.Any(f => f.FrameType == 0 /* DATA */));

        (long FrameType, byte[] Payload) headerFrame = frames.First(f => f.FrameType == 1);
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(headerFrame.Payload);
        headers[":status"].ShouldBe("200");
        headers.ContainsKey("content-length").ShouldBeFalse();

        byte[] streamed = frames.Where(f => f.FrameType == 0).SelectMany(f => f.Payload).ToArray();
        Encoding.UTF8.GetString(streamed).ShouldBe("chunk-1");

        await connectionContext.SendAsync(context);
        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Streaming/Http2: Should honor the send window and resume after WINDOW_UPDATE")]
    public async Task Http2_Streaming_ShouldRespectFlowControlBackpressure()
    {
        // Peer advertises a 4-octet initial stream window, so the first DATA frame can carry at most
        // 4 octets; the writer then parks until a WINDOW_UPDATE grants more credit.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(
            frameType: 0x4, flags: 0, streamId: 0,
            payload: Http2TestSettings.SettingsPayload((Http2TestSettings.Parameter.InitialWindowSize, 4u)));
        byte[] request = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            1,
            0x4 | 0x1, // END_HEADERS + END_STREAM
            (":method", "GET"),
            (":path", "/sse"),
            (":scheme", "https"),
            (":authority", "api.test"));
        byte[] streamWindowUpdate = Http2TestSettings.RawFrame(0x8, 0, 1, WindowUpdateIncrement(1000));
        byte[] connectionWindowUpdate = Http2TestSettings.RawFrame(0x8, 0, 0, WindowUpdateIncrement(1000));

        TestConnection connection = new(Combine(preface, settings, request, streamWindowUpdate, connectionWindowUpdate));
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        EnableStreaming(options);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = enumerator.Current;
        IHttpResponseStreamingFeature streaming = context.Response.Streaming;

        // Drive the write on a background task: it commits headers, emits the window-limited first
        // DATA frame (4 octets), then parks awaiting credit.
        Task writeTask = Task.Run(async () =>
        {
            await streaming.WriteAsync(Encoding.ASCII.GetBytes("0123456789"));
            await streaming.CompleteAsync();
        });

        // Observe the pre-credit output: exactly the 4 octets the window allowed.
        IReadOnlyList<(long FrameType, byte[] Payload)> preFrames = await ReadFramesUntilAsync(
            connection, fs => fs.Any(f => f.FrameType == 0 /* DATA */));
        byte[] preData = preFrames.Where(f => f.FrameType == 0).SelectMany(f => f.Payload).ToArray();
        Encoding.ASCII.GetString(preData).ShouldBe("0123");

        // Pump the receive loop so the preloaded WINDOW_UPDATE frames are processed, which unblocks
        // the parked writer; the loop then hits end-of-stream.
        (await enumerator.MoveNextAsync()).ShouldBeFalse();
        await writeTask;

        // The remainder is delivered once credit is granted.
        IReadOnlyList<(long FrameType, byte[] Payload)> postFrames =
            HttpProtocolPayloadFactory.ParseHttp2Frames(await connection.ReadOutputAsync());
        byte[] postData = postFrames.Where(f => f.FrameType == 0).SelectMany(f => f.Payload).ToArray();
        Encoding.ASCII.GetString(postData).ShouldBe("456789");
    }

    // -------------------------------------------------------------------- HTTP/3

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Streaming/Http3: Should emit incremental DATA frames without a Content-Length")]
    public async Task Http3_Streaming_ShouldEmitDataFramesBeforeComplete()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/sse", "https", "a");
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));
        EnableStreaming(options);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.Headers[HttpHeaderKey.ContentType] = "text/event-stream";
        IHttpResponseStreamingFeature streaming = context.Response.Streaming;

        await streaming.WriteAsync(Encoding.UTF8.GetBytes("chunk-1"));
        await streaming.FlushAsync();

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = await ReadHttp3FramesUntilAsync(
            stream, fs => fs.Any(f => f.FrameType == 0 /* DATA */));

        (long FrameType, byte[] Payload) headerFrame = frames.First(f => f.FrameType == 1);
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(headerFrame.Payload);
        headers[":status"].ShouldBe("200");
        headers.ContainsKey("content-length").ShouldBeFalse();

        byte[] streamed = frames.Where(f => f.FrameType == 0).SelectMany(f => f.Payload).ToArray();
        Encoding.UTF8.GetString(streamed).ShouldBe("chunk-1");

        await connectionContext.SendAsync(context);
        await context.DisposeAsync();
    }

    // ------------------------------------------------------------------- Helpers

    private static byte[] WindowUpdateIncrement(int increment) =>
    [
        (byte)((increment >> 24) & 0x7F),
        (byte)((increment >> 16) & 0xFF),
        (byte)((increment >> 8) & 0xFF),
        (byte)(increment & 0xFF),
    ];

    private static async Task<IReadOnlyList<(long FrameType, byte[] Payload)>> ReadFramesUntilAsync(
        TestConnection connection,
        Func<IReadOnlyList<(long FrameType, byte[] Payload)>, bool> predicate)
    {
        List<byte> accumulated = new();

        for (int attempt = 0; attempt < 50; attempt++)
        {
            accumulated.AddRange(await connection.ReadOutputAsync());
            IReadOnlyList<(long FrameType, byte[] Payload)> frames =
                HttpProtocolPayloadFactory.ParseHttp2Frames(accumulated.ToArray());
            if (predicate(frames))
            {
                return frames;
            }
        }

        throw new InvalidOperationException("The expected HTTP/2 frames were not observed on the wire.");
    }

    private static async Task<IReadOnlyList<(long FrameType, byte[] Payload)>> ReadHttp3FramesUntilAsync(
        TestConnection connection,
        Func<IReadOnlyList<(long FrameType, byte[] Payload)>, bool> predicate)
    {
        List<byte> accumulated = new();

        for (int attempt = 0; attempt < 50; attempt++)
        {
            accumulated.AddRange(await connection.ReadOutputAsync());
            IReadOnlyList<(long FrameType, byte[] Payload)> frames =
                HttpProtocolPayloadFactory.ParseHttp3Frames(accumulated.ToArray());
            if (predicate(frames))
            {
                return frames;
            }
        }

        throw new InvalidOperationException("The expected HTTP/3 frames were not observed on the wire.");
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private static byte[] Combine(params byte[][] segments)
    {
        int length = segments.Sum(segment => segment.Length);
        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] segment in segments)
        {
            Array.Copy(segment, 0, result, offset, segment.Length);
            offset += segment.Length;
        }

        return result;
    }
}
