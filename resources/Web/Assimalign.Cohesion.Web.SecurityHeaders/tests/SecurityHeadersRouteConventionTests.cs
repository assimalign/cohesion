using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using static Assimalign.Cohesion.Web.SecurityHeaders.Tests.SecurityHeadersTestHost;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

/// <summary>
/// The endpoint overrides over the real router: <c>WithSecurityHeaders</c> adjusting or replacing the
/// pipeline's policy, <c>DisableSecurityHeaders</c>, group-versus-route precedence, the override reaching
/// an endpoint's error and streamed responses while unrouted responses keep the pipeline policy, and the
/// CORS preflight that never runs its candidate endpoint. <c>UseSecurityHeaders</c> sits ahead of
/// <c>UseRouting</c> throughout, its documented position.
/// </summary>
public class SecurityHeadersRouteConventionTests
{
    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: An adjustment should apply to its route only")]
    public async Task WithSecurityHeaders_Adjustment_ShouldApplyToItsRouteOnly()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders();
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/embed", Ok()).WithSecurityHeaders(policy => policy.Framing = FramingPolicy.SameOrigin);
        routes.Map(CohesionHttpMethod.Get, "/page", Ok());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage embed = await client.GetAsync("/embed", cancellation.Token);
        using HttpResponseMessage page = await client.GetAsync("/page", cancellation.Token);
        using HttpResponseMessage missing = await client.GetAsync("/missing", cancellation.Token);

