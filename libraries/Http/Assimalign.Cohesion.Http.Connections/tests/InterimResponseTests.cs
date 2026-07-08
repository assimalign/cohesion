using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Exercises interim (<c>1xx</c>) responses across all three transports (#752): the automatic
/// HTTP/1.1 <c>Expect: 100-continue</c> handshake (RFC 9110 §10.1.1), application-emitted
/// <c>103 Early Hints</c> (RFC 8297) resolved through <see cref="IHttpInterimResponseFeature"/>, the
/// separate HEADERS frame / field section each multiplexed protocol emits (RFC 9113 §8.1 /
/// RFC 9114 §4.1), and the guard rejecting a <c>1xx</c> as the final response status.
/// </summary>
public class InterimResponseTests
{
    // The application-facing interim-response feature (103 Early Hints, manual 100 Continue) is an
    // opt-in feature package that plugs in via the response-interceptor seam — registered here the
    // same way the streaming tests register HttpResponseStreaming.CreateInterceptor(). The automatic
    // Expect: 100-continue handshake and the 1xx-as-final-status guard are transport behavior and do
    // NOT need this.
    private static void EnableInterimResponses(HttpConnectionListenerOptions options)
        => options.ResponseInterceptors.Add(HttpInterimResponses.CreateInterceptor());

    // ------------------------------------------------------------------ HTTP/1.1

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interim/Http1: Expect 100-continue solicits the body with 100 Continue before reading it, then completes normally")]
    public async Task Http1_Expect100Continue_ShouldEmit100BeforeBody_AndPreserveKeepAlive()
    {
        // The client sends the head with Expect: 100-continue and WITHHOLDS the body until it observes
        // 100 Continue — completeInput:false leaves the peer's writer open so the body read blocks.
        byte[] head = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n");
        TestConnection connection = new(head, completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();

        StringBuilder wire = new();

        // The transport solicits the body before it is read; the request is not yet dispatched.
        await PumpUntilAsync(connection, wire, text => text.Contains("100 Continue", StringComparison.Ordinal));
        moveNext.IsCompleted.ShouldBeFalse("the request must not dispatch until the withheld body arrives");

        // The client now releases the body having seen 100 Continue.
        await connection.WriteInputAsync(Encoding.ASCII.GetBytes("hello"));
        connection.CompleteInput();

        (await moveNext).ShouldBeTrue();
        IHttpContext context = enumerator.Current;

        using StreamReader bodyReader = new(context.Request.Body, Encoding.ASCII);
        (await bodyReader.ReadToEndAsync()).ShouldBe("hello");

        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("done"));
        await connectionContext.SendAsync(context);

        await PumpUntilAsync(connection, wire, text => text.Contains("HTTP/1.1 200", StringComparison.Ordinal));

        string observed = wire.ToString();
        observed.ShouldContain("HTTP/1.1 100 Continue");
        observed.IndexOf("100 Continue", StringComparison.Ordinal)
            .ShouldBeLessThan(observed.IndexOf("HTTP/1.1 200", StringComparison.Ordinal), "100 Continue must precede the final response");
        // Keep-alive is preserved — no Connection: close was forced.
        observed.ShouldNotContain("Connection: close", Case.Insensitive);

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interim/Http1: A request without Expect observes no interim response")]
    public async Task Http1_WithoutExpectHeader_ShouldNotEmitInterim()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\n\r\nhello");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);

        StringBuilder wire = new();
        await PumpUntilAsync(connection, wire, text => text.Contains("HTTP/1.1 200", StringComparison.Ordinal));
        wire.ToString().ShouldNotContain("100 Continue");

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interim/Http1: A handler emits 103 Early Hints before the final response")]
    public async Task Http1_EarlyHints_ShouldEmitInterimBeforeFinal()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /page HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        EnableInterimResponses(options);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.InterimResponse.ShouldNotBeNull();
        context.InterimResponse!.IsInterimResponseSupported.ShouldBeTrue();

        bool emitted = await context.SendEarlyHintsAsync(["</style.css>; rel=preload; as=style"]);
        emitted.ShouldBeTrue();

        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("<html></html>"));
        await connectionContext.SendAsync(context);

        StringBuilder wire = new();
        await PumpUntilAsync(connection, wire, text => text.Contains("HTTP/1.1 200", StringComparison.Ordinal));

        string observed = wire.ToString();
        observed.ShouldContain("HTTP/1.1 103 Early Hints");
        observed.ShouldContain("Link: </style.css>; rel=preload; as=style");
        observed.IndexOf("103 Early Hints", StringComparison.Ordinal)
            .ShouldBeLessThan(observed.IndexOf("HTTP/1.1 200", StringComparison.Ordinal));

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interim/Http1: A 1xx final status code is rejected")]
    public async Task Http1_FinalStatus1xx_ShouldThrow()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.StatusCode = HttpStatusCode.Continue;
        await Should.ThrowAsync<InvalidOperationException>(async () => await connectionContext.SendAsync(context));

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interim/Http1: An interim response after the final response has started is rejected")]
    public async Task Http1_InterimAfterFinalStarted_ShouldReportUnsupportedAndThrow()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /sse HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.ResponseInterceptors.Add(HttpResponseStreaming.CreateInterceptor());
        EnableInterimResponses(options);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        IHttpInterimResponseFeature interim = context.Features.Get<IHttpInterimResponseFeature>()!;
        interim.ShouldNotBeNull();
        interim.IsInterimResponseSupported.ShouldBeTrue();

        // Start the final (streamed) response: the head is now committed to the wire.
        await context.Response.Streaming.WriteAsync(Encoding.UTF8.GetBytes("event-1"));

        interim.IsInterimResponseSupported.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints));

        await connectionContext.SendAsync(context);
        await context.DisposeAsync();
    }

    // -------------------------------------------------------------------- HTTP/2

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interim/Http2: An interim response is a separate HEADERS frame (1xx status, no Content-Length) before the final HEADERS")]
    public async Task Http2_Interim_ShouldEmitHeadersFrameBeforeFinal()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/page", "https", "api.test");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        EnableInterimResponses(options);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        IHttpInterimResponseFeature interim = context.Features.Get<IHttpInterimResponseFeature>()!;
        interim.IsInterimResponseSupported.ShouldBeTrue();

        HttpHeaderCollection hints = new();
        hints[HttpHeaderKey.Link] = "</style.css>; rel=preload; as=style";
        await interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints, hints);

        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("body"));
        await connectionContext.SendAsync(context);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = await ReadHttp2FramesUntilAsync(
            connection, fs => fs.Count(f => f.FrameType == 1 /* HEADERS */) >= 2);

        List<(long FrameType, byte[] Payload)> headerFrames = frames.Where(f => f.FrameType == 1).ToList();

        Dictionary<string, string> interimHeaders = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(headerFrames[0].Payload);
        interimHeaders[":status"].ShouldBe("103");
        interimHeaders.ContainsKey("content-length").ShouldBeFalse();
        interimHeaders["link"].ShouldBe("</style.css>; rel=preload; as=style");

        Dictionary<string, string> finalHeaders = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(headerFrames[1].Payload);
        finalHeaders[":status"].ShouldBe("200");

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interim/Http2: A 1xx final status code is rejected")]
    public async Task Http2_FinalStatus1xx_ShouldThrow()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/page", "https", "api.test");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.StatusCode = HttpStatusCode.Continue;
        await Should.ThrowAsync<InvalidOperationException>(async () => await connectionContext.SendAsync(context));

        await context.DisposeAsync();
    }

    // -------------------------------------------------------------------- HTTP/3

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interim/Http3: An interim response is a separate QPACK HEADERS frame (1xx status, no Content-Length) before the final HEADERS")]
    public async Task Http3_Interim_ShouldEmitHeadersFrameBeforeFinal()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/page", "https", "a");
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));
        EnableInterimResponses(options);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        IHttpInterimResponseFeature interim = context.Features.Get<IHttpInterimResponseFeature>()!;
        interim.IsInterimResponseSupported.ShouldBeTrue();

        HttpHeaderCollection hints = new();
        hints[HttpHeaderKey.Link] = "</style.css>; rel=preload; as=style";
        await interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints, hints);

        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("body"));
        await connectionContext.SendAsync(context);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = await ReadHttp3FramesUntilAsync(
            stream, fs => fs.Count(f => f.FrameType == 1 /* HEADERS */) >= 2);

        List<(long FrameType, byte[] Payload)> headerFrames = frames.Where(f => f.FrameType == 1).ToList();

        Dictionary<string, string> interimHeaders = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(headerFrames[0].Payload);
        interimHeaders[":status"].ShouldBe("103");
        interimHeaders.ContainsKey("content-length").ShouldBeFalse();
        interimHeaders["link"].ShouldBe("</style.css>; rel=preload; as=style");

        Dictionary<string, string> finalHeaders = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(headerFrames[1].Payload);
        finalHeaders[":status"].ShouldBe("200");

        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interim/Http3: A 1xx final status code is rejected")]
    public async Task Http3_FinalStatus1xx_ShouldThrow()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/page", "https", "a");
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        context.Response.StatusCode = HttpStatusCode.Continue;
        await Should.ThrowAsync<InvalidOperationException>(async () => await connectionContext.SendAsync(context));

        await context.DisposeAsync();
    }

    // ------------------------------------------------------ Interim validation

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Interim: A non-1xx or 101 interim status code is rejected")]
    public async Task Interim_InvalidStatusCode_ShouldThrow()
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        EnableInterimResponses(options);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        IHttpInterimResponseFeature interim = context.Features.Get<IHttpInterimResponseFeature>()!;

        // A 2xx is not an interim response.
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await interim.SendInterimResponseAsync(HttpStatusCode.Ok));

        // 101 Switching Protocols is owned by the protocol-upgrade package, not the interim feature.
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await interim.SendInterimResponseAsync(HttpStatusCode.SwitchingProtocols));

        await context.DisposeAsync();
    }

    // ------------------------------------------------------------------- Helpers

    /// <summary>
    /// Accumulates HTTP/1.1 wire output into <paramref name="wire"/> until <paramref name="predicate"/>
    /// holds. Only reads when more output is genuinely expected (the caller triggers the write first),
    /// so it never blocks past the awaited marker.
    /// </summary>
    private static async Task PumpUntilAsync(TestConnection connection, StringBuilder wire, Func<string, bool> predicate)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (predicate(wire.ToString()))
            {
                return;
            }

            wire.Append(Encoding.ASCII.GetString(await connection.ReadOutputAsync()));
        }

        predicate(wire.ToString()).ShouldBeTrue($"the expected wire output was not observed. Wire:\n{wire}");
    }

    private static async Task<IReadOnlyList<(long FrameType, byte[] Payload)>> ReadHttp2FramesUntilAsync(
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
}
