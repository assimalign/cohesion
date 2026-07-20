using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.HttpsPolicy.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.HttpsPolicy.Tests;

public class HstsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should emit the default policy on a secure request")]
    public async Task Hsts_SecureRequestWithDefaults_ShouldEmitOneYearMaxAge()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildHsts();
        TestHttpContext context = new(HttpScheme.Https, "example.com");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert — 365 days = 31,536,000 seconds.
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should not emit the policy over an insecure transport")]
    public async Task Hsts_InsecureRequest_ShouldNotEmitPolicy()
    {
        // Arrange — RFC 6797 §7.2: never assert HSTS over plaintext.
        IWebApplicationMiddleware middleware = BuildHsts();
        TestHttpContext context = new(HttpScheme.Http, "example.com");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers.ContainsKey(HttpHeaderKey.StrictTransportSecurity).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should append includeSubDomains when enabled")]
    public async Task Hsts_IncludeSubDomains_ShouldAppendDirective()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildHsts(options => options.IncludeSubDomains = true);
        TestHttpContext context = new(HttpScheme.Https, "example.com");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000; includeSubDomains");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should append preload when enabled")]
    public async Task Hsts_Preload_ShouldAppendDirective()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildHsts(options => options.Preload = true);
        TestHttpContext context = new(HttpScheme.Https, "example.com");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000; preload");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should compose max-age, includeSubDomains, and preload in order")]
    public async Task Hsts_AllDirectivesWithCustomMaxAge_ShouldComposeInOrder()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(30);
            options.IncludeSubDomains = true;
            options.Preload = true;
        });
        TestHttpContext context = new(HttpScheme.Https, "example.com");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert — 30 days = 2,592,000 seconds.
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=2592000; includeSubDomains; preload");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should emit max-age=0 for a zero window")]
    public async Task Hsts_ZeroMaxAge_ShouldEmitMaxAgeZero()
    {
        // Arrange — max-age=0 tells a user agent to delete a stored policy (RFC 6797 §6.1.1).
        IWebApplicationMiddleware middleware = BuildHsts(options => options.MaxAge = TimeSpan.Zero);
        TestHttpContext context = new(HttpScheme.Https, "example.com");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=0");
    }

    [Theory(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should exclude loopback authorities by default")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task Hsts_DefaultExcludedLoopbackHost_ShouldNotEmitPolicy(string host)
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildHsts();
        TestHttpContext context = new(HttpScheme.Https, host);

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers.ContainsKey(HttpHeaderKey.StrictTransportSecurity).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should ignore the request port when matching an excluded host")]
    public async Task Hsts_ExcludedHostWithPort_ShouldStillBeExcluded()
    {
        // Arrange — the matcher compares the host component only, ignoring the port.
        IWebApplicationMiddleware middleware = BuildHsts();
        TestHttpContext context = new(HttpScheme.Https, "localhost:5001");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers.ContainsKey(HttpHeaderKey.StrictTransportSecurity).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should emit on a non-excluded host")]
    public async Task Hsts_NonExcludedHost_ShouldEmitPolicy()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildHsts();
        TestHttpContext context = new(HttpScheme.Https, "api.example.com");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should emit on loopback when the exclusion list is cleared")]
    public async Task Hsts_ClearedExclusions_ShouldEmitOnLoopback()
    {
        // Arrange — clearing the list makes the matcher null (no exclusions), not deny-all.
        IWebApplicationMiddleware middleware = BuildHsts(options => options.ExcludedHosts.Clear());
        TestHttpContext context = new(HttpScheme.Https, "localhost");

        // Act
        await middleware.InvokeAsync(context, Terminal);

        // Assert
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should honor a custom wildcard exclusion")]
    public async Task Hsts_CustomWildcardExclusion_ShouldExcludeMatchingSubdomain()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildHsts(options =>
        {
            options.ExcludedHosts.Clear();
            options.ExcludedHosts.Add("*.internal");
        });

        TestHttpContext excluded = new(HttpScheme.Https, "api.internal");
        TestHttpContext emitted = new(HttpScheme.Https, "example.com");

        // Act
        await middleware.InvokeAsync(excluded, Terminal);
        await middleware.InvokeAsync(emitted, Terminal);

        // Assert
        excluded.Response.Headers.ContainsKey(HttpHeaderKey.StrictTransportSecurity).ShouldBeFalse();
        emitted.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should apply the header after next so it survives a reset error response")]
    public async Task Hsts_FaultedResponseReset_ShouldStillCarryPolicy()
    {
        // Arrange — mimic the #881 exception boundary: a downstream fault cleared the headers and
        // rendered a fresh 500. Because HSTS is applied post-next, it lands on that reset response.
        IWebApplicationMiddleware middleware = BuildHsts();
        TestHttpContext context = new(HttpScheme.Https, "example.com");

        // Act
        await middleware.InvokeAsync(context, resetContext =>
        {
            resetContext.Response.Headers.Clear();
            resetContext.Response.StatusCode = HttpStatusCode.InternalServerError;
            return Task.CompletedTask;
        });

        // Assert
        context.Response.StatusCode.Value.ShouldBe(500);
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should apply the header only after the pipeline unwinds")]
    public async Task Hsts_EmissionPoint_ShouldBePostNext()
    {
        // Arrange
        IWebApplicationMiddleware middleware = BuildHsts();
        TestHttpContext context = new(HttpScheme.Https, "example.com");
        bool presentDuringNext = true;

        // Act
        await middleware.InvokeAsync(context, observed =>
        {
            presentDuringNext = observed.Response.Headers.ContainsKey(HttpHeaderKey.StrictTransportSecurity);
            return Task.CompletedTask;
        });

        // Assert
        presentDuringNext.ShouldBeFalse();
        context.Response.Headers.ContainsKey(HttpHeaderKey.StrictTransportSecurity).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should not fault when the response head is already committed")]
    public async Task Hsts_CommittedResponseHead_ShouldSkipWithoutFaulting()
    {
        // Arrange — a read-only header collection models a committed/streamed head.
        IWebApplicationMiddleware middleware = BuildHsts();
        TestHttpContext context = new(HttpScheme.Https, "example.com", responseHeaders: new HttpHeaderCollection().AsReadOnly());

        // Act & Assert — no throw, and no header (a committed head can carry no new field).
        await Should.NotThrowAsync(() => middleware.InvokeAsync(context, Terminal));
        context.Response.Headers.ContainsKey(HttpHeaderKey.StrictTransportSecurity).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should reject a negative max-age at builder time")]
    public void Hsts_NegativeMaxAge_ShouldThrowAtBuilderTime()
    {
        // Arrange & Act & Assert
        Should.Throw<ArgumentException>(() => BuildHsts(options => options.MaxAge = TimeSpan.FromSeconds(-1)));
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - UseHsts: Should reject an invalid excluded-host pattern at builder time")]
    public void Hsts_PortBearingExcludedHost_ShouldThrowAtBuilderTime()
    {
        // Arrange & Act & Assert — a port-bearing pattern is rejected by HttpHostMatcher.Create.
        Should.Throw<ArgumentException>(() => BuildHsts(options =>
        {
            options.ExcludedHosts.Clear();
            options.ExcludedHosts.Add("example.com:8080");
        }));
    }

    private static IWebApplicationMiddleware BuildHsts(Action<HstsOptions>? configure = null)
    {
        TestPipelineBuilder builder = new();
        builder.UseHsts(configure);

        return builder.LastMiddleware.ShouldNotBeNull();
    }

    private static Task Terminal(IHttpContext context) => Task.CompletedTask;
}
