using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

using Shouldly;

using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Cors.Tests;

/// <summary>
/// Middleware-level coverage for <c>UseCors</c> over the pipeline harness: actual-request headers (echoed,
/// wildcard, credentialed, denied, exposed, <c>Vary: Origin</c>), preflight evaluation (origin, byte-exact
/// methods with the safelisted ones always allowed, case-insensitive headers, any-method and any-header
/// echoes, max age), policy selection (default, named, inline, disabled, last-wins), the preflight policy
/// source when routing published a candidate, an explicit route, or nothing, and the re-application of the
/// headers after a downstream reset.
/// </summary>
public class CorsMiddlewareTests
{
    private const string App = "https://app.example";
    private const string Other = "https://other.example";

    // ------------------------------------------------------------------ actual requests

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - UseCors: Should throw on a null pipeline builder")]
    public void UseCors_NullBuilder_ShouldThrow()
    {
        // Arrange
        IWebApplicationPipelineBuilder builder = null!;

        // Act
        Action act = () => builder.UseCors();

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: An allowed origin should be echoed, with Vary: Origin")]
    public async Task InvokeAsync_AllowedOrigin_ShouldEchoOriginAndVaryByOrigin()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App)));

        // Assert
        continued.ShouldBeTrue();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(App);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowCredentials).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBe("Origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: An any-origin policy should answer '*' without Vary")]
    public async Task InvokeAsync_AnyOrigin_ShouldAnswerWildcardWithoutVary()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin()));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe("*");
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: A credentialed policy should echo the origin, never '*', and allow credentials")]
    public async Task InvokeAsync_CredentialedPolicy_ShouldEchoOriginAndAllowCredentials()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .SetIsOriginAllowed(origin => origin.StartsWith("https://", StringComparison.Ordinal))
            .AllowCredentials()));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(App);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowCredentials).ShouldBe("true");
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBe("Origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: A denied origin should get no CORS header but still Vary: Origin, and the request should continue")]
    public async Task InvokeAsync_DeniedOrigin_ShouldWriteNoCorsHeadersButVaryByOrigin()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: Other);

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .AllowCredentials()
            .WithExposedHeaders("ETag")));

        // Assert — CORS never blocks a request server-side; the browser withholds the response.
        continued.ShouldBeTrue();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowCredentials).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.AccessControlExposeHeaders).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBe("Origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: Exposed headers should be listed on an allowed response")]
    public async Task InvokeAsync_ExposedHeaders_ShouldListThemOnAllowedResponse()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .WithExposedHeaders("ETag", "X-Total-Count")));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlExposeHeaders).ShouldBe("ETag, X-Total-Count");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: A request without Origin to an origin-dependent policy should only get Vary: Origin")]
    public async Task InvokeAsync_NoOriginWithListPolicy_ShouldOnlyVaryByOrigin()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get);

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App)));

        // Assert — a cache must not hand this response to a later CORS request from the allowed origin.
        continued.ShouldBeTrue();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBe("Origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: A request without Origin to an any-origin policy should still carry '*'")]
    public async Task InvokeAsync_NoOriginWithAnyOriginPolicy_ShouldStillAnswerWildcard()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get);

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .AllowAnyOrigin()
            .WithExposedHeaders("ETag")));

        // Assert — Fetch: a static '*' is sent on every response, and no Vary is used.
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe("*");
        context.ResponseHeader(HttpHeaderKey.AccessControlExposeHeaders).ShouldBe("ETag");
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: More than one Origin value should be denied")]
    public async Task InvokeAsync_MultipleOriginValues_ShouldBeDenied()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get);
        context.Request.Headers[HttpHeaderKey.Origin] = new HttpHeaderValue([App, Other]);

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App, Other)));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBe("Origin");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Actual: Vary: Origin should be appended to existing tokens, never duplicated, and never added to Vary: *")]
    [InlineData("Accept-Encoding", "Accept-Encoding, Origin")]
    [InlineData("Accept, Accept-Encoding", "Accept, Accept-Encoding, Origin")]
    [InlineData("origin", "origin")]
    [InlineData("Accept, ORIGIN", "Accept, ORIGIN")]
    [InlineData("*", "*")]
    public async Task InvokeAsync_ExistingVary_ShouldAppendWithoutClobbering(string existing, string expected)
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);
        context.Response.Headers[HttpHeaderKey.Vary] = existing;

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App)));

        // Assert
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: Headers reset downstream should be written again before the response starts")]
    public async Task InvokeAsync_DownstreamResetsResponse_ShouldReapplyHeaders()
    {
        // Arrange — the shape an exception boundary or a request timeout registered after UseCors produces.
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);

        // Act
        await CorsPipeline.InvokeAsync(
            context,
            options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).AllowCredentials()),
            downstream: ctx =>
            {
                ctx.Response.Headers.Clear();
                ctx.Response.Headers[HttpHeaderKey.Vary] = "Accept";
                ctx.Response.StatusCode = HttpStatusCode.InternalServerError;
                return Task.CompletedTask;
            });

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(App);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowCredentials).ShouldBe("true");
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBe("Accept, Origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: Headers should not be written again once the response has started")]
    public async Task InvokeAsync_ResponseStartedDownstream_ShouldNotRewriteHeaders()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);

        // Act
        await CorsPipeline.InvokeAsync(
            context,
            options => options.AddDefaultPolicy(policy => policy.WithOrigins(App)),
            downstream: ctx =>
            {
                ctx.Response.Headers.Clear();
                ctx.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(hasStarted: true));
                return Task.CompletedTask;
            });

        // Assert — the head is on the wire; a write now would have no effect, so none is made.
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: CORS headers the policy does not grant should be removed, whoever set them")]
    public async Task InvokeAsync_StaleCorsHeaders_ShouldBeReplacedByThePolicyDecision()
    {
        // Arrange — an earlier component granted credentials and exposure the policy does not.
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);
        context.Response.Headers[HttpHeaderKey.AccessControlAllowOrigin] = "*";
        context.Response.Headers[HttpHeaderKey.AccessControlAllowCredentials] = "true";
        context.Response.Headers[HttpHeaderKey.AccessControlExposeHeaders] = "X-Secret";

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App)));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(App);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowCredentials).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.AccessControlExposeHeaders).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: A handler's own CORS header should not survive a policy that denies the origin")]
    public async Task InvokeAsync_DeniedOriginWithHandlerCorsHeader_ShouldRemoveIt()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: Other);

        // Act
        await CorsPipeline.InvokeAsync(
            context,
            options => options.AddDefaultPolicy(policy => policy.WithOrigins(App)),
            downstream: ctx =>
            {
                ctx.Response.Headers[HttpHeaderKey.AccessControlAllowOrigin] = Other;
                return Task.CompletedTask;
            });

        // Assert — an endpoint that writes its own CORS headers opts out with DisableCors.
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Actual: With no policy the request should pass through untouched")]
    public async Task InvokeAsync_NoPolicy_ShouldPassThroughUntouched()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, configure: null);

        // Assert
        continued.ShouldBeTrue();
        context.Response.Headers.Count.ShouldBe(0);
    }

    // ------------------------------------------------------------------ policy selection

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Selection: An endpoint's inline policy should replace the default policy")]
    public async Task InvokeAsync_EndpointInlinePolicy_ShouldReplaceDefault()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: Other);
        CorsPolicy inline = new CorsPolicyBuilder().WithOrigins(Other).Build();

        // Act
        await CorsPipeline.InvokeAsync(
            context,
            options => options.AddDefaultPolicy(policy => policy.WithOrigins(App)),
            new FakeRouteMatchFeature(new CorsMetadata(inline)));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(Other);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Selection: An endpoint's named policy should resolve against the registered policies")]
    public async Task InvokeAsync_EndpointNamedPolicy_ShouldResolveByName()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: Other);

        // Act
        await CorsPipeline.InvokeAsync(
            context,
            options => options
                .AddDefaultPolicy(policy => policy.WithOrigins(App))
                .AddPolicy("partners", policy => policy.WithOrigins(Other)),
            new FakeRouteMatchFeature(new CorsMetadata("partners")));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(Other);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Selection: An unknown policy name should throw InvalidOperationException")]
    public async Task InvokeAsync_UnknownNamedPolicy_ShouldThrow()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);

        // Act
        Func<Task> act = () => CorsPipeline.InvokeAsync(context, configure: null, new FakeRouteMatchFeature(new CorsMetadata("missing")));

        // Assert
        (await act.ShouldThrowAsync<InvalidOperationException>()).Message.ShouldContain("'missing'");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Selection: A disabled endpoint should get no CORS header even under an any-origin default")]
    public async Task InvokeAsync_DisabledEndpoint_ShouldSkipDefaultPolicy()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: App);

        // Act
        bool continued = await CorsPipeline.InvokeAsync(
            context,
            options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin()),
            new FakeRouteMatchFeature(CorsMetadata.Disabled));

        // Assert
        continued.ShouldBeTrue();
        context.Response.Headers.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Selection: The last CORS metadata item should decide")]
    public async Task InvokeAsync_SeveralMetadataItems_ShouldApplyTheLast()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Get, origin: Other);

        // Act
        await CorsPipeline.InvokeAsync(
            context,
            options => options
                .AddPolicy("group", policy => policy.WithOrigins(App))
                .AddPolicy("route", policy => policy.WithOrigins(Other)),
            new FakeRouteMatchFeature(new CorsMetadata("group"), new CorsMetadata("route")));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(Other);
    }

    // ------------------------------------------------------------------ preflight evaluation

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: An allowed preflight should be answered 204 with the full grant, without continuing")]
    public async Task InvokeAsync_AllowedPreflight_ShouldAnswer204WithGrant()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "PUT", "content-type,x-trace");

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .WithMethods("PUT", "DELETE")
            .WithHeaders("Content-Type", "X-Trace")
            .AllowCredentials()
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        // Assert
        continued.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(App);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowCredentials).ShouldBe("true");
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowMethods).ShouldBe("PUT, DELETE");
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowHeaders).ShouldBe("Content-Type, X-Trace");
        context.ResponseHeader(HttpHeaderKey.AccessControlMaxAge).ShouldBe("600");
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBe("Origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: A preflight from a denied origin should be answered 204 without any CORS header")]
    public async Task InvokeAsync_PreflightFromDeniedOrigin_ShouldAnswerWithoutCorsHeaders()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(Other, "PUT");

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .AllowAnyMethod()
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        // Assert
        continued.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        AssertNoCorsHeaders(context);
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBe("Origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: A denied preflight should carry no CORS header, even one set before UseCors")]
    public async Task InvokeAsync_DeniedPreflightWithStaleHeaders_ShouldRemoveThem()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(Other, "PUT");
        context.Response.Headers[HttpHeaderKey.AccessControlAllowOrigin] = "*";
        context.Response.Headers[HttpHeaderKey.AccessControlAllowMethods] = "PUT";

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).WithMethods("PUT")));

        // Assert
        AssertNoCorsHeaders(context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: A method the policy does not allow should get no CORS header")]
    public async Task InvokeAsync_PreflightForDisallowedMethod_ShouldAnswerWithoutCorsHeaders()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "DELETE");

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).WithMethods("PUT")));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        AssertNoCorsHeaders(context);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Preflight: Methods should compare byte for byte, as Fetch compares them")]
    [InlineData("PATCH", true)]
    [InlineData("patch", false)]
    [InlineData("Patch", false)]
    public async Task InvokeAsync_PreflightMethodCase_ShouldMatchByteForByte(string requestedMethod, bool allowed)
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, requestedMethod);

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).WithMethods("PATCH")));

        // Assert
        (context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin) is not null).ShouldBe(allowed);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Preflight: A CORS-safelisted method should be allowed without being listed, byte for byte")]
    [InlineData("POST", true)]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("post", false)]
    public async Task InvokeAsync_PreflightForSafelistedMethod_ShouldBeAllowedWithoutListing(string requestedMethod, bool allowed)
    {
        // Arrange — a JSON POST is preflighted for its Content-Type, not for its method.
        await using CorsTestContext context = CorsTestContext.Preflight(App, requestedMethod, "content-type");

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .WithMethods("PUT")
            .WithHeaders("Content-Type")));

        // Assert
        (context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin) is not null).ShouldBe(allowed);

        if (allowed)
        {
            context.ResponseHeader(HttpHeaderKey.AccessControlAllowMethods).ShouldBe("PUT");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: Requested header names should match case-insensitively")]
    public async Task InvokeAsync_PreflightHeadersInAnotherCase_ShouldBeAllowed()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "PUT", "CONTENT-TYPE, x-TRACE");

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .WithMethods("PUT")
            .WithHeaders("X-Trace", "Content-Type")));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(App);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowHeaders).ShouldBe("X-Trace, Content-Type");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Preflight: A requested header the policy does not allow, or a malformed one, should get no CORS header")]
    [InlineData("x-trace,x-secret")]
    [InlineData("content-type")]
    [InlineData("x-trace, bad header")]
    [InlineData("x-trace,x:trace")]
    public async Task InvokeAsync_PreflightForDisallowedHeader_ShouldAnswerWithoutCorsHeaders(string requestedHeaders)
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "PUT", requestedHeaders);

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .WithMethods("PUT")
            .WithHeaders("X-Trace")));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        AssertNoCorsHeaders(context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: Empty list elements in the requested headers should be ignored")]
    public async Task InvokeAsync_PreflightHeadersWithEmptyElements_ShouldIgnoreThem()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "PUT", " , x-trace,, ");

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .WithMethods("PUT")
            .WithHeaders("X-Trace")));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(App);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: Any header should echo the requested names, Authorization included, never '*'")]
    public async Task InvokeAsync_PreflightAnyHeader_ShouldEchoRequestedHeaders()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "GET", "authorization,content-type");

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .AllowAnyHeader()
            .AllowCredentials()));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowHeaders).ShouldBe("authorization,content-type");
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowMethods).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: Any method should echo the requested method, never '*'")]
    public async Task InvokeAsync_PreflightAnyMethod_ShouldEchoRequestedMethod()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "PURGE");

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).AllowAnyMethod()));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowMethods).ShouldBe("PURGE");
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowHeaders).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: An any-origin preflight should answer '*' without Vary, and omit an unset max age")]
    public async Task InvokeAsync_PreflightAnyOrigin_ShouldAnswerWildcard()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "PUT");

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().WithMethods("PUT")));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe("*");
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowCredentials).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.AccessControlMaxAge).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.Vary).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: A fractional max age should be sent as whole seconds")]
    public async Task InvokeAsync_PreflightFractionalMaxAge_ShouldTruncateToSeconds()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "GET");

        // Act
        await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(App)
            .SetPreflightMaxAge(TimeSpan.FromSeconds(90.9))));

        // Assert
        context.ResponseHeader(HttpHeaderKey.AccessControlMaxAge).ShouldBe("90");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight: OPTIONS with Origin but no Access-Control-Request-Method should be an actual request")]
    public async Task InvokeAsync_PlainOptionsWithOrigin_ShouldBeTreatedAsActualRequest()
    {
        // Arrange
        await using CorsTestContext context = new(HttpMethod.Options, origin: App);

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App)));

        // Assert
        continued.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(App);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Preflight: A malformed or overlong requested method should not make a preflight, as in routing")]
    [InlineData("DEL ETE")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFG")]
    public async Task InvokeAsync_MalformedRequestedMethod_ShouldBeTreatedAsActualRequest(string requestedMethod)
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, requestedMethod);

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).AllowAnyMethod()));

        // Assert
        continued.ShouldBeTrue();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowMethods).ShouldBeNull();
    }

    // ------------------------------------------------------------------ preflight policy source

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight source: A preflight with no endpoint should be answered with the default policy")]
    public async Task InvokeAsync_PreflightWithoutCandidate_ShouldAnswerWithDefaultPolicy()
    {
        // Arrange — routing's 405 or 404 path: no route serves the requested method, so no handler can run.
        await using CorsTestContext context = CorsTestContext.Preflight(App, "PUT");

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App).WithMethods("PUT")));

        // Assert
        continued.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(App);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight source: A preflight with no endpoint and no default policy should continue unanswered")]
    public async Task InvokeAsync_PreflightWithoutCandidateOrDefault_ShouldContinue()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "PUT");

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddPolicy("named", policy => policy.WithOrigins(App)));

        // Assert
        continued.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        AssertNoCorsHeaders(context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight source: The candidate endpoint's policy should answer the preflight")]
    public async Task InvokeAsync_PreflightCandidate_ShouldUseCandidatePolicy()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(Other, "DELETE");
        FakeRouteMatchFeature candidate = new(new CorsMetadata(new CorsPolicyBuilder().WithOrigins(Other).WithMethods("DELETE").Build()))
        {
            IsPreflight = true,
        };

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.WithOrigins(App)), candidate);

        // Assert
        continued.ShouldBeFalse();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(Other);
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowMethods).ShouldBe("DELETE");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight source: A candidate that disables CORS should leave the preflight unanswered")]
    public async Task InvokeAsync_PreflightCandidateDisabled_ShouldContinueWithoutAnswering()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(App, "DELETE");
        FakeRouteMatchFeature candidate = new(CorsMetadata.Disabled) { IsPreflight = true };

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod()), candidate);

        // Assert
        continued.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        AssertNoCorsHeaders(context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight source: An OPTIONS-only route should own its path's preflights")]
    public async Task InvokeAsync_PreflightToOptionsOnlyRoute_ShouldLeaveItToTheRoute()
    {
        // Arrange — routing matched the OPTIONS request itself; the DELETE endpoint's policy is unknown here.
        await using CorsTestContext context = CorsTestContext.Preflight(App, "DELETE");
        FakeRouteMatchFeature optionsRoute = new() { Route = new Route(HttpMethod.Options, "/items") };

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod()), optionsRoute);

        // Assert
        continued.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        AssertNoCorsHeaders(context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight source: A route that serves both OPTIONS and the requested method should answer with its policy")]
    public async Task InvokeAsync_PreflightToRouteServingRequestedMethod_ShouldAnswerWithRoutePolicy()
    {
        // Arrange
        await using CorsTestContext context = CorsTestContext.Preflight(Other, "DELETE");
        CorsPolicy routePolicy = new CorsPolicyBuilder().WithOrigins(Other).WithMethods("DELETE").Build();
        FakeRouteMatchFeature route = new(new CorsMetadata(routePolicy))
        {
            Route = new Route([HttpMethod.Options, HttpMethod.Delete], "/items"),
        };

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, configure: null, route);

        // Assert
        continued.ShouldBeFalse();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(Other);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - Preflight source: A route serves the requested method only byte for byte (RFC 9110 §9.1)")]
    [InlineData("PATCH", true)]
    [InlineData("patch", false)]
    [InlineData("Patch", false)]
    public async Task InvokeAsync_PreflightToRouteServingMethodInAnotherCase_ShouldLeaveItToTheRoute(string requestedMethod, bool answered)
    {
        // Arrange — the route's policy grants any method, so only the route's method set decides. The actual
        // 'patch' request would not match this PATCH route, so its policy must not approve the preflight.
        await using CorsTestContext context = CorsTestContext.Preflight(Other, requestedMethod);
        CorsPolicy routePolicy = new CorsPolicyBuilder().WithOrigins(Other).AllowAnyMethod().Build();
        FakeRouteMatchFeature route = new(new CorsMetadata(routePolicy))
        {
            Route = new Route([HttpMethod.Options, HttpMethod.Patch], "/items"),
        };

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, configure: null, route);

        // Assert
        continued.ShouldBe(!answered);

        if (answered)
        {
            context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBe(Other);
            context.ResponseHeader(HttpHeaderKey.AccessControlAllowMethods).ShouldBe(requestedMethod);
        }
        else
        {
            AssertNoCorsHeaders(context);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Preflight source: A route that accepts any method should answer with its policy")]
    public async Task InvokeAsync_PreflightToAnyMethodRoute_ShouldAnswerWithRoutePolicy()
    {
        // Arrange — a gateway-style catch-all serves every method, the preflight's actual method included.
        await using CorsTestContext context = CorsTestContext.Preflight(Other, "PATCH");
        CorsPolicy routePolicy = new CorsPolicyBuilder().WithOrigins(Other).WithMethods("PATCH").Build();
        FakeRouteMatchFeature route = new(new CorsMetadata(routePolicy))
        {
            Route = new Route(Array.Empty<HttpMethod>(), "/proxy/{**path}"),
        };

        // Act
        bool continued = await CorsPipeline.InvokeAsync(context, configure: null, route);

        // Assert
        continued.ShouldBeFalse();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowMethods).ShouldBe("PATCH");
    }

    private static void AssertNoCorsHeaders(CorsTestContext context)
    {
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowCredentials).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowMethods).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.AccessControlAllowHeaders).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.AccessControlMaxAge).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.AccessControlExposeHeaders).ShouldBeNull();
    }
}
