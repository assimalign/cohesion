using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http.Tests.TestObjects;

using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http.Tests;

public sealed class HttpForwarderTests
{
    private const string webSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private static readonly Uri _destination = new("http://backend.test:8080/api/items?page=2");

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - Create: Should reject a null invoker")]
    public void Create_NullInvoker_ShouldThrow()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentNullException>(() => HttpForwarder.Create(null!));
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should reject a destination that is not an absolute HTTP URI")]
    [InlineData("relative/items")]
    [InlineData("ftp://backend.test/items")]
    public async Task ForwardAsync_InvalidDestination_ShouldThrow(string destination)
    {
        // Arrange
        using HttpMessageInvoker invoker = new(StubHttpMessageHandler.Respond(_ => Ok()));
        HttpForwarder forwarder = HttpForwarder.Create(invoker);

        // Act / Assert
        await Should.ThrowAsync<ArgumentException>(
            () => forwarder.ForwardAsync(new TestHttpContext(), new Uri(destination, UriKind.RelativeOrAbsolute), CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should relay the destination's status, fields and body")]
    public async Task ForwardAsync_BufferedResponse_ShouldRelayStatusFieldsAndBody()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = Ok("created", NetHttpStatusCode.Created);
            response.Headers.TryAddWithoutValidation("X-Upstream", "yes");
            response.Headers.Location = new Uri("http://backend.test:8080/api/items/7");
            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Request.Path = "/api/items";
        context.Request.Headers["Accept"] = "application/json";

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        ((int)context.Response.StatusCode).ShouldBe(201);
        context.ReadResponseBody().ShouldBe("created");
        Header(context.Response.Headers, "X-Upstream").ShouldBe("yes");
        Header(context.Response.Headers, "Content-Type").ShouldBe("text/plain; charset=utf-8");
        Header(context.Response.Headers, "Location").ShouldBe("http://backend.test:8080/api/items/7");
        context.Response.Headers.ContainsKey("Via").ShouldBeFalse();
        HttpRequestMessage forwarded = handler.Request.ShouldNotBeNull();
        forwarded.Method.ShouldBe(NetHttpMethod.Get);
        forwarded.RequestUri.ShouldBe(_destination);
        forwarded.Version.ShouldBe(NetHttpVersion.Version20);
        forwarded.VersionPolicy.ShouldBe(HttpVersionPolicy.RequestVersionOrLower);
        Values(forwarded, "Accept").ShouldBe(["application/json"]);
        forwarded.Content.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should drop the fields that belong to one connection")]
    public async Task ForwardAsync_HopByHopFields_ShouldNotTravel()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = Ok("ok");
            response.Headers.TryAddWithoutValidation("Connection", "close, X-Hop");
            response.Headers.TryAddWithoutValidation("X-Hop", "1");
            response.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
            response.Headers.TryAddWithoutValidation("Alt-Svc", "h3=\":443\"");
            response.Headers.TryAddWithoutValidation("Proxy-Authenticate", "Basic realm=\"proxy\"");
            response.Headers.TryAddWithoutValidation("X-Keep", "1");
            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Request.Headers["Connection"] = "keep-alive, X-Secret";
        context.Request.Headers["Keep-Alive"] = "timeout=5";
        context.Request.Headers["X-Secret"] = "1";
        context.Request.Headers["Proxy-Authorization"] = "Basic cHJveHk6c2VjcmV0";
        context.Request.Headers["Proxy-Connection"] = "keep-alive";
        context.Request.Headers["Upgrade"] = "h2c";
        context.Request.Headers["HTTP2-Settings"] = "AAMAAABkAAQCAAAAAAIAAAAA";
        context.Request.Headers["TE"] = "trailers, deflate;q=0.5";
        context.Request.Headers["X-Keep"] = "1";

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        HttpRequestMessage forwarded = handler.Request.ShouldNotBeNull();
        foreach (string name in new[] { "Connection", "Keep-Alive", "X-Secret", "Proxy-Authorization", "Proxy-Connection", "Upgrade", "HTTP2-Settings" })
        {
            forwarded.Headers.Contains(name).ShouldBeFalse(name);
        }

        Values(forwarded, "TE").ShouldBe(["trailers"]);
        Values(forwarded, "X-Keep").ShouldBe(["1"]);

        foreach (string name in new[] { "Connection", "X-Hop", "Keep-Alive", "Alt-Svc", "Proxy-Authenticate" })
        {
            context.Response.Headers.ContainsKey(name).ShouldBeFalse(name);
        }

        Header(context.Response.Headers, "X-Keep").ShouldBe("1");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should not ask for trailers the client did not accept")]
    public async Task ForwardAsync_TeWithoutTrailers_ShouldSendNoTe()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Request.Headers["TE"] = "gzip";

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        handler.Request.ShouldNotBeNull().Headers.Contains("TE").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Set should replace the client's forwarded fields with this hop's view")]
    public async Task ForwardAsync_SetForwardedHeaders_ShouldReplaceClientValues()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.1";
        context.Request.Headers["X-Forwarded-Host"] = "spoofed.example";
        context.Request.Headers["Forwarded"] = "for=192.0.2.60";

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        HttpRequestMessage forwarded = handler.Request.ShouldNotBeNull();
        Values(forwarded, "X-Forwarded-For").ShouldBe(["203.0.113.7"]);
        Values(forwarded, "X-Forwarded-Proto").ShouldBe(["http"]);
        Values(forwarded, "X-Forwarded-Host").ShouldBe(["client.example"]);
        forwarded.Headers.Contains("Forwarded").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Set should describe the client a trusted proxy reported")]
    public async Task ForwardAsync_SetWithForwardedFeature_ShouldWriteEffectiveClient()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Features.Set(new TestForwardedFeature
        {
            RemoteIp = IPAddress.Parse("192.0.2.10"),
            RemotePort = 443,
            Scheme = HttpScheme.Https,
            Host = new HttpHost("public.example"),
        });

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        HttpRequestMessage forwarded = handler.Request.ShouldNotBeNull();
        Values(forwarded, "X-Forwarded-For").ShouldBe(["192.0.2.10"]);
        Values(forwarded, "X-Forwarded-Proto").ShouldBe(["https"]);
        Values(forwarded, "X-Forwarded-Host").ShouldBe(["public.example"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Append should add this hop to the client's forwarded fields")]
    public async Task ForwardAsync_AppendForwardedHeaders_ShouldAddThisHop()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions { ForwardedHeaderAction = HttpForwardedHeaderAction.Append });
        TestHttpContext context = new();
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.1";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "public.example";
        context.Request.Headers["Forwarded"] = "for=192.0.2.60";

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        HttpRequestMessage forwarded = handler.Request.ShouldNotBeNull();
        Values(forwarded, "X-Forwarded-For").ShouldBe(["198.51.100.1", "203.0.113.7"]);
        Values(forwarded, "X-Forwarded-Proto").ShouldBe(["https", "http"]);
        Values(forwarded, "X-Forwarded-Host").ShouldBe(["public.example", "client.example"]);
        Values(forwarded, "Forwarded").ShouldBe(["for=192.0.2.60"]);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: The Forwarded format should write one RFC 7239 element")]
    [InlineData(HttpForwardedHeaderAction.Set, "203.0.113.7", new[] { "for=203.0.113.7;host=client.example;proto=http" })]
    [InlineData(HttpForwardedHeaderAction.Set, "2001:db8::1", new[] { "for=\"[2001:db8::1]\";host=client.example;proto=http" })]
    [InlineData(HttpForwardedHeaderAction.Append, "203.0.113.7", new[] { "for=192.0.2.60", "for=203.0.113.7;host=client.example;proto=http" })]
    public async Task ForwardAsync_ForwardedFormat_ShouldWriteElement(HttpForwardedHeaderAction action, string remoteAddress, string[] expected)
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions
        {
            ForwardedHeaderAction = action,
            ForwardedHeaderFormat = HttpForwardedHeaderFormat.Forwarded,
        });
        TestHttpContext context = new(remoteAddress: IPAddress.Parse(remoteAddress));
        context.Request.Headers["Forwarded"] = "for=192.0.2.60";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.1";

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        HttpRequestMessage forwarded = handler.Request.ShouldNotBeNull();
        Values(forwarded, "Forwarded").ShouldBe(expected);

        // Set replaces both families; Append leaves the other family as the client sent it.
        if (action == HttpForwardedHeaderAction.Set)
        {
            forwarded.Headers.Contains("X-Forwarded-For").ShouldBeFalse();
        }
        else
        {
            Values(forwarded, "X-Forwarded-For").ShouldBe(["198.51.100.1"]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should report an IPv4-mapped client as IPv4")]
    public async Task ForwardAsync_MappedClientAddress_ShouldWriteIPv4()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new(remoteAddress: IPAddress.Parse("::ffff:203.0.113.9"));

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        Values(handler.Request.ShouldNotBeNull(), "X-Forwarded-For").ShouldBe(["203.0.113.9"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: None should pass the client's forwarded fields through")]
    public async Task ForwardAsync_ForwardedHeaderActionNone_ShouldPassClientValues()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions { ForwardedHeaderAction = HttpForwardedHeaderAction.None });
        TestHttpContext context = new();
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.1";

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        HttpRequestMessage forwarded = handler.Request.ShouldNotBeNull();
        Values(forwarded, "X-Forwarded-For").ShouldBe(["198.51.100.1"]);
        forwarded.Headers.Contains("X-Forwarded-Proto").ShouldBeFalse();
        forwarded.Headers.Contains("X-Forwarded-Host").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should add its Via entry after the client's")]
    public async Task ForwardAsync_Via_ShouldAppendPseudonym()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = Ok();
            response.Headers.TryAddWithoutValidation("Via", "1.1 origin-cache");
            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions { ViaPseudonym = "edge-1", AddViaToResponses = true });
        TestHttpContext context = new(HttpVersion.Http20);
        context.Request.Headers["Via"] = "1.1 corporate-proxy";

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        Values(handler.Request.ShouldNotBeNull(), "Via").ShouldBe(["1.1 corporate-proxy", "2 edge-1"]);
        context.Response.Headers.TryGetValue("Via", out HttpHeaderValue via).ShouldBeTrue();
        via.ToArray().ShouldBe(["1.1 origin-cache", "1.1 edge-1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should add no Via when the pseudonym is null")]
    public async Task ForwardAsync_NullPseudonym_ShouldAddNoVia()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions { ViaPseudonym = null, AddViaToResponses = true });
        TestHttpContext context = new();
        context.Request.Headers["Via"] = "1.1 corporate-proxy";

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        Values(handler.Request.ShouldNotBeNull(), "Via").ShouldBe(["1.1 corporate-proxy"]);
        context.Response.Headers.ContainsKey("Via").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer 508 when Via already names this forwarder")]
    public async Task ForwardAsync_ViaNamesPseudonym_ShouldAnswerLoopDetected()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions { ViaPseudonym = "edge-1" });
        TestHttpContext context = new();
        context.Request.Headers["Via"] = "1.0 corporate-proxy, 1.1 EDGE-1 (Cohesion)";

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.LoopDetected);
        ((int)context.Response.StatusCode).ShouldBe(508);
        handler.Request.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should keep the client's Host only when preserving it")]
    [InlineData(true, "client.example")]
    [InlineData(false, null)]
    public async Task ForwardAsync_PreserveHost_ShouldChooseHost(bool preserveHost, string? expected)
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions { PreserveHost = preserveHost });
        TestHttpContext context = new();
        context.Request.Headers["Host"] = "client.example";

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        handler.Request.ShouldNotBeNull().Headers.Host.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should stream the request body with its content fields")]
    public async Task ForwardAsync_RequestBody_ShouldStreamWithContentFields()
    {
        // Arrange
        long? contentLength = null;
        string? contentType = null;
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(request =>
        {
            contentLength = request.Content?.Headers.ContentLength;
            contentType = request.Content?.Headers.ContentType?.MediaType;
            return Ok();
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Request.Method = HttpMethod.Post;
        context.Request.Headers["Content-Length"] = "11";
        context.Request.Headers["Content-Type"] = "text/plain";
        context.Request.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello world"));

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        handler.Request.ShouldNotBeNull().Method.ShouldBe(NetHttpMethod.Post);
        Encoding.ASCII.GetString(handler.RequestBody.ShouldNotBeNull()).ShouldBe("hello world");
        contentLength.ShouldBe(11);
        contentType.ShouldBe("text/plain");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should forward an HTTP/2 body that has no length")]
    public async Task ForwardAsync_Http2BodyWithoutLength_ShouldForwardBody()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new(HttpVersion.Http20);
        context.Request.Method = HttpMethod.Put;
        context.Request.Body = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        Encoding.ASCII.GetString(handler.RequestBody.ShouldNotBeNull()).ShouldBe("abc");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should send no body for an HTTP/1.1 request without framing")]
    public async Task ForwardAsync_Http11WithoutFraming_ShouldSendNoBody()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Request.Method = HttpMethod.Post;
        context.Request.Body = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        handler.Request.ShouldNotBeNull().Content.ShouldBeNull();
        handler.RequestBody.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should join HTTP/2 cookie crumbs into one field")]
    public async Task ForwardAsync_Http2Cookies_ShouldJoinCrumbs()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new(HttpVersion.Http20);
        context.Request.Headers["Cookie"] = new[] { "a=1", "b=2" };

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        Values(handler.Request.ShouldNotBeNull(), "Cookie").ShouldBe(["a=1; b=2"]);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer OPTIONS and TRACE itself when Max-Forwards is zero")]
    [InlineData("OPTIONS", 200)]
    [InlineData("TRACE", 501)]
    public async Task ForwardAsync_MaxForwardsZero_ShouldAnswerByProxy(string method, int expectedStatus)
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Request.Method = new HttpMethod(method);
        context.Request.Headers["Max-Forwards"] = "0";
        context.Request.Headers["Authorization"] = "Bearer secret";

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.AnsweredByProxy);
        ((int)context.Response.StatusCode).ShouldBe(expectedStatus);
        context.ReadResponseBody().ShouldBeEmpty();
        handler.Request.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should decrement Max-Forwards on OPTIONS")]
    public async Task ForwardAsync_MaxForwardsPositive_ShouldDecrement()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Request.Method = HttpMethod.Options;
        context.Request.Headers["Max-Forwards"] = "3";

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        Values(handler.Request.ShouldNotBeNull(), "Max-Forwards").ShouldBe(["2"]);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer a failed destination with 502")]
    [InlineData(HttpRequestError.ConnectionError, HttpProxyResult.DestinationUnreachable)]
    [InlineData(HttpRequestError.NameResolutionError, HttpProxyResult.DestinationUnreachable)]
    [InlineData(HttpRequestError.SecureConnectionError, HttpProxyResult.DestinationUnreachable)]
    [InlineData(HttpRequestError.ResponseEnded, HttpProxyResult.DestinationFailed)]
    [InlineData(HttpRequestError.InvalidResponse, HttpProxyResult.DestinationFailed)]
    public async Task ForwardAsync_DestinationFails_ShouldAnswerBadGateway(HttpRequestError error, HttpProxyResult expected)
    {
        // Arrange
        StubHttpMessageHandler handler = new((_, _) => throw new HttpRequestException(error, "The destination failed."));
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(expected);
        ((int)context.Response.StatusCode).ShouldBe(502);
        context.IsCanceled.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer a refused connection with 502")]
    public async Task ForwardAsync_ConnectionRefused_ShouldAnswerBadGateway()
    {
        // Arrange
        using HttpMessageInvoker invoker = CreateSocketsInvoker();
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, new Uri($"http://127.0.0.1:{Loopback.GetUnusedPort()}/"), timeout.Token);

        // Assert
        result.ShouldBe(HttpProxyResult.DestinationUnreachable);
        ((int)context.Response.StatusCode).ShouldBe(502);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer a silent destination with 504")]
    public async Task ForwardAsync_DestinationSilent_ShouldAnswerGatewayTimeout()
    {
        // Arrange
        StubHttpMessageHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Ok();
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions { ActivityTimeout = TimeSpan.FromMilliseconds(100) });
        TestHttpContext context = new();

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.DestinationTimedOut);
        ((int)context.Response.StatusCode).ShouldBe(504);
        context.IsCanceled.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should abort the exchange when the caller or the client gives up")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForwardAsync_Canceled_ShouldAbortExchange(bool byCaller)
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        StubHttpMessageHandler handler = new(async (_, cancellationToken) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Ok();
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new(requestCancelled: byCaller ? CancellationToken.None : cancellation.Token);

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, byCaller ? cancellation.Token : CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.Canceled);
        context.IsCanceled.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer 502 when a buffered body exceeds the limit")]
    [InlineData(5, HttpProxyResult.Completed)]
    [InlineData(4, HttpProxyResult.ResponseTooLarge)]
    public async Task ForwardAsync_BufferLimit_ShouldRefuseLargerBody(long limit, HttpProxyResult expected)
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = Ok("hello");
            response.Headers.TryAddWithoutValidation("X-Upstream", "yes");
            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions { MaxBufferedResponseBodySize = limit });
        TestHttpContext context = new();

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(expected);

        if (expected == HttpProxyResult.Completed)
        {
            context.ReadResponseBody().ShouldBe("hello");
            Header(context.Response.Headers, "X-Upstream").ShouldBe("yes");
        }
        else
        {
            ((int)context.Response.StatusCode).ShouldBe(502);
            context.ReadResponseBody().ShouldBeEmpty();
            context.Response.Headers.ContainsKey("X-Upstream").ShouldBeFalse();
            context.Response.Headers.ContainsKey("Content-Type").ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should stream the response through the streaming feature")]
    public async Task ForwardAsync_StreamingFeature_ShouldWriteFlushAndComplete()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = Ok("hello", NetHttpStatusCode.Accepted);
            response.Headers.TryAddWithoutValidation("X-Upstream", "yes");
            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions { MaxBufferedResponseBodySize = 0 });
        TestHttpContext context = new();
        TestResponseStreamingFeature streaming = new(context);
        context.Features.Set(streaming);

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        streaming.StartedStatus.ShouldBe(202);
        streaming.StartedHeaders["X-Upstream"].ShouldBe("yes");
        Encoding.UTF8.GetString(streaming.Body.ToArray()).ShouldBe("hello");
        streaming.Flushes.ShouldBeGreaterThanOrEqualTo(1);
        streaming.Completed.ShouldBeTrue();
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should leave an empty streamed response to the server")]
    public async Task ForwardAsync_StreamingEmptyBody_ShouldNotStart()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => new HttpResponseMessage(NetHttpStatusCode.NoContent));
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        TestResponseStreamingFeature streaming = new(context);
        context.Features.Set(streaming);

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        ((int)context.Response.StatusCode).ShouldBe(204);
        streaming.HasStarted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should abort a streamed response whose body breaks off")]
    public async Task ForwardAsync_StreamedBodyFailsAfterStart_ShouldAbort()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => new HttpResponseMessage(NetHttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingReadStream(Encoding.ASCII.GetBytes("hel"))),
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        TestResponseStreamingFeature streaming = new(context);
        context.Features.Set(streaming);

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.ResponseBodyFailed);
        context.IsCanceled.ShouldBeTrue();
        Encoding.ASCII.GetString(streaming.Body.ToArray()).ShouldBe("hel");
        streaming.Completed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer 502 for a streamed body that fails before any byte")]
    public async Task ForwardAsync_StreamedBodyFailsBeforeStart_ShouldAnswerBadGateway()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = new(NetHttpStatusCode.OK)
            {
                Content = new StreamContent(new FailingReadStream([])),
            };
            response.Headers.TryAddWithoutValidation("X-Upstream", "yes");
            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        TestResponseStreamingFeature streaming = new(context);
        context.Features.Set(streaming);

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.ResponseBodyFailed);
        ((int)context.Response.StatusCode).ShouldBe(502);
        context.Response.Headers.ContainsKey("X-Upstream").ShouldBeFalse();
        context.IsCanceled.ShouldBeFalse();
        streaming.HasStarted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer 502 for a buffered body that breaks off")]
    public async Task ForwardAsync_BufferedBodyFails_ShouldAnswerBadGateway()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = new(NetHttpStatusCode.OK)
            {
                Content = new StreamContent(new FailingReadStream(Encoding.ASCII.GetBytes("hel"))),
            };
            response.Headers.TryAddWithoutValidation("X-Upstream", "yes");
            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.ResponseBodyFailed);
        ((int)context.Response.StatusCode).ShouldBe(502);
        context.ReadResponseBody().ShouldBeEmpty();
        context.Response.Headers.ContainsKey("X-Upstream").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer 400 when the client's body breaks off")]
    public async Task ForwardAsync_RequestBodyFails_ShouldAnswerBadRequest()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Request.Method = HttpMethod.Post;
        context.Request.Headers["Content-Length"] = "10";
        context.Request.Body = new FailingReadStream(Encoding.ASCII.GetBytes("abc"));

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.RequestBodyFailed);
        ((int)context.Response.StatusCode).ShouldBe(400);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should copy the destination's trailers where the client accepts them")]
    public async Task ForwardAsync_Trailers_ShouldCopyAllowedFields()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = Ok("x");
            response.Headers.TryAddWithoutValidation("Trailer", "grpc-status");
            response.TrailingHeaders.TryAddWithoutValidation("grpc-status", "0");
            response.TrailingHeaders.TryAddWithoutValidation("Set-Cookie", "session=1");
            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new(HttpVersion.Http20);

        // Act
        await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        Header(context.Response.Headers, "Trailer").ShouldBe("grpc-status");
        context.Response.Trailers.TryGetValue("grpc-status", out HttpHeaderValue status).ShouldBeTrue();
        ((string)status).ShouldBe("0");
        context.Response.Trailers.TryGetValue("Set-Cookie", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should not announce trailers a client cannot receive")]
    public async Task ForwardAsync_TrailersUnsupported_ShouldDropTrailerField()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = Ok("x");
            response.Headers.TryAddWithoutValidation("Trailer", "grpc-status");
            response.TrailingHeaders.TryAddWithoutValidation("grpc-status", "0");
            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        context.Response.Headers.ContainsKey("Trailer").ShouldBeFalse();
        context.Response.Trailers.IsSupported.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer CONNECT with 501")]
    public async Task ForwardAsync_ConnectMethod_ShouldAnswerNotImplemented()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        context.Request.Method = HttpMethod.Connect;

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.NotSupported);
        ((int)context.Response.StatusCode).ShouldBe(501);
        handler.Request.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer an extended CONNECT for another protocol with 501")]
    public async Task ForwardAsync_ExtendedConnectOtherProtocol_ShouldAnswerNotImplemented()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new(HttpVersion.Http20);
        context.Request.Method = HttpMethod.Connect;
        TestExtendedConnectFeature extendedConnect = new(context, "connect-udp", Stream.Null);
        context.Features.Set(extendedConnect);

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.NotSupported);
        ((int)context.Response.StatusCode).ShouldBe(501);
        extendedConnect.Accepted.ShouldBeFalse();
        handler.Request.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should relay an accepted HTTP/1.1 upgrade both ways")]
    public async Task ForwardAsync_UpgradeAccepted_ShouldRelayBothWays()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using LoopbackServer upstream = Loopback.Serve(async (head, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: echo\r\nX-Upstream: yes\r\n\r\n"), timeout.Token);
            string ping = await Loopback.ReadTextAsync(stream, 4, timeout.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(ping == "ping" ? "pong" : "fail"), timeout.Token);
        });
        (NetworkStream server, NetworkStream client) = await Loopback.CreateSocketPairAsync();
        await using NetworkStream clientEnd = client;
        TestHttpContext context = new();
        context.Request.Headers["Connection"] = "Upgrade";
        context.Request.Headers["Upgrade"] = "echo";
        TestProtocolUpgrade upgrade = new(context, HttpProtocolUpgradeKind.Upgrade, "echo", server);
        context.Features.Set(upgrade);
        using HttpMessageInvoker invoker = CreateSocketsInvoker();
        HttpForwarder forwarder = HttpForwarder.Create(invoker);

        // Act
        Task<HttpProxyResult> forwarding = forwarder.ForwardAsync(context, upstream.Uri, timeout.Token);
        await clientEnd.WriteAsync(Encoding.ASCII.GetBytes("ping"), timeout.Token);
        string reply = await Loopback.ReadTextAsync(clientEnd, 4, timeout.Token);
        HttpProxyResult result = await forwarding;
        int trailing = await clientEnd.ReadAsync(new byte[1], timeout.Token);

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        reply.ShouldBe("pong");
        trailing.ShouldBe(0);
        upgrade.Accepted.ShouldBeTrue();
        upgrade.AcceptedHeaders["X-Upstream"].ShouldBe("yes");
        upgrade.AcceptedHeaders.ContainsKey("Upgrade").ShouldBeFalse();
        upgrade.AcceptedHeaders.ContainsKey("Connection").ShouldBeFalse();
        string head = upstream.RequestHead.ShouldNotBeNull();
        head.ShouldStartWith("GET / HTTP/1.1\r\n", caseSensitivity: Case.Sensitive);
        GetHeader(head, "Upgrade").ShouldBe("echo");
        GetHeader(head, "Connection").ShouldBe("Upgrade");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should relay an ignored upgrade as an ordinary response")]
    public async Task ForwardAsync_UpgradeIgnored_ShouldRelayResponse()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok("plain"));
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        TestProtocolUpgrade upgrade = new(context, HttpProtocolUpgradeKind.Upgrade, "echo", Stream.Null);
        context.Features.Set(upgrade);

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        context.ReadResponseBody().ShouldBe("plain");
        upgrade.Accepted.ShouldBeFalse();
        HttpRequestMessage forwarded = handler.Request.ShouldNotBeNull();
        forwarded.Version.ShouldBe(NetHttpVersion.Version11);
        forwarded.VersionPolicy.ShouldBe(HttpVersionPolicy.RequestVersionExact);
        Values(forwarded, "Connection").ShouldBe(["Upgrade"]);
        Values(forwarded, "Upgrade").ShouldBe(["echo"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer 502 when the destination switches to another protocol")]
    public async Task ForwardAsync_UpgradeToOtherProtocol_ShouldAnswerBadGateway()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = new(NetHttpStatusCode.SwitchingProtocols);
            response.Headers.TryAddWithoutValidation("Upgrade", "other");
            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();
        TestProtocolUpgrade upgrade = new(context, HttpProtocolUpgradeKind.Upgrade, "echo", Stream.Null);
        context.Features.Set(upgrade);

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.UpgradeFailed);
        ((int)context.Response.StatusCode).ShouldBe(502);
        upgrade.Accepted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer 502 when the destination switches protocols unasked")]
    public async Task ForwardAsync_UnrequestedSwitch_ShouldAnswerBadGateway()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => new HttpResponseMessage(NetHttpStatusCode.SwitchingProtocols));
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new();

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.UpgradeFailed);
        ((int)context.Response.StatusCode).ShouldBe(502);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should carry an HTTP/2 WebSocket to the destination as an HTTP/1.1 upgrade")]
    public async Task ForwardAsync_WebSocketBridge_ShouldVerifyAcceptAndRelay()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using LoopbackServer upstream = Loopback.Serve(async (head, stream) =>
        {
            string accept = ComputeAccept(GetHeader(head, "Sec-WebSocket-Key") ?? string.Empty);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n"
                + $"Sec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: chat\r\n\r\n"), timeout.Token);
            string ping = await Loopback.ReadTextAsync(stream, 4, timeout.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(ping == "ping" ? "pong" : "fail"), timeout.Token);
        });
        (NetworkStream server, NetworkStream client) = await Loopback.CreateSocketPairAsync();
        await using NetworkStream clientEnd = client;
        TestHttpContext context = new(HttpVersion.Http20);
        context.Request.Method = HttpMethod.Connect;
        context.Request.Headers["Sec-WebSocket-Version"] = "13";
        context.Request.Headers["Sec-WebSocket-Protocol"] = "chat";
        context.Request.Headers["Sec-WebSocket-Key"] = "c2VudC1ieS10aGUtY2xpZW50";
        TestExtendedConnectFeature extendedConnect = new(context, "websocket", server);
        context.Features.Set(extendedConnect);
        using HttpMessageInvoker invoker = CreateSocketsInvoker();
        HttpForwarder forwarder = HttpForwarder.Create(invoker);

        // Act
        Task<HttpProxyResult> forwarding = forwarder.ForwardAsync(context, upstream.Uri, timeout.Token);
        await clientEnd.WriteAsync(Encoding.ASCII.GetBytes("ping"), timeout.Token);
        string reply = await Loopback.ReadTextAsync(clientEnd, 4, timeout.Token);
        HttpProxyResult result = await forwarding;

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        reply.ShouldBe("pong");
        extendedConnect.Accepted.ShouldBeTrue();
        extendedConnect.AcceptedHeaders["Sec-WebSocket-Protocol"].ShouldBe("chat");
        extendedConnect.AcceptedHeaders.ContainsKey("Sec-WebSocket-Accept").ShouldBeFalse();
        extendedConnect.AcceptedHeaders.ContainsKey("Upgrade").ShouldBeFalse();
        string head = upstream.RequestHead.ShouldNotBeNull();
        head.ShouldStartWith("GET / HTTP/1.1\r\n", caseSensitivity: Case.Sensitive);
        GetHeader(head, "Upgrade").ShouldBe("websocket");
        GetHeader(head, "Sec-WebSocket-Version").ShouldBe("13");
        GetHeader(head, "Sec-WebSocket-Protocol").ShouldBe("chat");
        string key = GetHeader(head, "Sec-WebSocket-Key").ShouldNotBeNull();
        key.ShouldNotBe("c2VudC1ieS10aGUtY2xpZW50");
        Convert.FromBase64String(key).Length.ShouldBe(16);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - ForwardAsync: Should answer 502 when the destination does not complete the WebSocket handshake")]
    [InlineData(101, "bm90LXRoZS1hY2NlcHQtdmFsdWU=")]
    [InlineData(200, null)]
    public async Task ForwardAsync_WebSocketBridgeUnverified_ShouldAnswerBadGateway(int status, string? accept)
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ =>
        {
            HttpResponseMessage response = new((NetHttpStatusCode)status);
            if (accept is not null)
            {
                response.Headers.TryAddWithoutValidation("Upgrade", "websocket");
                response.Headers.TryAddWithoutValidation("Sec-WebSocket-Accept", accept);
            }

            return response;
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);
        TestHttpContext context = new(HttpVersion.Http20);
        context.Request.Method = HttpMethod.Connect;
        TestExtendedConnectFeature extendedConnect = new(context, "websocket", Stream.Null);
        context.Features.Set(extendedConnect);

        // Act
        HttpProxyResult result = await forwarder.ForwardAsync(context, _destination, CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.UpgradeFailed);
        ((int)context.Response.StatusCode).ShouldBe(502);
        extendedConnect.Accepted.ShouldBeFalse();
        HttpRequestMessage forwarded = handler.Request.ShouldNotBeNull();
        forwarded.Method.ShouldBe(NetHttpMethod.Get);
        Values(forwarded, "Sec-WebSocket-Version").ShouldBe(["13"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - Create: Should copy the options it was given")]
    public async Task Create_OptionsChangedAfterwards_ShouldKeepCopiedValues()
    {
        // Arrange
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => Ok());
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarderOptions options = new() { ViaPseudonym = "first" };
        HttpForwarder forwarder = HttpForwarder.Create(invoker, options);
        options.ViaPseudonym = "second";

        // Act
        await forwarder.ForwardAsync(new TestHttpContext(), _destination, CancellationToken.None);

        // Assert
        Values(handler.Request.ShouldNotBeNull(), "Via").ShouldBe(["1.1 first"]);
    }

    private static HttpResponseMessage Ok(string? body = null, NetHttpStatusCode status = NetHttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = body is null ? new ByteArrayContent([]) : new StringContent(body),
        };
    }

    private static HttpMessageInvoker CreateSocketsInvoker()
    {
        return new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ActivityHeadersPropagator = null,
        });
    }

    private static string[] Values(HttpRequestMessage request, string name)
    {
        return request.Headers.NonValidated.TryGetValues(name, out HeaderStringValues values) ? values.ToArray() : [];
    }

    private static string? Header(IHttpHeaderCollection headers, string name)
    {
        return headers.TryGetValue(name, out HttpHeaderValue value) ? (string)value : null;
    }

    private static string? GetHeader(string head, string name)
    {
        foreach (string line in head.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private static string ComputeAccept(string key)
    {
        return Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + webSocketGuid)));
    }
}