        // Assert
        Header(embed, "X-Frame-Options").ShouldBe("SAMEORIGIN");
        Header(embed, "Content-Security-Policy").ShouldBe("frame-ancestors 'self'");
        Header(page, "X-Frame-Options").ShouldBe("DENY");
        Header(page, "Content-Security-Policy").ShouldBe("frame-ancestors 'none'");
        missing.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        Header(missing, "X-Frame-Options").ShouldBe("DENY");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: An adjustment should start from the pipeline's configured policy")]
    public async Task WithSecurityHeaders_Adjustment_ShouldKeepThePipelinePolicy()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders(policy =>
        {
            policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp.DefaultSrc(sources => sources.Self()));
            policy.CrossOriginOpenerPolicy = CrossOriginOpenerPolicy.SameOrigin;
        });
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/partner", Ok())
            .WithSecurityHeaders(policy => policy.Framing = FramingPolicy.AllowFrom("https://partner.example.com"));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/partner", cancellation.Token);

        // Assert
        Header(response, "Content-Security-Policy").ShouldBe("default-src 'self'; frame-ancestors https://partner.example.com");
        Header(response, "X-Frame-Options").ShouldBeNull();
        Header(response, "Cross-Origin-Opener-Policy").ShouldBe("same-origin");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: A replacement should take the place of the pipeline's policy")]
    public async Task WithSecurityHeaders_Replacement_ShouldReplaceThePipelinePolicy()
    {
        // Arrange — the replacement starts from the safe defaults, not from the pipeline's opt-ins.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders(policy =>
        {
            policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp.DefaultSrc(sources => sources.Self()));
            policy.CrossOriginOpenerPolicy = CrossOriginOpenerPolicy.SameOrigin;
        });
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/api", Ok())
            .WithSecurityHeaders(new SecurityHeadersPolicy { ReferrerPolicy = ReferrerPolicy.NoReferrer });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/api", cancellation.Token);

        // Assert
        Header(response, "Content-Security-Policy").ShouldBe("frame-ancestors 'none'");
        Header(response, "Cross-Origin-Opener-Policy").ShouldBeNull();
        Header(response, "Referrer-Policy").ShouldBe("no-referrer");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: A replacement should be a copy taken when it is attached")]
    public async Task WithSecurityHeaders_ReplacementMutatedLater_ShouldKeepTheCapturedCopy()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        SecurityHeadersPolicy replacement = new() { ReferrerPolicy = ReferrerPolicy.NoReferrer };
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders();
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/api", Ok()).WithSecurityHeaders(replacement);
        replacement.ReferrerPolicy = ReferrerPolicy.UnsafeUrl;
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/api", cancellation.Token);

        // Assert
        Header(response, "Referrer-Policy").ShouldBe("no-referrer");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: Disabling should drop every field except the application's own")]
    public async Task DisableSecurityHeaders_OnRoute_ShouldEmitNoField()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders(policy => policy.CrossOriginResourcePolicy = CrossOriginResourcePolicy.SameOrigin);
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/raw", new RouterRouteHandler(context =>
        {
            context.Response.Headers[HttpHeaderKey.ReferrerPolicy] = "same-origin";
            return WriteTextAsync(context, "raw");
        })).DisableSecurityHeaders();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/raw", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        Header(response, "X-Content-Type-Options").ShouldBeNull();
        Header(response, "X-Frame-Options").ShouldBeNull();
        Header(response, "Content-Security-Policy").ShouldBeNull();
        Header(response, "Cross-Origin-Resource-Policy").ShouldBeNull();
        Header(response, "Referrer-Policy").ShouldBe("same-origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: A route-level declaration should override its group's")]
    public async Task WithSecurityHeaders_RouteInDisabledGroup_ShouldOverrideTheGroup()
    {
        // Arrange — the group disables the headers; one route opts back in with an empty adjustment.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders();
        IRouterBuilder routes = factory.Application.UseRouting();
        IRouterGroupBuilder api = routes.MapGroup("/api").DisableSecurityHeaders();
        api.Map(CohesionHttpMethod.Get, "data", Ok());
        api.Map(CohesionHttpMethod.Get, "page", Ok()).WithSecurityHeaders(policy => { });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage data = await client.GetAsync("/api/data", cancellation.Token);
        using HttpResponseMessage page = await client.GetAsync("/api/page", cancellation.Token);

        // Assert
        Header(data, "X-Content-Type-Options").ShouldBeNull();
        Header(data, "X-Frame-Options").ShouldBeNull();
        Header(page, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(page, "X-Frame-Options").ShouldBe("DENY");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: An endpoint's override should reach its error response")]
    public async Task WithSecurityHeaders_EndpointFault_ShouldApplyToTheErrorResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders();
        factory.Application.UseErrorHandling();
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/widget", new RouterRouteHandler(context =>
            throw new InvalidOperationException("Deliberate endpoint fault.")))
            .WithSecurityHeaders(policy => policy.Framing = FramingPolicy.SameOrigin);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/widget", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        Header(response, "X-Frame-Options").ShouldBe("SAMEORIGIN");
        Header(response, "Content-Security-Policy").ShouldBe("frame-ancestors 'self'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: A CORS preflight should keep the pipeline policy")]
    public async Task DisableSecurityHeaders_CorsPreflight_ShouldUseThePipelinePolicy()
    {
        // Arrange — routing publishes the DELETE route as the preflight's candidate; it never runs for it.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders();
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Delete, "/items", Ok()).DisableSecurityHeaders();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage preflight = new(NetHttpMethod.Options, "/items");
        preflight.Headers.Add("Origin", "https://app.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "DELETE");

        // Act
        using HttpResponseMessage response = await client.SendAsync(preflight, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: A streamed endpoint should carry its override")]
    public async Task WithSecurityHeaders_StreamedEndpoint_ShouldApplyTheOverride()
    {
        // Arrange — the head commits inside the handler, after UseRouting has published the endpoint.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(streaming: true);
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders();
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/events", new RouterRouteHandler(context => StreamTextAsync(context, "data")))
            .WithSecurityHeaders(policy => policy.Framing = FramingPolicy.SameOrigin);
        routes.Map(CohesionHttpMethod.Get, "/raw-events", new RouterRouteHandler(context => StreamTextAsync(context, "data")))
            .DisableSecurityHeaders();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage adjusted = await client.GetAsync("/events", cancellation.Token);
        using HttpResponseMessage disabled = await client.GetAsync("/raw-events", cancellation.Token);

        // Assert
        (await adjusted.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("data");
        Header(adjusted, "X-Frame-Options").ShouldBe("SAMEORIGIN");
        Header(adjusted, "X-Content-Type-Options").ShouldBe("nosniff");
        (await disabled.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("data");
        Header(disabled, "X-Frame-Options").ShouldBeNull();
        Header(disabled, "X-Content-Type-Options").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: An adjustment should be compiled once and reused")]
    public async Task WithSecurityHeaders_Adjustment_ShouldRunOncePerEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        int runs = 0;
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        factory.Application.UseSecurityHeaders();
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/embed", Ok()).WithSecurityHeaders(policy =>
        {
            Interlocked.Increment(ref runs);
            policy.Framing = FramingPolicy.SameOrigin;
        });
        using HttpClient client = factory.CreateClient();

        // Act
        for (int request = 0; request < 3; request++)
        {
            using HttpResponseMessage response = await client.GetAsync("/embed", cancellation.Token);
            Header(response, "X-Frame-Options").ShouldBe("SAMEORIGIN");
        }

        // Assert
        runs.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: An invalid replacement should throw when it is attached")]
    public async Task WithSecurityHeaders_InvalidReplacement_ShouldThrowAtMapTime()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Builder.Services.AddRouting();
        IRouterBuilder routes = factory.Application.UseRouting();
        IRouterRouteBuilder route = routes.Map(CohesionHttpMethod.Get, "/api", Ok());

        // Act
        Action act = () => route.WithSecurityHeaders(new SecurityHeadersPolicy
        {
            CrossOriginEmbedderPolicy = (CrossOriginEmbedderPolicy)17,
        });

        // Assert
        act.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: Metadata should report its kind")]
    public void SecurityHeadersMetadata_Kinds_ShouldReportDisabledAndAdjustment()
    {
        // Arrange & Act
        SecurityHeadersMetadata adjustment = new(policy => policy.Framing = null);
        SecurityHeadersMetadata replacement = new(new SecurityHeadersPolicy());

        // Assert
        SecurityHeadersMetadata.Disabled.IsDisabled.ShouldBeTrue();
        SecurityHeadersMetadata.Disabled.IsAdjustment.ShouldBeFalse();
        adjustment.IsDisabled.ShouldBeFalse();
        adjustment.IsAdjustment.ShouldBeTrue();
        replacement.IsDisabled.ShouldBeFalse();
        replacement.IsAdjustment.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: A verb on a null builder should throw ArgumentNullException")]
    public void WithSecurityHeaders_NullBuilder_ShouldThrow()
    {
        // Arrange
        IRouterRouteBuilder builder = null!;

        // Act
        Action adjust = () => builder.WithSecurityHeaders(policy => { });
        Action replace = () => builder.WithSecurityHeaders(new SecurityHeadersPolicy());
        Action disable = () => builder.DisableSecurityHeaders();

        // Assert
        adjust.ShouldThrow<ArgumentNullException>();
        replace.ShouldThrow<ArgumentNullException>();
        disable.ShouldThrow<ArgumentNullException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Conventions: A null policy or adjustment should throw ArgumentNullException")]
    public void SecurityHeadersMetadata_NullArguments_ShouldThrow()
    {
        // Arrange & Act
        Action replacement = () => _ = new SecurityHeadersMetadata((SecurityHeadersPolicy)null!);
        Action adjustment = () => _ = new SecurityHeadersMetadata((Action<SecurityHeadersPolicy>)null!);

        // Assert
        replacement.ShouldThrow<ArgumentNullException>();
        adjustment.ShouldThrow<ArgumentNullException>();
    }
}
