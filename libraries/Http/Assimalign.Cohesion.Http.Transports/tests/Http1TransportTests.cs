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
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET http://api.test/widgets?id=42 HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert
        httpContext.Request.Path.Value.ShouldBe("/widgets");
        httpContext.Request.Query["id"].Value.ShouldBe("42");
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
