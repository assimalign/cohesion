using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;
using Assimalign.Cohesion.Transports;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Transports.Tests;

public class Http3TransportTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http3: Should parse a request stream and write response frames")]
    public async Task Http3_OnRequest_ShouldParseRequestStreamAndWriteResponseFrames()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/q?id=9", "https", "a");
        TestTransportConnectionContext streamContext = new(payload);
        TestMultiplexTransportConnection connection = new(new[] { streamContext }, TransportProtocol.Quic);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http30, new TestServerTransport(TransportProtocol.Quic, new TransportConnection[] { connection }), isSecure: true);

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

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await streamContext.ReadOutputAsync());

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

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http3: Should yield multiple inbound request streams")]
    public async Task Http3_OnMultipleStreams_ShouldYieldRequestsInSequence()
    {
        // Arrange
        TestTransportConnectionContext firstStream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/one", "https", "a"));
        TestTransportConnectionContext secondStream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/two", "https", "a"));
        TestMultiplexTransportConnection connection = new(new[] { firstStream, secondStream }, TransportProtocol.Quic);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http30, new TestServerTransport(TransportProtocol.Quic, new TransportConnection[] { connection }), isSecure: true);

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

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http3: Should accept a control stream + SETTINGS and still process the request")]
    public async Task Http3_OnControlStreamThenRequest_ShouldProcessRequest()
    {
        // RFC 9114 §6.2.1 / §7.2.4 — the peer opens a unidirectional control
        // stream (type 0x00) whose first frame is SETTINGS, then a request on a
        // bidirectional stream. The control stream is consumed and the request
        // is still surfaced.
        TestTransportConnectionContext control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x01, 0), (0x06, 8192)),
            isBidirectional: false);
        TestTransportConnectionContext request = new(
            HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/c", "https", "a"));
        TestMultiplexTransportConnection connection = new(new[] { control, request }, TransportProtocol.Quic);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http30, new TestServerTransport(TransportProtocol.Quic, new TransportConnection[] { connection }), isSecure: true);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Path.Value.ShouldBe("/c");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http3: Should accept a QPACK encoder stream and still process the request")]
    public async Task Http3_OnQPackStreamThenRequest_ShouldProcessRequest()
    {
        TestTransportConnectionContext qpack = new(
            HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(0x02 /* QPACK encoder */),
            isBidirectional: false);
        TestTransportConnectionContext request = new(
            HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/q", "https", "a"));
        TestMultiplexTransportConnection connection = new(new[] { qpack, request }, TransportProtocol.Quic);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http30, new TestServerTransport(TransportProtocol.Quic, new TransportConnection[] { connection }), isSecure: true);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Request.Path.Value.ShouldBe("/q");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http3: Should terminate on a duplicate control stream")]
    public async Task Http3_OnDuplicateControlStream_ShouldTerminate()
    {
        // RFC 9114 §6.2.1 — only one control stream per peer; a second is
        // H3_STREAM_CREATION_ERROR, a connection error.
        TestTransportConnectionContext first = new(HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x06, 8192)), isBidirectional: false);
        TestTransportConnectionContext second = new(HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x06, 8192)), isBidirectional: false);
        TestMultiplexTransportConnection connection = new(new[] { first, second }, TransportProtocol.Quic);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http30, new TestServerTransport(TransportProtocol.Quic, new TransportConnection[] { connection }), isSecure: true);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http3: Should terminate when the control stream's first frame is not SETTINGS")]
    public async Task Http3_OnControlStreamMissingSettings_ShouldTerminate()
    {
        // RFC 9114 §6.2.1 — the first control frame MUST be SETTINGS. A control
        // stream (type 0x00) whose first frame is DATA (0x00, length 0) is
        // H3_MISSING_SETTINGS.
        TestTransportConnectionContext badControl = new(
            HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(0x00, new byte[] { 0x00, 0x00 }),
            isBidirectional: false);
        TestMultiplexTransportConnection connection = new(new[] { badControl }, TransportProtocol.Quic);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http30, new TestServerTransport(TransportProtocol.Quic, new TransportConnection[] { connection }), isSecure: true);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http3: Should terminate on a client-created push stream")]
    public async Task Http3_OnClientPushStream_ShouldTerminate()
    {
        // RFC 9114 §6.2.2 — a client MUST NOT create a push stream (type 0x01);
        // it is H3_STREAM_CREATION_ERROR.
        TestTransportConnectionContext push = new(
            HttpProtocolPayloadFactory.CreateHttp3UnidirectionalStream(0x01 /* push */),
            isBidirectional: false);
        TestMultiplexTransportConnection connection = new(new[] { push }, TransportProtocol.Quic);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http30, new TestServerTransport(TransportProtocol.Quic, new TransportConnection[] { connection }), isSecure: true);

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
