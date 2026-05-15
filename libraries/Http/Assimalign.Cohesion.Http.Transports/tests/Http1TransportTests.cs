using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;
using Assimalign.Cohesion.Transports;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Transports.Tests;

public class Http1TransportTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should parse request metadata and serialize the response")]
    public async Task Http1_OnRequest_ShouldParseRequestMetadataAndSerializeResponse()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /widgets?id=42 HTTP/1.1\r\nHost: api.test\r\nCookie: session=abc; theme=light\r\nUser-Agent: tests\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnection httpConnection = await listener.AcceptOrListenAsync();
        IHttpConnectionContext httpConnectionContext = await httpConnection.OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert request
        httpContext.Version.ShouldBe(HttpVersion.Http11);
        httpContext.Request.Method.ShouldBe(HttpMethod.Get);
        httpContext.Request.Host.Value.ShouldBe("api.test");
        httpContext.Request.Path.Value.ShouldBe("/widgets");
        httpContext.Request.Query["id"].Value.ShouldBe("42");
        httpContext.Request.Cookies.Count.ShouldBe(2);

        // Act response
        httpContext.Response.StatusCode = HttpStatusCode.Created;
        httpContext.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
        httpContext.Response.Cookies.Add(new HttpCookie("trace", "123"));
        httpContext.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("created"));
        await httpConnectionContext.SendAsync(httpContext);

        string responseText = Encoding.ASCII.GetString(await transportContext.ReadOutputAsync());

        // Assert response
        responseText.ShouldContain("HTTP/1.1 201 Created");
        responseText.ShouldContain("Content-Type: text/plain");
        responseText.ShouldContain("Set-Cookie: trace=123; Path=/");
        responseText.ShouldContain("created");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should parse absolute-form request targets")]
    public async Task Http1_OnAbsoluteFormRequest_ShouldParsePathAndQuery()
    {
        // RFC 9112 §3.2.2 — absolute-form, used when a proxy forwards a request.
        // The authority component of the target supersedes any Host header.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET https://upstream.example.com:8443/widgets?id=42 HTTP/1.1\r\nHost: lying-host-header.test\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert
        httpContext.Request.Method.ShouldBe(HttpMethod.Get);
        httpContext.Request.Path.Value.ShouldBe("/widgets");
        httpContext.Request.Query["id"].Value.ShouldBe("42");
        // Authority component of the target wins over the Host header per RFC 9112 §3.2.2.
        httpContext.Request.Host.Value.ShouldBe("upstream.example.com:8443");
        httpContext.Request.Scheme.ShouldBe(HttpScheme.Https);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should parse authority-form CONNECT targets")]
    public async Task Http1_OnAuthorityFormConnectRequest_ShouldExtractHostAndPort()
    {
        // RFC 9112 §3.2.3 — authority-form, reserved for CONNECT.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "CONNECT origin.example.com:443 HTTP/1.1\r\nHost: origin.example.com:443\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert
        httpContext.Request.Method.ShouldBe(HttpMethod.Connect);
        httpContext.Request.Host.Value.ShouldBe("origin.example.com:443");
        httpContext.Request.Path.ShouldBe(HttpPath.Root);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should parse asterisk-form OPTIONS targets")]
    public async Task Http1_OnAsteriskFormOptionsRequest_ShouldExposeAsteriskPath()
    {
        // RFC 9112 §3.2.4 — asterisk-form, reserved for OPTIONS *.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "OPTIONS * HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert
        httpContext.Request.Method.ShouldBe(HttpMethod.Options);
        httpContext.Request.Path.Value.ShouldBe("*");
        // Asterisk-form has no authority on the target; Host header is the fallback.
        httpContext.Request.Host.Value.ShouldBe("api.test");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reject malformed request-targets")]
    [InlineData("CONNECT /path HTTP/1.1\r\nHost: api.test\r\n\r\n")]          // CONNECT requires authority-form
    [InlineData("GET * HTTP/1.1\r\nHost: api.test\r\n\r\n")]                  // asterisk-form is OPTIONS-only
    [InlineData("GET example.com:80 HTTP/1.1\r\nHost: api.test\r\n\r\n")]     // authority-form on non-CONNECT
    [InlineData("GET  HTTP/1.1\r\nHost: api.test\r\n\r\n")]                   // empty target (collapses to 2 parts after Split)
    [InlineData("GET ftp://example.com/p HTTP/1.1\r\nHost: api.test\r\n\r\n")] // unsupported scheme
    public async Task Http1_OnMalformedRequestTarget_ShouldThrow(string payloadText)
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(payloadText);
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await Should.ThrowAsync<InvalidDataException>(
            async () => await ReadSingleContextAsync(httpConnectionContext));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should deliver URL encoded request body intact to the application layer")]
    public async Task Http1_OnUrlEncodedPost_ShouldDeliverBodyIntact()
    {
        // Form parsing is an application-layer concern (Assimalign.Cohesion.Http.Forms);
        // the transport's job is to deliver the body bytes faithfully. This test verifies
        // the urlencoded payload reaches the application untouched.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /submit HTTP/1.1\r\nHost: api.test\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: 14\r\n\r\nname=alice&x=1");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert
        httpContext.Request.Method.ShouldBe(HttpMethod.Post);
        httpContext.Request.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/x-www-form-urlencoded");
        using System.IO.StreamReader reader = new(httpContext.Request.Body);
        string body = await reader.ReadToEndAsync();
        body.ShouldBe("name=alice&x=1");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should yield pipelined requests in order")]
    public async Task Http1_OnPipelinedRequests_ShouldYieldRequestsInOrder()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /one HTTP/1.1\r\nHost: api.test\r\n\r\nGET /two HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

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
