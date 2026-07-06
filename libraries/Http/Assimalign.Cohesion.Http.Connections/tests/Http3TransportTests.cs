using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class Http3TransportTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should parse a request stream and write response frames")]
    public async Task Http3_OnRequest_ShouldParseRequestStreamAndWriteResponseFrames()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/q?id=9", "https", "a");
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert request
        httpContext.Version.ShouldBe(HttpVersion.Http30);
        httpContext.Request.Method.ShouldBe(HttpMethod.Get);
        httpContext.Request.Path.Value.ShouldBe("/q");
        httpContext.Request.Query["id"].Value.ShouldBe("9");
        httpContext.Request.Host.Value.ShouldBe("a");
        httpContext.Request.Scheme.ShouldBe(HttpScheme.Https);

        // Act response
        httpContext.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
        httpContext.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("quic"));
        await httpConnectionContext.SendAsync(httpContext);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());

        // Assert response
        frames.Count.ShouldBe(2);
        frames[0].FrameType.ShouldBe(1);
        frames[1].FrameType.ShouldBe(0);

        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload);
        headers[":status"].ShouldBe("200");
        headers["content-type"].ShouldBe("text/plain");
        headers["content-length"].ShouldBe("4");
        Encoding.UTF8.GetString(frames[1].Payload).ShouldBe("quic");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should yield multiple inbound request streams")]
    public async Task Http3_OnMultipleStreams_ShouldYieldRequestsInSequence()
    {
        // Arrange
        TestConnection firstStream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/one", "https", "a"));
        TestConnection secondStream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/two", "https", "a"));
        TestMultiplexedConnection connection = new(firstStream, secondStream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

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

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should accept a control stream + SETTINGS and still process the request")]
    public async Task Http3_OnControlStreamThenRequest_ShouldProcessRequest()
    {
        // RFC 9114 §6.2.1 / §7.2.4 — the peer opens a unidirectional control
        // stream (type 0x00) whose first frame is SETTINGS, then a request on a
        // bidirectional stream. The control stream is consumed and the request
        // is still surfaced.
        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x01, 0), (0x06, 8192)),
            ConnectionDirection.ReadOnly);
        TestConnection request = new(
            HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/c", "https", "a"));
        TestMultiplexedConnection connection = new(control, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Path.Value.ShouldBe("/c");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should accept a QPACK encoder stream and still process the request")]
    public async Task Http3_OnQPackStreamThenRequest_ShouldProcessRequest()
    {
        TestConnection qpack = new(
            HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(0x02 /* QPACK encoder */),
            ConnectionDirection.ReadOnly);
        TestConnection request = new(
            HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/q", "https", "a"));
        TestMultiplexedConnection connection = new(qpack, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Path.Value.ShouldBe("/q");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should terminate on a duplicate control stream")]
    public async Task Http3_OnDuplicateControlStream_ShouldTerminate()
    {
        // RFC 9114 §6.2.1 — only one control stream per peer; a second is
        // H3_STREAM_CREATION_ERROR, a connection error.
        TestConnection first = new(HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x06, 8192)), ConnectionDirection.ReadOnly);
        TestConnection second = new(HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x06, 8192)), ConnectionDirection.ReadOnly);
        TestMultiplexedConnection connection = new(first, second);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should terminate when the control stream's first frame is not SETTINGS")]
    public async Task Http3_OnControlStreamMissingSettings_ShouldTerminate()
    {
        // RFC 9114 §6.2.1 — the first control frame MUST be SETTINGS. A control
        // stream (type 0x00) whose first frame is DATA (0x00, length 0) is
        // H3_MISSING_SETTINGS.
        TestConnection badControl = new(
            HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(0x00, new byte[] { 0x00, 0x00 }),
            ConnectionDirection.ReadOnly);
        TestMultiplexedConnection connection = new(badControl);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should terminate on a client-created push stream")]
    public async Task Http3_OnClientPushStream_ShouldTerminate()
    {
        // RFC 9114 §6.2.2 — a client MUST NOT create a push stream (type 0x01);
        // it is H3_STREAM_CREATION_ERROR.
        TestConnection push = new(
            HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(0x01 /* push */),
            ConnectionDirection.ReadOnly);
        TestMultiplexedConnection connection = new(push);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should open a control stream and emit SETTINGS with ENABLE_CONNECT_PROTOCOL = 1")]
    public async Task Http3_OnConnectionOpen_ShouldEmitControlStreamSettings()
    {
        // RFC 9114 §6.2.1 — the server opens its own unidirectional control
        // stream (type 0x00) and sends SETTINGS as its first frame. RFC 9220 §3
        // — SETTINGS_ENABLE_CONNECT_PROTOCOL = 1 advertises extended CONNECT
        // support; RFC 9204 §5 — QPACK_MAX_TABLE_CAPACITY = 0 states the
        // dynamic-table-disabled posture.
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/s", "https", "a"));
        TestMultiplexedConnection connection = new(request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Driving the receive loop opens and writes the control stream at start.
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);
        httpContext.Request.Path.Value.ShouldBe("/s");

        TestConnection? controlStream = connection.ControlStream;
        controlStream.ShouldNotBeNull();
        controlStream!.Direction.ShouldBe(ConnectionDirection.WriteOnly);

        (long streamType, IReadOnlyList<(long FrameType, byte[] Payload)> frames) =
            HttpProtocolPayloadFactory.ParseHttp3UnidirectionalStream(await controlStream.ReadOutputAsync());

        // First bytes: the control stream-type prefix, then a SETTINGS frame.
        streamType.ShouldBe(0x00L);
        frames.Count.ShouldBeGreaterThanOrEqualTo(1);
        frames[0].FrameType.ShouldBe(0x04L);

        IReadOnlyDictionary<long, long> settings = HttpProtocolPayloadFactory.DecodeHttp3Settings(frames[0].Payload);
        settings.ContainsKey(0x08).ShouldBeTrue(); // SETTINGS_ENABLE_CONNECT_PROTOCOL
        settings[0x08].ShouldBe(1L);
        settings.ContainsKey(0x01).ShouldBeTrue(); // QPACK_MAX_TABLE_CAPACITY
        settings[0x01].ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should keep the control stream open while a request is served")]
    public async Task Http3_WhileServingRequest_ShouldKeepControlStreamOpen()
    {
        // RFC 9114 §6.2.1 — the control stream is a critical stream; completing,
        // aborting, or FIN'ing it before connection close is
        // H3_CLOSED_CRITICAL_STREAM at the peer. It must stay open while
        // requests are being served.
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/k", "https", "a"));
        TestMultiplexedConnection connection = new(request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/k");

        // The enumerator is still alive (request in flight), so teardown has not
        // run: the control stream must not have been completed or disposed.
        TestConnection? controlStream = connection.ControlStream;
        controlStream.ShouldNotBeNull();
        controlStream!.State.ShouldBe(ConnectionState.Open);
        controlStream.IsDisposed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should drain post-SETTINGS GOAWAY and MAX_PUSH_ID without erroring")]
    public async Task Http3_OnPeerControlStreamWithTrailingFrames_ShouldDrainAndServe()
    {
        // RFC 9114 §7.2 — post-SETTINGS control frames (GOAWAY §7.2.6,
        // MAX_PUSH_ID §7.2.7) are drained (parse-and-discard) so they cannot
        // accumulate unread. Draining runs on a background task, so the accept
        // loop keeps serving the request; were it inline the accept loop would
        // block on the long-lived control stream and this request would never
        // surface. MAX_PUSH_ID stays inert (the server never pushes).
        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
                new (long, long)[] { (0x01, 0), (0x08, 1) },
                (0x07 /* GOAWAY */, HttpProtocolPayloadFactory.CreateHttp3VarintPayload(0)),
                (0x0D /* MAX_PUSH_ID */, HttpProtocolPayloadFactory.CreateHttp3VarintPayload(0))),
            ConnectionDirection.ReadOnly);
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/g", "https", "a"));
        TestMultiplexedConnection connection = new(control, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        // The request is served despite the trailing control frames...
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/g");

        // ...and the connection then completes cleanly — draining did not error.
        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should decode a Huffman-coded request field section")]
    public async Task Http3_OnHuffmanEncodedRequest_ShouldParseFields()
    {
        // RFC 9204 §4.1.2 — names and values may be Huffman-coded. The whole
        // field section here uses literal field lines with Huffman strings.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            huffman: true,
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/huff"),
            (":authority", "example.com"),
            ("accept", "application/json"));

        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Method.ShouldBe(HttpMethod.Get);
        httpContext.Request.Path.Value.ShouldBe("/huff");
        httpContext.Request.Host.Value.ShouldBe("example.com");
        httpContext.Request.Headers[HttpHeaderKey.Accept].Value.ShouldBe("application/json");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should drop a stream with an uppercase field name")]
    public Task Http3_OnUppercaseFieldName_ShouldDropStream()
        // RFC 9114 §4.2 — uppercase field names are malformed.
        => AssertMalformedRequestIsDroppedAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "a"),
            ("X-Bad", "v")));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should drop a stream with a pseudo-header after a regular field")]
    public Task Http3_OnPseudoHeaderAfterRegularField_ShouldDropStream()
        // RFC 9114 §4.3 — pseudo-headers MUST precede regular fields.
        => AssertMalformedRequestIsDroppedAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "GET"),
            ("x-early", "1"),
            (":scheme", "https"),
            (":path", "/")));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should drop a stream with a duplicate pseudo-header")]
    public Task Http3_OnDuplicatePseudoHeader_ShouldDropStream()
        // RFC 9114 §4.3.1 — a pseudo-header MUST NOT appear more than once.
        => AssertMalformedRequestIsDroppedAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "GET"),
            (":method", "POST"),
            (":scheme", "https"),
            (":path", "/")));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should drop a stream with an unknown pseudo-header")]
    public Task Http3_OnUnknownPseudoHeader_ShouldDropStream()
        // RFC 9114 §4.3.1 — an unknown request pseudo-header is malformed.
        => AssertMalformedRequestIsDroppedAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "GET"),
            (":bogus", "x"),
            (":scheme", "https"),
            (":path", "/")));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should drop a stream missing the :path pseudo-header")]
    public Task Http3_OnMissingPathPseudoHeader_ShouldDropStream()
        // RFC 9114 §4.3.1 — a non-CONNECT request MUST include :path.
        => AssertMalformedRequestIsDroppedAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "GET"),
            (":scheme", "https"),
            (":authority", "a")));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should surface a valid extended CONNECT via the :protocol item")]
    public async Task Http3_OnExtendedConnect_ShouldSurfaceProtocolItem()
    {
        // RFC 9220 — CONNECT + :protocol with :scheme/:path/:authority is a
        // valid extended CONNECT. The transport surfaces the :protocol
        // pseudo-header verbatim through IHttpContext.Items so the
        // ExtendedConnect package can model it without a transport dependency.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "api.test"));

        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Method.ShouldBe(HttpMethod.Connect);
        httpContext.Request.Path.Value.ShouldBe("/chat");
        httpContext.Items.ContainsKey(TransportItemKeys.Protocol).ShouldBeTrue();
        httpContext.Items[TransportItemKeys.Protocol].ShouldBe("websocket");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: A valid extended CONNECT exposes the ExtendedConnect feature")]
    public async Task Http3_OnExtendedConnect_ShouldExposeExtendedConnectFeature()
    {
        // The transport surfaces :protocol via IHttpContext.Items; the
        // Http.ExtendedConnect package models it as a typed feature.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "api.test"));

        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.IsExtendedConnect.ShouldBeTrue();
        httpContext.ExtendedConnect.ShouldNotBeNull();
        httpContext.ExtendedConnect!.Protocol.ShouldBe("websocket");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should drop :protocol on a non-CONNECT request")]
    public Task Http3_OnProtocolPseudoHeaderWithoutConnect_ShouldDropStream()
        // RFC 8441 §4 / RFC 9220 — :protocol is only valid on a CONNECT request.
        => AssertMalformedRequestIsDroppedAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "GET"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/"),
            (":authority", "api.test")));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should drop an extended CONNECT missing :path")]
    public Task Http3_OnExtendedConnectMissingPath_ShouldDropStream()
        // RFC 9220 — an extended CONNECT MUST include :scheme, :path, :authority.
        => AssertMalformedRequestIsDroppedAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":authority", "api.test")));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Enabling the dynamic table advertises QPACK capacity and blocked streams")]
    public async Task Http3_OnDynamicTableEnabled_ShouldAdvertiseCapacityAndBlockedStreams()
    {
        // RFC 9204 §5 — with the dynamic table enabled the server advertises a
        // non-zero QPACK_MAX_TABLE_CAPACITY and QPACK_BLOCKED_STREAMS in SETTINGS.
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/s", "https", "a"));
        TestMultiplexedConnection connection = new(request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), static o =>
        {
            o.QPack.MaxTableCapacity = 4096;
            o.QPack.MaxBlockedStreams = 16;
        });

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);
        httpContext.Request.Path.Value.ShouldBe("/s");

        TestConnection? controlStream = connection.ControlStream;
        controlStream.ShouldNotBeNull();

        (long streamType, IReadOnlyList<(long FrameType, byte[] Payload)> frames) =
            HttpProtocolPayloadFactory.ParseHttp3UnidirectionalStream(await controlStream!.ReadOutputAsync());

        streamType.ShouldBe(0x00L);
        IReadOnlyDictionary<long, long> settings = HttpProtocolPayloadFactory.DecodeHttp3Settings(frames[0].Payload);
        settings[0x01].ShouldBe(4096L); // QPACK_MAX_TABLE_CAPACITY
        settings[0x07].ShouldBe(16L);   // QPACK_BLOCKED_STREAMS
        settings[0x08].ShouldBe(1L);    // SETTINGS_ENABLE_CONNECT_PROTOCOL
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should resolve a dynamic-table reference and acknowledge the insert count")]
    public async Task Http3_OnDynamicTableRequest_ShouldResolveDynamicEntryAndAckInsertCount()
    {
        // RFC 9204 — the peer's encoder stream inserts a dynamic entry; a request
        // then references it by a dynamic indexed field line. The decoder resolves
        // it and emits Insert Count Increment on its decoder stream (§4.4.3).
        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x01, 4096), (0x07, 16)),
            ConnectionDirection.ReadOnly);
        TestConnection encoder = new(
            HttpProtocolPayloadFactory.CreateHttp3QPackEncoderStream(
                HttpProtocolPayloadFactory.QPackSetCapacity(4096),
                HttpProtocolPayloadFactory.QPackInsertWithLiteralName("x-dyn", "hello")),
            ConnectionDirection.ReadOnly);

        // Encoded RIC 2 → RIC 1; Delta Base 0 → Base 1; dynamic indexed rel 0 → absolute 0.
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3DynamicRequest(
            encodedRequiredInsertCount: 2,
            deltaBaseByte: 0x00,
            literalFields: [(":method", "GET"), (":scheme", "https"), (":path", "/d"), (":authority", "a")],
            0));

        TestMultiplexedConnection connection = new(control, encoder, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), static o =>
        {
            o.QPack.MaxTableCapacity = 4096;
            o.QPack.MaxBlockedStreams = 16;
        });

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext httpContext = enumerator.Current;
        httpContext.Request.Path.Value.ShouldBe("/d");
        httpContext.Request.Headers[new HttpHeaderKey("x-dyn")].Value.ShouldBe("hello");

        // Drive the loop to completion so teardown awaits the encoder drain.
        (await enumerator.MoveNextAsync()).ShouldBeFalse();

        // OpenedStreams: [0] control, [1] decoder. The decoder stream carries its
        // type prefix (0x03) then Insert Count Increment(1) = 0x01.
        connection.OpenedStreams.Count.ShouldBeGreaterThanOrEqualTo(2);
        byte[] decoderOutput = await connection.OpenedStreams[1].ReadOutputAsync();
        decoderOutput[0].ShouldBe((byte)0x03);
        decoderOutput.ShouldContain((byte)0x01);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should terminate when a request blocks beyond QPACK_BLOCKED_STREAMS")]
    public async Task Http3_OnDynamicReferenceBeyondBlockedStreamLimit_ShouldTerminate()
    {
        // RFC 9204 §2.1.2 / §2.2 — the dynamic table is enabled but the
        // blocked-stream budget is 0, so a request referencing a not-yet-inserted
        // dynamic entry is a QPACK_DECOMPRESSION_FAILED connection error.
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3DynamicRequest(
            encodedRequiredInsertCount: 2,
            deltaBaseByte: 0x00,
            literalFields: [(":method", "GET"), (":scheme", "https"), (":path", "/d"), (":authority", "a")],
            0));
        TestMultiplexedConnection connection = new(request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection), static o =>
        {
            o.QPack.MaxTableCapacity = 4096;
            o.QPack.MaxBlockedStreams = 0;
        });

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    private static async Task AssertMalformedRequestIsDroppedAsync(byte[] requestPayload)
    {
        // A per-stream field-section failure drops the offending stream; with
        // a single stream the connection then ends, so no context is yielded.
        TestConnection stream = new(requestPayload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }
}
