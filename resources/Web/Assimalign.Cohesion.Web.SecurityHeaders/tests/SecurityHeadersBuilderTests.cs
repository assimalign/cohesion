using System;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

/// <summary>
/// "Add configures, Use places": <see cref="SecurityHeadersBuilder.Build"/> validates and compiles the policy,
/// and <c>UseSecurityHeaders()</c> places the security-headers middleware the application registered (the
/// last one), or the defaults when it registered none. The placement tests compose over
/// <see cref="TestPipelineBuilder"/>, whose application context lists the registrations, and stage the
/// fields through the starting callback of <see cref="TestHttpContext"/>.
/// </summary>
public class SecurityHeadersBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Build: An undefined enumeration value should throw ArgumentOutOfRangeException")]
    public void Build_UndefinedEnumerationValue_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        SecurityHeadersBuilder headers = new();
        headers.Options.CrossOriginOpenerPolicy = (CrossOriginOpenerPolicy)42;

        // Act
        Action act = () => headers.Build();

        // Assert
        act.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - UseSecurityHeaders: Should place the middleware the application registered")]
    public async Task UseSecurityHeaders_WithRegisteredMiddleware_ShouldPlaceIt()
    {
        // Arrange
        TestPipelineBuilder pipeline = new();
        pipeline.Registered.Add(BuildSecurityHeaders(policy =>
        {
            policy.CrossOriginOpenerPolicy = CrossOriginOpenerPolicy.SameOrigin;
            policy.ReferrerPolicy = ReferrerPolicy.NoReferrer;
        }));
        TestHttpContext context = new();

        // Act
        pipeline.UseSecurityHeaders();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);
        await context.Starting.InvokeAsync();

        // Assert
        context.Response.Headers[HttpHeaderKey.CrossOriginOpenerPolicy].Value.ShouldBe("same-origin");
        context.Response.Headers[HttpHeaderKey.ReferrerPolicy].Value.ShouldBe("no-referrer");
        context.Response.Headers[HttpHeaderKey.XContentTypeOptions].Value.ShouldBe("nosniff");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - UseSecurityHeaders: Should place the last registration when the headers are registered twice")]
    public async Task UseSecurityHeaders_WithTwoRegistrations_ShouldPlaceTheLast()
    {
        // Arrange — each registration opts into a different field, so the response shows which one ran.
        TestPipelineBuilder pipeline = new();
        pipeline.Registered.Add(BuildSecurityHeaders(policy => policy.CrossOriginOpenerPolicy = CrossOriginOpenerPolicy.SameOrigin));
        pipeline.Registered.Add(BuildSecurityHeaders(policy => policy.CrossOriginResourcePolicy = CrossOriginResourcePolicy.SameSite));
        TestHttpContext context = new();

        // Act
        pipeline.UseSecurityHeaders();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);
        await context.Starting.InvokeAsync();

        // Assert
        context.Response.Headers[HttpHeaderKey.CrossOriginResourcePolicy].Value.ShouldBe("same-site");
        context.Response.Headers.ContainsKey(HttpHeaderKey.CrossOriginOpenerPolicy).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - UseSecurityHeaders: Should place the safe defaults when the application registered none")]
    public async Task UseSecurityHeaders_WithoutRegistration_ShouldPlaceTheDefaults()
    {
        // Arrange
        TestPipelineBuilder pipeline = new();
        TestHttpContext context = new();

        // Act
        pipeline.UseSecurityHeaders();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);
        await context.Starting.InvokeAsync();

        // Assert
        context.Response.Headers[HttpHeaderKey.XContentTypeOptions].Value.ShouldBe("nosniff");
        context.Response.Headers[HttpHeaderKey.ContentSecurityPolicy].Value.ShouldBe("frame-ancestors 'none'");
        context.Response.Headers[HttpHeaderKey.XFrameOptions].Value.ShouldBe("DENY");
        context.Response.Headers[HttpHeaderKey.ReferrerPolicy].Value.ShouldBe("strict-origin-when-cross-origin");
        context.Response.Headers.ContainsKey(HttpHeaderKey.CrossOriginOpenerPolicy).ShouldBeFalse();
        context.Response.Headers.ContainsKey(HttpHeaderKey.PermissionsPolicy).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - UseSecurityHeaders: Should place the defaults, not another feature's registered middleware")]
    public async Task UseSecurityHeaders_WithOnlyOtherMiddlewareRegistered_ShouldPlaceTheDefaults()
    {
        // Arrange — every feature registers under IWebApplicationMiddleware; the verb takes only its own.
        TestPipelineBuilder pipeline = new();
        CountingMiddleware other = new();
        pipeline.Registered.Add(other);
        TestHttpContext context = new();

        // Act
        pipeline.UseSecurityHeaders();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);
        await context.Starting.InvokeAsync();

        // Assert
        other.Invocations.ShouldBe(0);
        context.Response.Headers[HttpHeaderKey.XContentTypeOptions].Value.ShouldBe("nosniff");
        context.Response.Headers[HttpHeaderKey.XFrameOptions].Value.ShouldBe("DENY");
    }

    private static IWebApplicationMiddleware BuildSecurityHeaders(Action<SecurityHeadersPolicy>? configure = null)
    {
        SecurityHeadersBuilder headers = new();
        configure?.Invoke(headers.Options);

        return headers.Build();
    }
}
