using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Transports.Internal.Http2;
using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;
using Assimalign.Cohesion.Transports;

using Shouldly;

using Xunit;

using NetHttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Http.Transports.Tests;

public class Http2TransportTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should parse request headers and write response frames")]
    public async Task Http2_OnRequest_ShouldParseHeadersAndWriteResponseFrames()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/items?id=7", "https", "api.test");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert request
        httpContext.Version.ShouldBe(HttpVersion.Http20);
        httpContext.Request.Method.ShouldBe(HttpMethod.Get);
        httpContext.Request.Path.Value.ShouldBe("/items");
        httpContext.Request.Query["id"].Value.ShouldBe("7");
        httpContext.Request.Host.Value.ShouldBe("api.test");
        httpContext.Request.Scheme.ShouldBe(HttpScheme.Https);

        // Act response
        httpContext.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
        httpContext.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"ok\":true}"));
        await httpConnectionContext.SendAsync(httpContext);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp2Frames(await transportContext.ReadOutputAsync());

        // Assert response
        frames.Count.ShouldBeGreaterThanOrEqualTo(4);
        frames[0].FrameType.ShouldBe(4);
        frames[1].FrameType.ShouldBe(4);
        frames[2].FrameType.ShouldBe(1);
        frames[3].FrameType.ShouldBe(0);

        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frames[2].Payload);
        headers[":status"].ShouldBe("200");
        headers["content-type"].ShouldBe("application/json");
        headers["content-length"].ShouldBe("11");
        Encoding.UTF8.GetString(frames[3].Payload).ShouldBe("{\"ok\":true}");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should yield multiple streams in sequence")]
    public async Task Http2_OnMultipleStreams_ShouldYieldRequestsInSequence()
    {
        // Arrange
        byte[] first = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/one", "https", "api.test");
        byte[] second = HttpProtocolPayloadFactory.CreateHttp2Request(3, "GET", "/two", "https", "api.test");
        byte[] secondWithoutPreface = new byte[second.Length - 24];
        Array.Copy(second, 24, secondWithoutPreface, 0, secondWithoutPreface.Length);
        byte[] payload = Combine(first, secondWithoutPreface);
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        // Act
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        string firstPath = enumerator.Current.Request.Path.Value;
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        string secondPath = enumerator.Current.Request.Path.Value;

        // Assert
        firstPath.ShouldBe("/one");
        secondPath.ShouldBe("/two");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should emit a graceful GOAWAY on connection disposal")]
    public async Task Http2_OnDispose_ShouldEmitGracefulGoAway()
    {
        // RFC 9113 §6.8 — a server initiating an orderly connection close MUST
        // emit a GOAWAY frame carrying NO_ERROR before tearing down the wire.
        // This fixes #686: without the graceful close, Http2Connection.Dispose
        // can race the underlying socket's send task and lose buffered bytes.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);

        await using (IHttpConnection httpConnection = await listener.AcceptOrListenAsync())
        {
            IHttpConnectionContext httpConnectionContext = await httpConnection.OpenAsync();
            IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);
            httpContext.Response.StatusCode = HttpStatusCode.Ok;
            await httpConnectionContext.SendAsync(httpContext);
        }

        // Read the full output of the connection — the last frame should be a
        // GOAWAY(NO_ERROR) carrying the highest observed inbound stream ID.
        byte[] output = await transportContext.ReadOutputAsync();
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp2Frames(output);
        (long FrameType, byte[] Payload) goAway = frames[frames.Count - 1];

        goAway.FrameType.ShouldBe(7L); // GOAWAY frame type
        goAway.Payload.Length.ShouldBe(8);
        // Last 4 octets carry the error code; NO_ERROR = 0x0.
        uint errorCode = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload.AsSpan(4, 4));
        errorCode.ShouldBe(0u);
        // First 4 octets carry the last-stream-id; we processed stream 1.
        uint lastStreamId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload.AsSpan(0, 4));
        lastStreamId.ShouldBe(1u);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should serialize concurrent SendAsync writes without frame interleaving")]
    public async Task Http2_OnConcurrentSendAsync_ShouldNotInterleaveFrames()
    {
        // RFC 9113 §4.1 — frames from concurrent senders MUST NOT interleave on
        // the wire. PR #686 added a per-connection write semaphore so two
        // SendAsync calls cannot tear each other's HEADERS+DATA sequence even
        // when invoked from racing tasks. This test confirms each DATA frame
        // carries a contiguous response body (no split / interleave).
        byte[] firstRequest = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/one", "https", "api.test");
        byte[] secondRequest = HttpProtocolPayloadFactory.CreateHttp2Request(3, "GET", "/two", "https", "api.test");
        byte[] secondRequestWithoutPreface = new byte[secondRequest.Length - 24];
        Array.Copy(secondRequest, 24, secondRequestWithoutPreface, 0, secondRequestWithoutPreface.Length);
        byte[] combined = Combine(firstRequest, secondRequestWithoutPreface);

        TestTransportConnectionContext transportContext = new(combined);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext firstContext = enumerator.Current;
        const string firstBody = "AAAAAAAAAAAAAAAA";
        firstContext.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes(firstBody));

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext secondContext = enumerator.Current;
        const string secondBody = "BBBBBBBBBBBBBBBB";
        secondContext.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes(secondBody));

        // Drive both SendAsync calls concurrently. Without the write lock, the
        // resulting DATA frame payloads could be split / interleaved across
        // the two streams; with the lock each stream's HEADERS+DATA sequence
        // is atomic.
        Task firstSend = httpConnectionContext.SendAsync(firstContext).AsTask();
        Task secondSend = httpConnectionContext.SendAsync(secondContext).AsTask();
        await Task.WhenAll(firstSend, secondSend);

        // Drain the wire output exactly once and inspect every DATA frame.
        byte[] output = await transportContext.ReadOutputAsync();
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp2Frames(output);

        List<string> dataPayloads = new();
        foreach ((long frameType, byte[] payload) in frames)
        {
            if (frameType == 0) // DATA
            {
                dataPayloads.Add(Encoding.ASCII.GetString(payload));
            }
        }

        // Exactly two DATA frames, each carrying its full response body
        // contiguously. Interleaving would split the body across multiple
        // DATA payloads or mix the two bodies into a single payload.
        dataPayloads.Count.ShouldBe(2);
        dataPayloads.ShouldContain(firstBody);
        dataPayloads.ShouldContain(secondBody);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should keep a localhost connection open for sequential requests")]
    public async Task Http2_OnSequentialLocalhostRequests_ShouldKeepConnectionOpen()
    {
        // Arrange
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(10));
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        int port = GetAvailablePort();
        List<string> observedPaths = new();

        // No client-side handshake to gate teardown — #686 fixed the race where
        // Http2Connection.DisposeAsync could close the socket before the send
        // task had moved the response bytes from the pipe to the wire. With the
        // graceful close in place, the server task can `break` out of its
        // receive loop the moment SendAsync returns and the test still passes
        // deterministically.
        await using HttpConnectionListener listener = HttpConnectionListener.Create(options =>
        {
            options.UseHttp2(transport =>
            {
                transport.EndPoint = new IPEndPoint(IPAddress.Loopback, port);
            });
        });

        Task serverTask = Task.Run(async () =>
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken);
            IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken);

            await foreach (IHttpContext context in connectionContext.ReceiveAsync(cancellationToken))
            {
                try
                {
                    observedPaths.Add(context.Request.Path.Value);

                    byte[] body = Encoding.UTF8.GetBytes(context.Request.Path.Value);
                    context.Response.StatusCode = HttpStatusCode.Ok;
                    context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
                    await context.Response.Body.WriteAsync(body, 0, body.Length, cancellationToken);
                    await connectionContext.SendAsync(context, cancellationToken);
                }
                finally
                {
                    await context.DisposeAsync();
                }

                if (observedPaths.Count >= 2)
                {
                    break;
                }
            }
        }, cancellationToken);

        await Task.Delay(200, cancellationToken);

        using SocketsHttpHandler handler = new();
        using HttpClient client = new(handler);

        // Act
        string firstBody = await SendHttp2RequestAsync(client, new Uri($"http://127.0.0.1:{port}/one"), cancellationToken);
        string secondBody = await SendHttp2RequestAsync(client, new Uri($"http://127.0.0.1:{port}/two"), cancellationToken);

        await serverTask;

        // Assert
        firstBody.ShouldBe("/one");
        secondBody.ShouldBe("/two");
        observedPaths.Count.ShouldBe(2);
        observedPaths[0].ShouldBe("/one");
        observedPaths[1].ShouldBe("/two");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should advertise explicit server SETTINGS after the preface")]
    public async Task Http2_OnInitialization_ShouldEmitExplicitServerSettings()
    {
        // RFC 9113 §3.4 — the server's first frame after the preface MUST be a
        // SETTINGS frame. We advertise our local defaults so the peer never has
        // to guess; the most important one is ENABLE_PUSH=0 because the server
        // does not implement push.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);
        httpContext.Response.StatusCode = HttpStatusCode.Ok;
        await httpConnectionContext.SendAsync(httpContext);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp2Frames(await transportContext.ReadOutputAsync());

        // First frame must be the server SETTINGS (frame type 0x4).
        frames.Count.ShouldBeGreaterThanOrEqualTo(1);
        frames[0].FrameType.ShouldBe(4);
        (frames[0].Payload.Length % 6).ShouldBe(0);

        Dictionary<Http2TestSettings.Parameter, uint> declared = Http2TestSettings.ReadSettings(frames[0].Payload);
        declared[Http2TestSettings.Parameter.EnablePush].ShouldBe(0u);
        declared[Http2TestSettings.Parameter.HeaderTableSize].ShouldBe(4096u);
        declared[Http2TestSettings.Parameter.MaxFrameSize].ShouldBe(16384u);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject an invalid connection preface with PROTOCOL_ERROR")]
    public async Task Http2_OnInvalidPreface_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §3.4 — a preface mismatch is a connection error and MUST
        // surface to the peer as GOAWAY(PROTOCOL_ERROR) before the transport
        // is torn down.
        byte[] payload = Encoding.ASCII.GetBytes("NOT THE PRI * PREFACE\r\n\r\n");
        await AssertGoAwayAsync(payload, Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject a non-SETTINGS first client frame with PROTOCOL_ERROR")]
    public async Task Http2_OnFirstClientFrameNotSettings_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §3.4 — the first client frame after the preface MUST be a
        // SETTINGS frame. Any other frame type is a PROTOCOL_ERROR.
        byte[] preface = Http2TestSettings.Preface();
        byte[] pingFrame = Http2TestSettings.RawFrame(frameType: 0x6, flags: 0, streamId: 0, payload: new byte[8]);
        await AssertGoAwayAsync(Combine(preface, pingFrame), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject SETTINGS on a non-zero stream with PROTOCOL_ERROR")]
    public async Task Http2_OnSettingsOnNonZeroStream_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §6.5.1 — SETTINGS frames MUST be sent on stream 0.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settingsOnStream1 = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0, streamId: 1, payload: Array.Empty<byte>());
        await AssertGoAwayAsync(Combine(preface, settingsOnStream1), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject a SETTINGS ACK that carries a payload with FRAME_SIZE_ERROR")]
    public async Task Http2_OnSettingsAckWithPayload_ShouldGoAwayFrameSizeError()
    {
        // RFC 9113 §6.5 — an ACK SETTINGS frame MUST have an empty payload.
        // Receiving an ACK with any payload is a FRAME_SIZE_ERROR.
        byte[] preface = Http2TestSettings.Preface();
        byte[] ackWithPayload = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0x1, streamId: 0, payload: new byte[6]);
        await AssertGoAwayAsync(Combine(preface, ackWithPayload), Http2ErrorCode.FrameSizeError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject SETTINGS payload not a multiple of 6 with FRAME_SIZE_ERROR")]
    public async Task Http2_OnSettingsPayloadNotMultipleOfSix_ShouldGoAwayFrameSizeError()
    {
        // RFC 9113 §6.5.1 — each setting is 6 octets (16-bit id + 32-bit value).
        // A non-multiple-of-six payload is a FRAME_SIZE_ERROR.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0, streamId: 0, payload: new byte[5]);
        await AssertGoAwayAsync(Combine(preface, settings), Http2ErrorCode.FrameSizeError);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject MAX_FRAME_SIZE outside [2^14, 2^24-1] with PROTOCOL_ERROR")]
    [InlineData(16_383u)]      // one below minimum (2^14 = 16384)
    [InlineData(16_777_216u)]  // one above maximum (2^24 - 1 = 16777215)
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public async Task Http2_OnInvalidMaxFrameSize_ShouldGoAwayProtocolError(uint value)
    {
        byte[] preface = Http2TestSettings.Preface();
        byte[] payload = Http2TestSettings.SettingsPayload((Http2TestSettings.Parameter.MaxFrameSize, value));
        byte[] settings = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0, streamId: 0, payload: payload);
        await AssertGoAwayAsync(Combine(preface, settings), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject INITIAL_WINDOW_SIZE above 2^31-1 with FLOW_CONTROL_ERROR")]
    public async Task Http2_OnInvalidInitialWindowSize_ShouldGoAwayFlowControlError()
    {
        // RFC 9113 §6.5.2 — a value greater than 2^31-1 is a FLOW_CONTROL_ERROR.
        byte[] preface = Http2TestSettings.Preface();
        byte[] payload = Http2TestSettings.SettingsPayload((Http2TestSettings.Parameter.InitialWindowSize, (uint)int.MaxValue + 1u));
        byte[] settings = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0, streamId: 0, payload: payload);
        await AssertGoAwayAsync(Combine(preface, settings), Http2ErrorCode.FlowControlError);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject ENABLE_PUSH not in {0,1} with PROTOCOL_ERROR")]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public async Task Http2_OnInvalidEnablePush_ShouldGoAwayProtocolError(uint value)
    {
        byte[] preface = Http2TestSettings.Preface();
        byte[] payload = Http2TestSettings.SettingsPayload((Http2TestSettings.Parameter.EnablePush, value));
        byte[] settings = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0, streamId: 0, payload: payload);
        await AssertGoAwayAsync(Combine(preface, settings), Http2ErrorCode.ProtocolError);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject ENABLE_CONNECT_PROTOCOL not in {0,1} with PROTOCOL_ERROR")]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public async Task Http2_OnInvalidEnableConnectProtocol_ShouldGoAwayProtocolError(uint value)
    {
        // RFC 8441 §3 — the value MUST be 0 or 1.
        byte[] preface = Http2TestSettings.Preface();
        byte[] payload = Http2TestSettings.SettingsPayload((Http2TestSettings.Parameter.EnableConnectProtocol, value));
        byte[] settings = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0, streamId: 0, payload: payload);
        await AssertGoAwayAsync(Combine(preface, settings), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should silently ignore unknown SETTINGS parameters")]
    public async Task Http2_OnUnknownSettingsParameter_ShouldIgnore()
    {
        // RFC 9113 §6.5.2 — an endpoint MUST ignore unknown setting identifiers
        // so future-defined parameters do not break a compliant endpoint.
        const ushort unknownParameter = 0xFEED;
        byte[] preface = Http2TestSettings.Preface();
        byte[] payload = Http2TestSettings.SettingsPayloadRaw((unknownParameter, 1234u));
        byte[] settings = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0, streamId: 0, payload: payload);
        // Follow with a valid request so we can confirm the connection continued.
        byte[] request = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        byte[] requestWithoutPreface = new byte[request.Length - preface.Length];
        Array.Copy(request, preface.Length, requestWithoutPreface, 0, requestWithoutPreface.Length);

        TestTransportConnectionContext transportContext = new(Combine(preface, settings, requestWithoutPreface));
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Path.Value.ShouldBe("/");
    }

    /// <summary>
    /// Drives the receive loop on a server with the supplied bytes and asserts
    /// that the output stream contains a GOAWAY frame whose error code matches
    /// <paramref name="expectedErrorCode"/>. The receive loop is expected to
    /// throw <see cref="Http2ConnectionException"/> carrying the same code.
    /// </summary>
    private static async Task AssertGoAwayAsync(byte[] payload, Http2ErrorCode expectedErrorCode)
    {
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        Exception? captured = null;
        try
        {
            await foreach (IHttpContext _ in httpConnectionContext.ReceiveAsync())
            {
                // Should not yield — these payloads are all malformed.
            }
        }
        catch (Exception error)
        {
            captured = error;
        }

        captured.ShouldNotBeNull();
        captured.ShouldBeOfType<Http2ConnectionException>();
        ((Http2ConnectionException)captured!).ErrorCode.ShouldBe(expectedErrorCode);

        // The peer must have observed a GOAWAY frame on the wire carrying the
        // same error code as the exception.
        byte[] output = await transportContext.ReadOutputAsync();
        Http2TestSettings.AssertContainsGoAway(output, expectedErrorCode);
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

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private static async Task<string> SendHttp2RequestAsync(HttpClient client, Uri requestUri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(NetHttpMethod.Get, requestUri)
        {
            Version = System.Net.HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.Version.ShouldBe(System.Net.HttpVersion.Version20);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static int GetAvailablePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
