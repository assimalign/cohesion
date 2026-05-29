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

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }
}
