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
using Assimalign.Cohesion.Http.Transports.Internal.Http2.HPack;
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
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

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
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

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
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

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
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

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
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

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

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject a client-sent PUSH_PROMISE with PROTOCOL_ERROR")]
    public async Task Http2_OnClientPushPromise_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §8.4 / §6.6 — only servers send PUSH_PROMISE; a client
        // cannot push. A server MUST treat receipt of a PUSH_PROMISE as a
        // connection error of type PROTOCOL_ERROR. Cohesion de-scopes server
        // push entirely (see docs/DESIGN.md), so this is also the enforcement
        // of that decision: a peer can never coax the server into a push flow.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0, streamId: 0, payload: Array.Empty<byte>());
        // PUSH_PROMISE (0x5) on stream 1 with a promised-stream-id + empty block.
        byte[] pushPromise = Http2TestSettings.RawFrame(frameType: 0x5, flags: 0x4 /* END_HEADERS */, streamId: 1, payload: new byte[] { 0x00, 0x00, 0x00, 0x02 });
        await AssertGoAwayAsync(Combine(preface, settings, pushPromise), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should advertise SETTINGS_ENABLE_PUSH = 0")]
    public async Task Http2_OnConnect_ShouldAdvertisePushDisabled()
    {
        // Cohesion does not implement server push, so the server's initial
        // SETTINGS MUST advertise ENABLE_PUSH = 0 (RFC 9113 §6.5.2) to tell the
        // peer not to expect pushed streams.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(frameType: 0x4, flags: 0, streamId: 0, payload: Array.Empty<byte>());

        TestTransportConnectionContext transportContext = new(Combine(preface, settings));
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Drain the receive loop to drive initialization — the server writes
        // its initial SETTINGS during init. The payload is only preface +
        // client SETTINGS (no request), so the loop initializes, ACKs, and
        // completes at end-of-stream without yielding a context.
        await foreach (IHttpContext _ in httpConnectionContext.ReceiveAsync())
        {
        }

        byte[] output = await transportContext.ReadOutputAsync();
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp2Frames(output);

        byte[]? settingsPayload = null;
        foreach ((long frameType, byte[] payload) in frames)
        {
            if (frameType == 0x4)
            {
                settingsPayload = payload;
                break;
            }
        }

        settingsPayload.ShouldNotBeNull();
        Dictionary<Http2TestSettings.Parameter, uint> serverSettings = Http2TestSettings.ReadSettings(settingsPayload!);
        serverSettings.ContainsKey(Http2TestSettings.Parameter.EnablePush).ShouldBeTrue();
        serverSettings[Http2TestSettings.Parameter.EnablePush].ShouldBe(0u);
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
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Path.Value.ShouldBe("/");
    }

    /// <summary>
    /// Drives the receive loop on a server with the supplied bytes and asserts
    /// that the output stream contains a GOAWAY frame whose error code matches
    /// <paramref name="expectedErrorCode"/>. The receive loop emits GOAWAY on
    /// the wire then yields nothing — by contract a malformed peer must never
    /// crash the listener, so the enumerable completes without throwing.
    /// </summary>
    private static async Task AssertGoAwayAsync(byte[] payload, Http2ErrorCode expectedErrorCode)
    {
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // The receive loop must absorb the protocol violation: emit GOAWAY on
        // the wire (asserted below) and complete the enumerable without
        // propagating an exception, so a malformed peer cannot crash the
        // listener.
        await foreach (IHttpContext _ in httpConnectionContext.ReceiveAsync())
        {
            // Should not yield — these payloads are all malformed.
        }

        // The peer must have observed a GOAWAY frame on the wire carrying the
        // expected error code.
        byte[] output = await transportContext.ReadOutputAsync();
        Http2TestSettings.AssertContainsGoAway(output, expectedErrorCode);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject HEADERS on stream 0 with PROTOCOL_ERROR")]
    public async Task Http2_OnHeadersOnStreamZero_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §5.1.1 — stream 0 is reserved for connection-control
        // frames. HEADERS on stream 0 is a connection error.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headersOnStreamZero = Http2TestSettings.RawFrame(0x1, 0x4 /* END_HEADERS */, 0, Array.Empty<byte>());
        await AssertGoAwayAsync(Combine(preface, settings, headersOnStreamZero), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject HEADERS on an even stream ID with PROTOCOL_ERROR")]
    public async Task Http2_OnHeadersOnEvenStreamId_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §5.1.1 — client-initiated streams MUST use odd
        // identifiers. Server-initiated (even) ids are only legal when
        // produced by the server itself via PUSH_PROMISE, which Cohesion
        // disables. An even client-initiated id is a connection error.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headersOnEvenId = Http2TestSettings.RawFrame(0x1, 0x4, 2, Array.Empty<byte>());
        await AssertGoAwayAsync(Combine(preface, settings, headersOnEvenId), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject HEADERS on a stream id lower than the highest previously seen")]
    public async Task Http2_OnHeadersOnDecreasingStreamId_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §5.1.1 — newly opened client streams MUST use a
        // strictly higher id than every previously opened client stream.
        byte[] firstRequest = HttpProtocolPayloadFactory.CreateHttp2Request(3, "GET", "/three", "https", "api.test");
        // Build a HEADERS frame on stream 1 (lower) — the connection has
        // already seen stream 3, so opening stream 1 now is an ordering
        // violation.
        byte[] lowerIdHeaders = Http2TestSettings.RawFrame(0x1, 0x4 | 0x1 /* END_HEADERS + END_STREAM */, 1, Array.Empty<byte>());
        await AssertGoAwayAsync(Combine(firstRequest, lowerIdHeaders), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject DATA on stream 0 with PROTOCOL_ERROR")]
    public async Task Http2_OnDataOnStreamZero_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §6.1 — DATA frames MUST be associated with a non-zero
        // stream.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] dataOnStreamZero = Http2TestSettings.RawFrame(0x0, 0, 0, new byte[] { 1, 2, 3 });
        await AssertGoAwayAsync(Combine(preface, settings, dataOnStreamZero), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject DATA on an unknown/idle stream with PROTOCOL_ERROR")]
    public async Task Http2_OnDataOnIdleStream_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §5.1 — DATA on an idle stream (no HEADERS observed) is
        // a connection error.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] dataOnIdle = Http2TestSettings.RawFrame(0x0, 0, 5, new byte[] { 1, 2, 3 });
        await AssertGoAwayAsync(Combine(preface, settings, dataOnIdle), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject RST_STREAM on stream 0 with PROTOCOL_ERROR")]
    public async Task Http2_OnRstStreamOnStreamZero_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §6.4 — RST_STREAM frames MUST be associated with a
        // specific stream; stream 0 is illegal.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] rstOnStreamZero = Http2TestSettings.RawFrame(0x3, 0, 0, new byte[4]);
        await AssertGoAwayAsync(Combine(preface, settings, rstOnStreamZero), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject RST_STREAM on an idle stream with PROTOCOL_ERROR")]
    public async Task Http2_OnRstStreamOnIdleStream_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §6.4 — RST_STREAM on a stream that has never been
        // opened is a connection error.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] rstOnIdle = Http2TestSettings.RawFrame(0x3, 0, 7, new byte[4]);
        await AssertGoAwayAsync(Combine(preface, settings, rstOnIdle), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reset a stream that receives DATA after END_STREAM")]
    public async Task Http2_OnDataAfterEndStream_ShouldEmitRstStream()
    {
        // RFC 9113 §5.1 — DATA on a stream that has already had its remote
        // half closed is a stream error (STREAM_CLOSED). The connection
        // stays alive; the offending stream is reset.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        // Build a HEADERS+END_STREAM for stream 1 by reusing the factory
        // (which produces a complete preface + SETTINGS + HEADERS bundle)
        // and stripping the preface — we already have one above.
        byte[] requestWithBody = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        byte[] requestWithoutPreface = new byte[requestWithBody.Length - Http2TestSettings.Preface().Length];
        Array.Copy(requestWithBody, Http2TestSettings.Preface().Length, requestWithoutPreface, 0, requestWithoutPreface.Length);
        // Now a DATA frame on the just-closed stream.
        byte[] dataAfterClose = Http2TestSettings.RawFrame(0x0, 0, 1, new byte[] { 1, 2, 3 });

        TestTransportConnectionContext transportContext = new(Combine(requestWithoutPreface, dataAfterClose));
        // Inject our own preface + SETTINGS in front so the connection
        // initialises cleanly.
        transportContext = new(Combine(preface, settings, requestWithoutPreface, dataAfterClose));
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Drive the receive loop. The first iteration produces the valid
        // request. The second MoveNextAsync drives the loop past the
        // DATA-after-END_STREAM frame — the state machine raises a stream
        // error which the loop catches, emits a RST_STREAM, and continues.
        // The pipe is finite, so the next ReadFrameAsync returns null and
        // the loop yields break.
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext first = enumerator.Current;
        first.Response.StatusCode = HttpStatusCode.Ok;
        await httpConnectionContext.SendAsync(first);
        (await enumerator.MoveNextAsync()).ShouldBeFalse();

        byte[] output = await transportContext.ReadOutputAsync();
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp2Frames(output);

        bool foundRstStream = false;
        foreach ((long frameType, byte[] payload) in frames)
        {
            if (frameType == 3) // RST_STREAM
            {
                foundRstStream = true;
                payload.Length.ShouldBe(4);
                uint code = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload);
                code.ShouldBe((uint)Http2ErrorCode.StreamClosed);
            }
        }

        foundRstStream.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject WINDOW_UPDATE with increment 0 on stream 0 with PROTOCOL_ERROR")]
    public async Task Http2_OnWindowUpdateConnectionZeroIncrement_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §6.9 — a WINDOW_UPDATE on stream 0 with an increment of
        // 0 is a connection-level protocol error.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] windowUpdate = Http2TestSettings.RawFrame(0x8, 0, 0, new byte[4]); // 31-bit increment = 0
        await AssertGoAwayAsync(Combine(preface, settings, windowUpdate), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject WINDOW_UPDATE with wrong payload length")]
    public async Task Http2_OnWindowUpdateWrongPayloadLength_ShouldGoAwayFrameSizeError()
    {
        // RFC 9113 §6.9 — WINDOW_UPDATE MUST carry exactly 4 octets of payload.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] windowUpdate = Http2TestSettings.RawFrame(0x8, 0, 0, new byte[3]);
        await AssertGoAwayAsync(Combine(preface, settings, windowUpdate), Http2ErrorCode.FrameSizeError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject WINDOW_UPDATE that overflows the connection send window")]
    public async Task Http2_OnWindowUpdateOverflowingConnectionWindow_ShouldGoAwayFlowControlError()
    {
        // RFC 9113 §6.9.1 — a WINDOW_UPDATE that would push the window past
        // 2^31-1 is a FLOW_CONTROL_ERROR. Start with the default window of
        // 65535 and credit 2^31-1 to push over the limit.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        // Big-endian 31-bit increment = int.MaxValue (= 0x7FFFFFFF).
        byte[] hugeIncrement = new byte[] { 0x7F, 0xFF, 0xFF, 0xFF };
        byte[] windowUpdate = Http2TestSettings.RawFrame(0x8, 0, 0, hugeIncrement);
        await AssertGoAwayAsync(Combine(preface, settings, windowUpdate), Http2ErrorCode.FlowControlError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should accept a valid WINDOW_UPDATE on stream 0")]
    public async Task Http2_OnValidWindowUpdate_ShouldAcceptWithoutError()
    {
        // Sanity check that the happy path through ProcessWindowUpdateFrame
        // does not produce a GOAWAY / RST_STREAM. A standard request follows
        // the WINDOW_UPDATE so we can confirm the connection survives.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        // Increment = 1024, big-endian.
        byte[] increment = new byte[] { 0, 0, 4, 0 };
        byte[] windowUpdate = Http2TestSettings.RawFrame(0x8, 0, 0, increment);
        byte[] request = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        byte[] requestWithoutPreface = new byte[request.Length - preface.Length];
        Array.Copy(request, preface.Length, requestWithoutPreface, 0, requestWithoutPreface.Length);

        TestTransportConnectionContext transportContext = new(Combine(preface, settings, windowUpdate, requestWithoutPreface));
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Path.Value.ShouldBe("/");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should accept inbound GOAWAY without faulting the connection")]
    public async Task Http2_OnInboundGoAway_ShouldNotFaultConnection()
    {
        // RFC 9113 §6.8 — a peer's GOAWAY is a unidirectional close signal;
        // the connection MUST continue to process in-flight streams. We
        // verify that a request preceding GOAWAY still produces a yieldable
        // context.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());

        // GOAWAY payload: 4-byte last-stream-id + 4-byte error code.
        // last-stream-id = 1, error code = 0 (NO_ERROR).
        byte[] goAwayPayload = new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 };
        byte[] goAway = Http2TestSettings.RawFrame(0x7, 0, 0, goAwayPayload);

        byte[] request = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        byte[] requestWithoutPreface = new byte[request.Length - preface.Length];
        Array.Copy(request, preface.Length, requestWithoutPreface, 0, requestWithoutPreface.Length);

        TestTransportConnectionContext transportContext = new(Combine(preface, settings, requestWithoutPreface, goAway));
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // The request landed even though GOAWAY arrived afterwards.
        httpContext.Request.Path.Value.ShouldBe("/");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject GOAWAY on a non-zero stream with PROTOCOL_ERROR")]
    public async Task Http2_OnGoAwayOnNonZeroStream_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §6.8 — GOAWAY MUST be sent on stream 0.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] goAwayPayload = new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 };
        byte[] goAwayOnStream1 = Http2TestSettings.RawFrame(0x7, 0, 1, goAwayPayload);
        await AssertGoAwayAsync(Combine(preface, settings, goAwayOnStream1), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should fire RequestAborted when peer resets the stream")]
    public async Task Http2_OnRstStream_ShouldFireRequestAbortedOnApplicationContext()
    {
        // RFC 9113 §5.4.2 — RST_STREAM aborts the application's view of the
        // request. The Http2Stream's RequestAborted token surfaces this so
        // handler code (e.g. a long-running ReadAsync on the body) can wake
        // up and bail.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());

        // HEADERS WITHOUT END_STREAM so the stream stays open after the
        // request lands and is eligible for reset.
        byte[] request = HttpProtocolPayloadFactory.CreateHttp2Request(1, "POST", "/upload", "https", "api.test");
        byte[] requestWithoutPreface = new byte[request.Length - preface.Length];
        Array.Copy(request, preface.Length, requestWithoutPreface, 0, requestWithoutPreface.Length);
        // Clear END_STREAM on the HEADERS frame so the stream remains open.
        for (int i = 0; i < requestWithoutPreface.Length;)
        {
            int payloadLength = (requestWithoutPreface[i] << 16) | (requestWithoutPreface[i + 1] << 8) | requestWithoutPreface[i + 2];
            byte type = requestWithoutPreface[i + 3];
            if (type == 0x1) // HEADERS
            {
                requestWithoutPreface[i + 4] &= unchecked((byte)~0x1);
            }

            i += 9 + payloadLength;
        }

        // Hmm — without END_STREAM the stream's IsRequestReady is false
        // so the context is never yielded. We need at least a body byte
        // with END_STREAM, OR to keep END_STREAM on the original frame
        // and send RST_STREAM as a follow-up. Use the END_STREAM path:
        // the stream is briefly in HalfClosedRemote when we yield, then
        // RST_STREAM closes it.
        Array.Copy(request, preface.Length, requestWithoutPreface, 0, requestWithoutPreface.Length);

        // RST_STREAM(CANCEL) on stream 1.
        byte[] rstStreamPayload = new byte[] { 0, 0, 0, 0x8 }; // CANCEL
        byte[] rstStream = Http2TestSettings.RawFrame(0x3, 0, 1, rstStreamPayload);

        TestTransportConnectionContext transportContext = new(Combine(preface, settings, requestWithoutPreface, rstStream));
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext httpContext = enumerator.Current;

        // The application sees the request before RST_STREAM is processed
        // because the receive loop yields immediately after HEADERS+END_STREAM.
        // Pump the enumerator so the RST_STREAM frame is consumed; once it
        // is, the stream's RequestAborted should fire.
        Task pump = enumerator.MoveNextAsync().AsTask();

        // Give the receive loop a moment to process RST_STREAM. The token
        // should fire because Http2Stream.ReceiveReset() triggers it.
        for (int i = 0; i < 100 && !httpContext.RequestAborted.IsCancellationRequested; i++)
        {
            await Task.Delay(10);
        }

        httpContext.RequestAborted.IsCancellationRequested.ShouldBeTrue();

        // Make sure the pumped MoveNext completes (it should return false
        // because the connection's input is finite).
        await pump;
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject a request with the response-only :status pseudo-header")]
    public async Task Http2_OnStatusPseudoHeaderInRequest_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §8.3 — :status is a response pseudo-header. Receiving
        // it in a request field section is malformed and surfaces as a
        // connection-level PROTOCOL_ERROR.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId: 1,
            flags: 0x4 | 0x1, // END_HEADERS + END_STREAM
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "api.test"),
            (":status", "200"));
        await AssertGoAwayAsync(Combine(preface, settings, headers), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject pseudo-headers that appear after regular fields")]
    public async Task Http2_OnPseudoHeaderAfterRegularField_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §8.3 — pseudo-header fields MUST appear in the field
        // section BEFORE regular fields. A pseudo-header after a regular
        // field is malformed.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId: 1,
            flags: 0x4 | 0x1,
            (":method", "GET"),
            (":scheme", "https"),
            ("user-agent", "tests"),       // regular field
            (":path", "/"),                 // pseudo-header after regular — illegal
            (":authority", "api.test"));
        await AssertGoAwayAsync(Combine(preface, settings, headers), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject an unknown pseudo-header field")]
    public async Task Http2_OnUnknownPseudoHeader_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §8.3 — pseudo-header names that aren't defined for the
        // message type are malformed.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId: 1,
            flags: 0x4 | 0x1,
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "api.test"),
            (":foo", "bar"));
        await AssertGoAwayAsync(Combine(preface, settings, headers), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject duplicate pseudo-header fields")]
    public async Task Http2_OnDuplicatePseudoHeader_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §8.3 — each pseudo-header MUST appear at most once.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId: 1,
            flags: 0x4 | 0x1,
            (":method", "GET"),
            (":method", "POST"),     // duplicate
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "api.test"));
        await AssertGoAwayAsync(Combine(preface, settings, headers), Http2ErrorCode.ProtocolError);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject connection-specific header fields")]
    [InlineData("connection", "close")]
    [InlineData("proxy-connection", "keep-alive")]
    [InlineData("keep-alive", "timeout=5")]
    [InlineData("transfer-encoding", "chunked")]
    [InlineData("upgrade", "h2c")]
    public async Task Http2_OnConnectionSpecificHeader_ShouldGoAwayProtocolError(string name, string value)
    {
        // RFC 9113 §8.2.2 — these connection-specific header fields are
        // forbidden in HTTP/2 because their semantics conflict with
        // multiplexed framing.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId: 1,
            flags: 0x4 | 0x1,
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "api.test"),
            (name, value));
        await AssertGoAwayAsync(Combine(preface, settings, headers), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject TE field with value other than 'trailers'")]
    public async Task Http2_OnTeFieldNotTrailers_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §8.2.2 — TE MAY appear with the single value
        // 'trailers'. Any other value is malformed.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId: 1,
            flags: 0x4 | 0x1,
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "api.test"),
            ("te", "gzip"));
        await AssertGoAwayAsync(Combine(preface, settings, headers), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should accept TE: trailers")]
    public async Task Http2_OnTeFieldTrailers_ShouldAcceptRequest()
    {
        // RFC 9113 §8.2.2 — TE: trailers is the single legal form. The
        // request should land cleanly with the field surfaced through
        // IHttpRequest.Headers.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId: 1,
            flags: 0x4 | 0x1,
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "api.test"),
            ("te", "trailers"));

        TestTransportConnectionContext transportContext = new(Combine(preface, settings, headers));
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Headers[new HttpHeaderKey("te")].Value.ShouldBe("trailers");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should reject field names with uppercase letters")]
    public async Task Http2_OnUppercaseFieldName_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §8.2.1 — HTTP/2 field names MUST be lowercase. The
        // encoder is required to lower-case names before sending; a
        // mixed-case name is malformed.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId: 1,
            flags: 0x4 | 0x1,
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "api.test"),
            ("User-Agent", "tests"));
        await AssertGoAwayAsync(Combine(preface, settings, headers), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Should coalesce multiple Cookie fields into one with '; ' separator")]
    public async Task Http2_OnMultipleCookieFields_ShouldCoalesceWithSeparator()
    {
        // RFC 9113 §8.2.3 — multiple Cookie field-lines in an HTTP/2 field
        // section MUST be coalesced into a single Cookie field with a
        // "; " separator before being passed to higher layers.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId: 1,
            flags: 0x4 | 0x1,
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "api.test"),
            ("cookie", "a=1"),
            ("cookie", "b=2"));

        TestTransportConnectionContext transportContext = new(Combine(preface, settings, headers));
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Headers[HttpHeaderKey.Cookie].Value.ShouldBe("a=1; b=2");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - Http2: HPackHuffmanDecoder should decode RFC 7541 §C.4 example strings")]
    // RFC 7541 §C.4.2 — `cache-control: no-cache`
    [InlineData(new byte[] { 0xa8, 0xeb, 0x10, 0x64, 0x9c, 0xbf }, "no-cache")]
    // RFC 7541 §C.4.1 — `:authority: www.example.com`
    [InlineData(new byte[] { 0xf1, 0xe3, 0xc2, 0xe5, 0xf2, 0x3a, 0x6b, 0xa0, 0xab, 0x90, 0xf4, 0xff }, "www.example.com")]
    // RFC 7541 §C.4.3 — `custom-key`
    [InlineData(new byte[] { 0x25, 0xa8, 0x49, 0xe9, 0x5b, 0xa9, 0x7d, 0x7f }, "custom-key")]
    // RFC 7541 §C.4.3 — `custom-value`
    [InlineData(new byte[] { 0x25, 0xa8, 0x49, 0xe9, 0x5b, 0xb8, 0xe8, 0xb4, 0xbf }, "custom-value")]
    public void Http2_HPackHuffmanDecoder_ShouldDecodeRfcExamples(byte[] encoded, string expected)
    {
        // RFC 7541 Appendix C gives canonical Huffman examples; these
        // verify the decoder + table against the spec's exact wire bytes.
        byte[] decoded = HPackHuffmanDecoder.Decode(encoded);
        Encoding.ASCII.GetString(decoded).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: HPackHuffmanDecoder should reject embedded EOS")]
    public void Http2_HPackHuffmanDecoder_ShouldRejectEmbeddedEos()
    {
        // RFC 7541 §5.2 — the EOS symbol (30 bits of 1) MUST NOT appear in
        // a decoded sequence; encountering it is a decoding error.
        // 30 ones + 2 ones padding packs to four 0xff octets.
        byte[] encoded = { 0xff, 0xff, 0xff, 0xff };
        Should.Throw<HPackDecodingException>(() => HPackHuffmanDecoder.Decode(encoded));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Http2FlowControlWindow.TryConsume should drain to zero and refuse underflow")]
    public void Http2_FlowControlWindow_TryConsume_RefusesUnderflow()
    {
        Http2FlowControlWindow window = new(100);

        window.TryConsume(40).ShouldBeTrue();
        window.Available.ShouldBe(60L);
        window.TryConsume(60).ShouldBeTrue();
        window.Available.ShouldBe(0L);
        // Drained — any additional consume refuses.
        window.TryConsume(1).ShouldBeFalse();
        window.Available.ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Http2FlowControlWindow.TryReplenish should refuse 0 increments and overflow")]
    public void Http2_FlowControlWindow_TryReplenish_RefusesZeroAndOverflow()
    {
        Http2FlowControlWindow window = new(65_535);

        // RFC 9113 §6.9 — a 0 increment is a protocol error; the helper
        // surfaces it as a refused replenish.
        window.TryReplenish(0).ShouldBeFalse();
        window.Available.ShouldBe(65_535L);

        // Replenishing up to the cap is allowed; pushing past 2^31-1 is
        // not (FLOW_CONTROL_ERROR per §6.9.1).
        window.TryReplenish(int.MaxValue - 65_535).ShouldBeTrue();
        window.Available.ShouldBe((long)int.MaxValue);
        window.TryReplenish(1).ShouldBeFalse();
        window.Available.ShouldBe((long)int.MaxValue);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http2: Http2FlowControlWindow.TryAdjustInitialWindow should allow negative windows")]
    public void Http2_FlowControlWindow_TryAdjustInitialWindow_AllowsNegative()
    {
        // RFC 9113 §6.9.2 — a SETTINGS_INITIAL_WINDOW_SIZE reduction can
        // drive an existing stream's send window negative. The helper
        // allows that; only overflow above 2^31-1 is rejected.
        Http2FlowControlWindow window = new(100);
        window.TryAdjustInitialWindow(-150).ShouldBeTrue();
        window.Available.ShouldBe(-50L);

        // Overflow rejection.
        Http2FlowControlWindow large = new(int.MaxValue - 5);
        large.TryAdjustInitialWindow(10).ShouldBeFalse();
        large.Available.ShouldBe((long)int.MaxValue - 5);
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
