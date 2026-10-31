using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.HttpsPolicy.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.HttpsPolicy.Tests;

public class HttpsRedirectionTests
{
    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should answer an insecure request with 307 and an https Location and short-circuit")]
    public async Task Redirection_InsecureRequestWithDefaults_ShouldRespond307AndNotCallNext()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildRedirection();
        TestHttpContext context = new(HttpScheme.Http, "example.com", "/page");
        bool nextCalled = false;

        // Act
        await middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        // Assert
        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.Value.ShouldBe(307);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should emit 308 when configured permanent")]
    public async Task Redirection_ConfiguredPermanent_ShouldRespond308()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildRedirection(options => options.StatusCode = HttpStatusCode.PermanentRedirect);
        TestHttpContext context = new(HttpScheme.Http, "example.com", "/page");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(308);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should preserve the method by using a method-preserving status for a non-GET request")]
    public async Task Redirection_NonGetRequest_ShouldUseMethodPreservingStatus()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildRedirection();
        TestHttpContext context = new(HttpScheme.Http, "example.com", "/submit");
        context.Request.Method = HttpMethod.Post;

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert — 307 preserves the POST method and body across the redirect (RFC 9110 §15.4.8).
        context.Response.StatusCode.Value.ShouldBe(307);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should omit the port for the default HTTPS port 443 and drop the inbound port")]
    public async Task Redirection_DefaultHttpsPort_ShouldOmitPortAndDropInboundPort()
    {
        // Arrange — inbound host carries the plaintext :80 port, which must be replaced, not kept.
        IWebApplicationMiddleware middleware = BuildRedirection();
        TestHttpContext context = new(HttpScheme.Http, "example.com:80", "/page");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should emit a non-default HTTPS port and swap the inbound port")]
    public async Task Redirection_CustomHttpsPort_ShouldSwapInboundPortForHttpsPort()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildRedirection(options => options.HttpsPort = 8443);
        TestHttpContext context = new(HttpScheme.Http, "example.com:8080", "/page");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com:8443/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should preserve the request path verbatim")]
    public async Task Redirection_PathWithSegments_ShouldPreservePathVerbatim()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildRedirection();
        TestHttpContext context = new(HttpScheme.Http, "example.com", "/a/b/c");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/a/b/c");
    }

