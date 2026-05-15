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

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should deliver Content-Length framed body intact")]
    public async Task Http1_OnContentLengthBody_ShouldDeliverBodyIntact()
    {
        // RFC 9112 §6.2 — Content-Length is the second framing strategy.
        const string bodyText = "hello world";
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            $"POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: {bodyText.Length}\r\n\r\n{bodyText}");
        IHttpContext httpContext = await ReceiveFirstContextAsync(payload);

        using StreamReader reader = new(httpContext.Request.Body);
        string body = await reader.ReadToEndAsync();
        body.ShouldBe(bodyText);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should accept repeated Content-Length headers with the same value")]
    public async Task Http1_OnRepeatedContentLengthSameValue_ShouldAcceptBody()
    {
        // RFC 9112 §6.3 — repeated Content-Length values are only rejected when they differ.
        // Identical repeats may be folded into a single header by an intermediary, so a
        // standards-compliant server treats them as one declaration.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\nhello");
        IHttpContext httpContext = await ReceiveFirstContextAsync(payload);

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reject inconsistent Content-Length values")]
    public async Task Http1_OnConflictingContentLength_ShouldThrow()
    {
        // RFC 9112 §6.3 — conflicting Content-Length values are a smuggling vector and MUST
        // be rejected with a Bad Request response.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\nContent-Length: 7\r\n\r\nhello!!");

        await Should.ThrowAsync<InvalidDataException>(async () => await ReceiveFirstContextAsync(payload));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reject coexisting Content-Length and Transfer-Encoding")]
    public async Task Http1_OnContentLengthAndTransferEncoding_ShouldThrow()
    {
        // RFC 9112 §6.3 — the canonical request-smuggling vector. A message that declares
        // both framing strategies is ambiguous and MUST be rejected.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");

        await Should.ThrowAsync<InvalidDataException>(async () => await ReceiveFirstContextAsync(payload));
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reject malformed Content-Length values")]
    [InlineData("abc")]    // non-digit
    [InlineData("-5")]     // negative
    [InlineData("+5")]     // explicit sign
    [InlineData("5.0")]    // decimal point
    [InlineData("0x5")]    // hex
    [InlineData("5 5")]    // embedded whitespace inside the value
    [InlineData("")]       // empty
    public async Task Http1_OnMalformedContentLength_ShouldThrow(string contentLength)
    {
        // RFC 9112 §6.3 — Content-Length MUST be one or more ASCII digits with no leading
        // sign, decimal point, hex digits, or embedded whitespace.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            $"POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: {contentLength}\r\n\r\n");

        await Should.ThrowAsync<InvalidDataException>(async () => await ReceiveFirstContextAsync(payload));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reject non-chunked Transfer-Encoding")]
    public async Task Http1_OnNonChunkedTransferEncoding_ShouldThrow()
    {
        // RFC 9112 §7.4 — the final coding of Transfer-Encoding MUST be 'chunked' for an
        // HTTP/1.1 request. gzip / deflate / compress are content codings and don't belong
        // here; an unrecognized transfer coding is a hard failure.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: gzip\r\n\r\n");

        await Should.ThrowAsync<InvalidDataException>(async () => await ReceiveFirstContextAsync(payload));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reassemble chunked body across multiple chunks")]
    public async Task Http1_OnChunkedBody_ShouldReassembleBody()
    {
        // RFC 9112 §7.1 — chunked transfer coding. Chunks are concatenated in order to
        // reconstitute the body the peer intended to send.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5\r\nhello\r\n"
            + "7\r\n, world\r\n"
            + "0\r\n\r\n");
        IHttpContext httpContext = await ReceiveFirstContextAsync(payload);

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello, world");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should strip chunk extensions from the assembled body")]
    public async Task Http1_OnChunkedBodyWithExtensions_ShouldStripExtensions()
    {
        // RFC 9112 §7.1.1 — chunk-ext is optional and a recipient MAY ignore it. We strip
        // and never surface it; the assembled body must contain only the chunk-data octets.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5;name=value;flag\r\nhello\r\n"
            + "0\r\n\r\n");
        IHttpContext httpContext = await ReceiveFirstContextAsync(payload);

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should consume trailers without leaking into headers")]
    public async Task Http1_OnChunkedBodyWithTrailers_ShouldNotLeakIntoHeaders()
    {
        // RFC 9112 §7.1.2 — trailers follow the last chunk and are terminated by an empty
        // line. They MUST NOT appear in the field section delivered to the application as
        // request headers. Surfacing trailers on IHttpRequest is part of the field-section
        // work; for now the transport must at least consume and discard them cleanly.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5\r\nhello\r\n"
            + "0\r\nX-Trace-Id: trace-42\r\n\r\n");
        IHttpContext httpContext = await ReceiveFirstContextAsync(payload);

        using StreamReader reader = new(httpContext.Request.Body);
        (await reader.ReadToEndAsync()).ShouldBe("hello");
        httpContext.Request.Headers.ContainsKey(new HttpHeaderKey("X-Trace-Id")).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reject trailers that contain framing-related headers")]
    public async Task Http1_OnChunkedBodyWithForbiddenTrailer_ShouldThrow()
    {
        // RFC 9112 §7.1.2 — Content-Length, Transfer-Encoding, and Host are forbidden as
        // trailer fields. Letting them through would be a smuggling vector.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5\r\nhello\r\n"
            + "0\r\nContent-Length: 5\r\n\r\n");

        await Should.ThrowAsync<InvalidDataException>(async () => await ReceiveFirstContextAsync(payload));
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reject malformed chunk sizes")]
    [InlineData("xyz")]    // non-hex
    [InlineData("-1")]     // signed
    [InlineData(" 5")]     // leading whitespace
    [InlineData("")]       // empty
    public async Task Http1_OnMalformedChunkSize_ShouldThrow(string chunkSize)
    {
        // RFC 9112 §7.1 — chunk-size is 1*HEXDIG with no leading sign and no whitespace.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + $"{chunkSize}\r\n");

        await Should.ThrowAsync<InvalidDataException>(async () => await ReceiveFirstContextAsync(payload));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reject a chunked body that ends before the terminating zero chunk")]
    public async Task Http1_OnChunkedBodyTruncatedBeforeFinalChunk_ShouldThrow()
    {
        // RFC 9112 §7.1 — a chunked body MUST end with a zero chunk. Truncation before the
        // terminator is a half-message; the parser must surface that rather than treating
        // it as success.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5\r\nhello\r\n");

        await Should.ThrowAsync<EndOfStreamException>(async () => await ReceiveFirstContextAsync(payload));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reject a chunk that ends before its declared size is reached")]
    public async Task Http1_OnChunkTruncatedMidChunk_ShouldThrow()
    {
        // RFC 9112 §7.1 — chunk-data must contain chunk-size octets followed by CRLF.
        // Truncating inside the data is an EOF, not a successful body.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "10\r\nshort");

        await Should.ThrowAsync<EndOfStreamException>(async () => await ReceiveFirstContextAsync(payload));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - Http1: Should reject Content-Length declarations exceeding the body size cap")]
    public async Task Http1_OnContentLengthExceedingCap_ShouldThrow()
    {
        // DoS guard — a peer that claims Content-Length: 10 GB would otherwise force a
        // 10 GB allocation. Cohesion caps the body at 100 MB and rejects oversize
        // declarations before reading a single byte from the stream.
        const long oversize = 200L * 1024 * 1024;
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            $"POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: {oversize}\r\n\r\n");

        await Should.ThrowAsync<InvalidDataException>(async () => await ReceiveFirstContextAsync(payload));
    }

    private static async Task<IHttpContext> ReceiveFirstContextAsync(byte[] payload)
    {
        TestTransportConnectionContext transportContext = new(payload);
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new ITransportConnection[] { connection }));

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        return await ReadSingleContextAsync(httpConnectionContext);
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }
}
