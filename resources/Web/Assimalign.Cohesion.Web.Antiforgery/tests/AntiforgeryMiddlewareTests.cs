using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Antiforgery.Tests;

/// <summary>
/// Middleware-level coverage for <c>UseAntiforgery</c> over a composing pipeline harness. A stage ahead of
/// the middleware publishes a fake route match, which is what <c>UseRouting</c> does: exempt and validated
/// methods, the header-first token read, the form read and its failure, metadata states, the preflight
/// skip, the problem+json rejection and the committed-head abort, and validation with the exchange's
/// service.
/// </summary>
public class AntiforgeryMiddlewareTests
{
    private const string cookieName = "__cohesion-antiforgery";
    private const string headerName = "X-CSRF-TOKEN";
    private const string formFieldName = "__RequestVerificationToken";

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - UseAntiforgery: Should throw on a null pipeline builder")]
    public void UseAntiforgery_NullBuilder_ShouldThrow()
    {
        // Arrange
        IWebApplicationPipelineBuilder builder = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.UseAntiforgery());
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - UseAntiforgery: Without AddAntiforgery the pipeline should fail to build")]
    public void UseAntiforgery_WithoutAddAntiforgery_ShouldThrowWhenPipelineIsBuilt()
    {
        // Arrange
        TestPipelineBuilder pipeline = new(new TestWebApplicationContext([]), Ok);
        pipeline.UseAntiforgery();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => pipeline.Build());

