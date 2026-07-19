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

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should reject a non-method-preserving status at builder time")]
    public void Redirection_NonMethodPreservingStatus_ShouldThrowAtBuilderTime()
    {
        // Arrange & Act & Assert
        Should.Throw<ArgumentException>(() => BuildRedirection(options => options.StatusCode = HttpStatusCode.Found));
    }

    [Theory(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHttpsRedirection: Should reject an out-of-range HTTPS port at builder time")]
    [InlineData(0)]
    [InlineData(70000)]
    public void Redirection_OutOfRangeHttpsPort_ShouldThrowAtBuilderTime(int port)
    {
        // Arrange & Act & Assert
        Should.Throw<ArgumentException>(() => BuildRedirection(options => options.HttpsPort = port));
    }

    private static IWebApplicationMiddleware BuildRedirection(Action<HttpsRedirectionOptions>? configure = null)
    {
        TestPipelineBuilder builder = new();
        builder.UseHttpsRedirection(configure);

        return builder.LastMiddleware.ShouldNotBeNull();
    }

    private static Task Terminal(IHttpContext context) => Task.CompletedTask;
}