    [Theory(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should percent-encode each segment of the decoded path")]
    [InlineData("/café", "https://example.com/caf%C3%A9")]
    [InlineData("/docs/日本", "https://example.com/docs/%E6%97%A5%E6%9C%AC")]
    [InlineData("/100%", "https://example.com/100%25")]
    [InlineData("/a\\b", "https://example.com/a%5Cb")]
    [InlineData("/a%2Fb", "https://example.com/a%2Fb")]
    [InlineData("/caf%E9", "https://example.com/caf%E9")]
    public async Task Redirection_PathNeedingEncoding_ShouldPercentEncodeEachSegment(string decodedPath, string expected)
    {
        // Arrange — the transports hand the middleware the percent-decoded path, so "/caf%C3%A9" on the
        // wire arrives as "/café" and "/100%25" as "/100%". An encoded slash stays encoded ("/a%2Fb"), as
        // does a Latin-1 escape that is not UTF-8 ("/caf%E9"), and neither may be encoded again into
        // "%252F" or "%25E9".
        IWebApplicationMiddleware middleware = BuildRedirection();
        TestHttpContext context = new(HttpScheme.Http, "example.com", decodedPath);

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(307);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should redirect an OPTIONS * request to the bare authority")]
    [InlineData(443, "https://example.com")]
    [InlineData(8443, "https://example.com:8443")]
    public async Task Redirection_AsteriskFormRequest_ShouldRedirectToTheAuthority(int httpsPort, string expected)
    {
        // Arrange — RFC 9112 §3.2.4: "*" names the server as a whole, and its absolute-form is the
        // authority with an empty path. Appended after the authority, it would become "example.com*".
        IWebApplicationMiddleware middleware = BuildRedirection(options => options.HttpsPort = httpsPort);
        TestHttpContext context = new(HttpScheme.Http, "example.com", "*");
        context.Request.Method = HttpMethod.Options;

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(307);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should answer 400 instead of redirecting to a host it cannot name")]
    [InlineData("")]
    [InlineData("example.com:")]
    [InlineData("example.com:99999")]
    [InlineData("[::1")]
    [InlineData("[::1]junk")]
    [InlineData("evil.example/path")]
    [InlineData("user@evil.example")]
    [InlineData("example.com\\evil")]
    [InlineData("example.com?x")]
    [InlineData("example.com#x")]
    [InlineData("exa mple.com")]
    public async Task Redirection_HostItCannotName_ShouldRespond400WithoutLocation(string host)
    {
        // Arrange — an empty host, one that is not host[:port], and one whose host would move part of
        // the URL into or out of the authority.
        IWebApplicationMiddleware middleware = BuildRedirection();
        TestHttpContext context = new(HttpScheme.Http, host, "/page");
        bool nextCalled = false;

        // Act
        await middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        // Assert — the request is neither redirected nor served over the insecure connection.
        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.Value.ShouldBe(400);
        context.Response.Headers.ContainsKey(HttpHeaderKey.Location).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should preserve and re-encode the query")]
    public async Task Redirection_RequestWithQuery_ShouldPreserveAndEncodeQuery()
    {
        // Arrange — a single query entry keeps the assertion deterministic while covering encoding.
        IWebApplicationMiddleware middleware = BuildRedirection();
        TestHttpContext context = new(HttpScheme.Http, "example.com", "/login");
        context.Request.Query["returnUrl"] = "/dashboard";

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert — the value's '/' is percent-encoded per application/x-www-form-urlencoded.
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/login?returnUrl=%2Fdashboard");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should carry every query entry across the redirect")]
    public async Task Redirection_RequestWithMultipleQueryEntries_ShouldCarryEveryEntry()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildRedirection();
        TestHttpContext context = new(HttpScheme.Http, "example.com", "/search");
        context.Request.Query["a"] = "1";
        context.Request.Query["b"] = "2";

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert — order follows the parsed collection's enumeration, so assert membership.
        string location = context.Response.Headers[HttpHeaderKey.Location].Value;
        location.ShouldStartWith("https://example.com/search?");
        location.ShouldContain("a=1", Case.Sensitive);
        location.ShouldContain("b=2", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should re-bracket an IPv6 host authority")]
    public async Task Redirection_IPv6Host_ShouldReBracketAuthority()
    {
        // Arrange — inbound bracketed IPv6 literal with a plaintext port.
        IWebApplicationMiddleware middleware = BuildRedirection();
        TestHttpContext context = new(HttpScheme.Http, "[::1]:80", "/page");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://[::1]/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should re-bracket an unbracketed IPv6 host and emit a custom port")]
    public async Task Redirection_UnbracketedIPv6HostWithCustomPort_ShouldBracketAndEmitPort()
    {
        // Arrange — unbracketed IPv6 literal (no port); the component split treats it as IPv6.
        IWebApplicationMiddleware middleware = BuildRedirection(options => options.HttpsPort = 8443);
        TestHttpContext context = new(HttpScheme.Http, "::1", "/page");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://[::1]:8443/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should pass an already-secure request straight through")]
    public async Task Redirection_AlreadySecureRequest_ShouldCallNextAndNotRedirect()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildRedirection();
        TestHttpContext context = new(HttpScheme.Https, "example.com", "/page");
        bool nextCalled = false;

        // Act
        await middleware.InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

        // Assert
        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.Value.ShouldBe(200);
        context.Response.Headers.ContainsKey(HttpHeaderKey.Location).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - AddHttpsRedirection: Should reject a non-method-preserving status at registration")]
    public void Redirection_NonMethodPreservingStatus_ShouldThrowAtBuilderTime()
    {
        // Arrange & Act & Assert
        Should.Throw<ArgumentException>(() => BuildRedirection(options => options.StatusCode = HttpStatusCode.Found));
    }

    [Theory(DisplayName = "Cohesion Test [Web.HttpsPolicy] - AddHttpsRedirection: Should reject an out-of-range HTTPS port at registration")]
    [InlineData(0)]
    [InlineData(70000)]
    public void Redirection_OutOfRangeHttpsPort_ShouldThrowAtBuilderTime(int port)
    {
        // Arrange & Act & Assert
        Should.Throw<ArgumentException>(() => BuildRedirection(options => options.HttpsPort = port));
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should place the middleware the application registered")]
    public async Task UseHttpsRedirection_WithRegisteredMiddleware_ShouldPlaceIt()
    {
        // Arrange
        TestPipelineBuilder pipeline = new();
        pipeline.Registered.Add(BuildRedirection(options => options.StatusCode = HttpStatusCode.PermanentRedirect));
        TestHttpContext context = new(HttpScheme.Http, "example.com");

        // Act
        pipeline.UseHttpsRedirection();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(308);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should place the defaults when the application registered none")]
    public async Task UseHttpsRedirection_WithoutRegistration_ShouldPlaceTheDefaults()
    {
        // Arrange
        TestPipelineBuilder pipeline = new();
        TestHttpContext context = new(HttpScheme.Http, "example.com");

        // Act
        pipeline.UseHttpsRedirection();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(307);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/");
    }

    private static IWebApplicationMiddleware BuildRedirection(Action<HttpsRedirectionOptions>? configure = null)
    {
        HttpsRedirectionBuilder redirection = new();
        configure?.Invoke(redirection.Options);

        return redirection.Build();
    }

    private static Task Terminal(IHttpContext context) => Task.CompletedTask;
}