        // Assert
        exception.Message.ShouldContain("AddAntiforgery()", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A safe method should pass without a token and leave the body unread")]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public async Task InvokeAsync_ExemptMethodWithoutToken_ShouldRunEndpointWithoutReadingBody(string method)
    {
        // Arrange
        int endpointInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(Counting(() => endpointInvocations++), out _);
        await using AntiforgeryTestContext context = AntiforgeryTestContext.ForUrlEncodedForm(HttpMethod.GetCanonicalizedValue(method), "name=value");
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        endpointInvocations.ShouldBe(1);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.RequestBody.Position.ShouldBe(0);
        context.Features.Get<IHttpFormFeature>().ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: An unsafe method without a token should be rejected")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("QUERY")]
    public async Task InvokeAsync_UnsafeMethodWithoutToken_ShouldRejectWithoutRunningEndpoint(string method)
    {
        // Arrange — QUERY is safe (RFC 10008) but carries a body, so it is validated like POST.
        int endpointInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(Counting(() => endpointInvocations++), out _);
        await using AntiforgeryTestContext context = new(HttpMethod.GetCanonicalizedValue(method));
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        endpointInvocations.ShouldBe(0);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A rejection should be a 400 problem+json response")]
    public async Task InvokeAsync_Rejection_ShouldWriteProblemDetails()
    {
        // Arrange
        IWebApplicationPipeline pipeline = BuildPipeline(Ok, out _);
        await using AntiforgeryTestContext context = new(HttpMethod.Post);
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        context.Response.Headers.GetValue(HttpHeaderKey.ContentType).ShouldBe("application/problem+json");
        string body = context.ReadResponseBody();
        body.ShouldContain("\"status\":400", Case.Sensitive);
        body.ShouldContain("antiforgery token was missing or invalid", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A valid cookie and header token pair should run the endpoint")]
    public async Task InvokeAsync_ValidHeaderToken_ShouldRunEndpoint()
    {
        // Arrange
        int endpointInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(Counting(() => endpointInvocations++), out IHttpAntiforgery antiforgery);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        await using AntiforgeryTestContext context = new AntiforgeryTestContext(HttpMethod.Post)
            .WithCookie(cookieName, tokens.CookieToken!)
            .WithHeader(headerName, tokens.RequestToken!);
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        endpointInvocations.ShouldBe(1);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A valid form token should run the endpoint with the parsed form on the exchange")]
    public async Task InvokeAsync_ValidFormToken_ShouldRunEndpointWithParsedForm()
    {
        // Arrange
        string? boundName = null;
        IWebApplicationPipeline pipeline = BuildPipeline(
            context =>
            {
                // The middleware parsed the form; the endpoint reads it without touching the body again.
                boundName = context.Request.Form.TryGetValue("name", out HttpQueryValue value) ? value.Value : null;
                return Task.CompletedTask;
            },
            out IHttpAntiforgery antiforgery);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        await using AntiforgeryTestContext context = AntiforgeryTestContext
            .ForUrlEncodedForm(HttpMethod.Post, $"name=ada&{formFieldName}={Uri.EscapeDataString(tokens.RequestToken!)}")
            .WithCookie(cookieName, tokens.CookieToken!);
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        boundName.ShouldBe("ada");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A header token should leave a form body unread for the endpoint")]
    public async Task InvokeAsync_HeaderTokenWithFormBody_ShouldNotReadBody()
    {
        // Arrange
        long? positionAtEndpoint = null;
        IWebApplicationPipeline pipeline = BuildPipeline(
            context =>
            {
                positionAtEndpoint = context.Request.Body.Position;
                return Task.CompletedTask;
            },
            out IHttpAntiforgery antiforgery);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        await using AntiforgeryTestContext context = AntiforgeryTestContext
            .ForUrlEncodedForm(HttpMethod.Post, "name=ada")
            .WithCookie(cookieName, tokens.CookieToken!)
            .WithHeader(headerName, tokens.RequestToken!);
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        positionAtEndpoint.ShouldBe(0);
        context.Features.Get<IHttpFormFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A form the reader rejects should be rejected as an antiforgery failure")]
    public async Task InvokeAsync_UnreadableForm_ShouldReject()
    {
        // Arrange — a multipart boundary over the 70-character limit (RFC 2046) makes the form reader throw.
        int endpointInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(Counting(() => endpointInvocations++), out IHttpAntiforgery antiforgery);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        await using AntiforgeryTestContext context = new AntiforgeryTestContext(HttpMethod.Post, Encoding.UTF8.GetBytes("ignored"))
            .WithCookie(cookieName, tokens.CookieToken!)
            .WithHeader("Content-Type", "multipart/form-data; boundary=" + new string('b', 120));
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        endpointInvocations.ShouldBe(0);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A token bound to another cookie should be rejected")]
    public async Task InvokeAsync_TokenFromAnotherCookie_ShouldReject()
    {
        // Arrange — two independently minted pairs; the request token of one with the cookie of the other.
        IWebApplicationPipeline pipeline = BuildPipeline(Ok, out IHttpAntiforgery antiforgery);
        HttpAntiforgeryTokenSet first = antiforgery.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));
        HttpAntiforgeryTokenSet second = antiforgery.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        await using AntiforgeryTestContext context = new AntiforgeryTestContext(HttpMethod.Post)
            .WithCookie(cookieName, first.CookieToken!)
            .WithHeader(headerName, second.RequestToken!);
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: An endpoint without antiforgery metadata should pass through")]
    public async Task InvokeAsync_EndpointWithoutMetadata_ShouldRunEndpoint()
    {
        // Arrange
        int endpointInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(Counting(() => endpointInvocations++), out _);
        await using AntiforgeryTestContext context = new(HttpMethod.Post);
        Publish(context);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        endpointInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A request with no endpoint should pass through")]
    public async Task InvokeAsync_NoEndpoint_ShouldCallNext()
    {
        // Arrange
        int nextInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(Counting(() => nextInvocations++), out _);
        await using AntiforgeryTestContext context = new(HttpMethod.Post);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        nextInvocations.ShouldBe(1);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A route that disables antiforgery under a requiring group should pass")]
    public async Task InvokeAsync_DisabledAfterRequired_ShouldRunEndpoint()
    {
        // Arrange — group metadata first, the route's own last: last-wins resolves Disabled.
        int endpointInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(Counting(() => endpointInvocations++), out _);
        await using AntiforgeryTestContext context = new(HttpMethod.Post);
        Publish(context, AntiforgeryMetadata.Required, AntiforgeryMetadata.Disabled);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        endpointInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A route that requires antiforgery under a disabling group should be validated")]
    public async Task InvokeAsync_RequiredAfterDisabled_ShouldReject()
    {
        // Arrange
        IWebApplicationPipeline pipeline = BuildPipeline(Ok, out _);
        await using AntiforgeryTestContext context = new(HttpMethod.Post);
        Publish(context, AntiforgeryMetadata.Disabled, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A CORS preflight's candidate endpoint should not be validated")]
    public async Task InvokeAsync_PreflightCandidate_ShouldNotValidate()
    {
        // Arrange — the method is unsafe on purpose: only the preflight flag can explain the pass-through.
        int nextInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(Counting(() => nextInvocations++), out _);
        await using AntiforgeryTestContext context = AntiforgeryTestContext.ForUrlEncodedForm(HttpMethod.Post, "name=value");
        context.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(AntiforgeryMetadata.Required) { IsPreflight = true });

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        nextInvocations.ShouldBe(1);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.RequestBody.Position.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A rejection after the head was committed should abort the exchange")]
    public async Task InvokeAsync_RejectionAfterResponseStarted_ShouldAbort()
    {
        // Arrange
        int endpointInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(Counting(() => endpointInvocations++), out _);
        await using AntiforgeryTestContext context = new(HttpMethod.Post);
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature());
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        context.CancelRequested.ShouldBeTrue();
        endpointInvocations.ShouldBe(0);
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: Validation should use the service the exchange carries")]
    public async Task InvokeAsync_ExchangeServiceReplaced_ShouldValidateWithIt()
    {
        // Arrange — a middleware ahead replaced the registered service for this exchange; tokens minted by
        // the replacement validate, so minting and validation always agree.
        IWebApplicationPipeline pipeline = BuildPipeline(Ok, out _);
        IHttpAntiforgery replacement = HttpAntiforgery.Create();
        HttpAntiforgeryTokenSet tokens = replacement.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        await using AntiforgeryTestContext context = new AntiforgeryTestContext(HttpMethod.Post)
            .WithCookie(cookieName, tokens.CookieToken!)
            .WithHeader(headerName, tokens.RequestToken!);
        context.Antiforgery = replacement;
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A custom token header should be honored")]
    public async Task InvokeAsync_CustomHeaderName_ShouldValidateFromIt()
    {
        // Arrange
        IWebApplicationPipeline pipeline = BuildPipeline(Ok, out IHttpAntiforgery antiforgery, options => options.HeaderName = "X-XSRF-TOKEN");
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(new AntiforgeryTestContext(HttpMethod.Get));

        await using AntiforgeryTestContext context = new AntiforgeryTestContext(HttpMethod.Post)
            .WithCookie(cookieName, tokens.CookieToken!)
            .WithHeader("X-XSRF-TOKEN", tokens.RequestToken!);
        Publish(context, AntiforgeryMetadata.Required);

        // Act
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Assert
        tokens.HeaderName.ShouldBe("X-XSRF-TOKEN");
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    private static IWebApplicationPipeline BuildPipeline(
        WebApplicationMiddleware endpoint,
        out IHttpAntiforgery antiforgery,
        Action<HttpAntiforgeryOptions>? configure = null)
    {
        TestWebApplicationBuilder application = new();
        application.AddAntiforgery(configure);
        antiforgery = application.Antiforgery;

        TestPipelineBuilder pipeline = new(new TestWebApplicationContext(application.Features), endpoint);
        pipeline.UseAntiforgery();
        return pipeline.Build();
    }

    // What UseRouting does ahead of the middleware: publish the matched endpoint and its metadata.
    private static void Publish(IHttpContext context, params object[] metadata)
        => context.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(metadata));

    private static Task Ok(IHttpContext context) => Task.CompletedTask;

    private static WebApplicationMiddleware Counting(Action onInvoke) => context =>
    {
        onInvoke();
        return Task.CompletedTask;
    };
}
