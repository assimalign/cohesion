using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

/// <summary>
/// The match/dispatch split (#1054): <c>UseRouting</c> selects the endpoint and calls <c>next</c>, and
/// the pipeline's terminal runs it. Covers pipeline ordering, HEAD, 405, CORS-preflight candidate
/// resolution, stale-endpoint clearing, and the fail-closed check for endpoint metadata that names a
/// required middleware.
/// </summary>
public class EndpointSelectionTests
{
    private sealed class PolicyMetadata : IRouteMiddlewareMetadata
    {
        public PolicyMetadata(string? requiredMiddleware) => RequiredMiddleware = requiredMiddleware;

        public string? RequiredMiddleware { get; }
    }

    // ------------------------------------------------------------------ ordering

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: Middleware ahead of UseRouting sees no endpoint; middleware after it does")]
    public async Task UseRouting_Ordering_ShouldPublishEndpointOnlyToLaterMiddleware()
    {
        // Arrange
        RecordingRouterRouteHandler handler = new();
        bool beforeSawMatch = true;
        bool afterSawMatch = false;
        TestWebApplication app = new();
        app.AddRouting();
        app.Use((ctx, next) => { beforeSawMatch = ctx.GetRouteMatch() is not null; return next.Invoke(ctx); });
        app.UseRouting().Map(new Route(HttpMethod.Get, "/items", handler));
        app.Use((ctx, next) => { afterSawMatch = ctx.GetRouteMatch() is not null; return next.Invoke(ctx); });

        // Act
        await app.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/items"));

        // Assert
        beforeSawMatch.ShouldBeFalse();
        afterSawMatch.ShouldBeTrue();
        handler.InvocationCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: A middleware after UseRouting that short-circuits keeps the endpoint from running")]
    public async Task UseRouting_WhenLaterMiddlewareShortCircuits_ShouldNotRunEndpoint()
    {
        // Arrange
        RecordingRouterRouteHandler handler = new();
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/items");
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/items", handler));
        app.Use((ctx, _) =>
        {
            ctx.Response.StatusCode = HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        });

        // Act
        await app.ExecuteAsync(context);

        // Assert
        handler.WasInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: HEAD is served by the GET route at the terminal")]
    public async Task UseRouting_OnHeadToGetRoute_ShouldRunGetHandler()
    {
        // Arrange
        RecordingRouterRouteHandler handler = new();
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Head, "/items");
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/items", handler));

        // Act
        await app.ExecuteAsync(context);

        // Assert
        handler.WasInvoked.ShouldBeTrue();
        context.GetRouteMatch().ShouldNotBeNull();
        context.GetRouteMatch()!.IsPreflight.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: No match clears an endpoint an earlier selection published")]
    public async Task UseRouting_OnNoMatch_ShouldClearStaleEndpoint()
    {
        // Arrange — an earlier middleware selected a route; routing then finds nothing for the request.
        RecordingRouterRouteHandler stale = new();
        TestWebApplication app = new();
        app.AddRouting();
        app.Use((ctx, next) =>
        {
            ctx.SetRouteMatch(new Route(HttpMethod.Get, "/elsewhere", stale), new RouteValueDictionary());
            return next.Invoke(ctx);
        });
        app.UseRouting().Map(new Route(HttpMethod.Get, "/items"));

        // Act
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/nope");
        await app.ExecuteAsync(context);

        // Assert
        stale.WasInvoked.ShouldBeFalse();
        context.GetRouteMatch().ShouldBeNull();
    }

    // ------------------------------------------------------------------ CORS preflight

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: A CORS preflight publishes the candidate for the requested method without running it")]
    public async Task UseRouting_OnPreflight_ShouldPublishCandidateWithoutRunningIt()
    {
        // Arrange
        RecordingRouterRouteHandler handler = new();
        PolicyMetadata policy = new(requiredMiddleware: null);
        TestHttpContext context = CreatePreflight("/items/7", requestedMethod: "DELETE");
        IRouteMatchFeature? seen = null;
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Delete, "/items/{id:int}", handler, new RouterRouteMetadataCollection(policy)));
        app.Use((ctx, next) => { seen = ctx.GetRouteMatch(); return next.Invoke(ctx); });

        // Act
        await app.ExecuteAsync(context);

        // Assert — the candidate and its metadata are readable downstream; it never runs for the preflight,
        // and an unhandled preflight is answered as the plain OPTIONS request it is.
        seen.ShouldNotBeNull();
        seen!.IsPreflight.ShouldBeTrue();
        seen.Values!["id"].ShouldBe(7);
        seen.Metadata.GetMetadata<PolicyMetadata>().ShouldBeSameAs(policy);
        handler.WasInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        context.Response.Headers[HttpHeaderKey.Allow].ToString().ShouldBe("DELETE");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: An explicit OPTIONS route answers a preflight-shaped request itself")]
    public async Task UseRouting_OnPreflightWithOptionsRoute_ShouldRunOptionsRoute()
    {
        // Arrange
        RecordingRouterRouteHandler options = new();
        RecordingRouterRouteHandler delete = new();
        TestHttpContext context = CreatePreflight("/items", requestedMethod: "DELETE");
        TestWebApplication app = new();
        app.AddRouting();
        IRouterBuilder routes = app.UseRouting();
        routes.Map(new Route(HttpMethod.Options, "/items", options));
        routes.Map(new Route(HttpMethod.Delete, "/items", delete));

        // Act
        await app.ExecuteAsync(context);

        // Assert
        options.WasInvoked.ShouldBeTrue();
        delete.WasInvoked.ShouldBeFalse();
        context.GetRouteMatch()!.IsPreflight.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: A preflight with no candidate for the requested method is a plain 405")]
    [InlineData("PUT")]       // no route accepts PUT on the path
    [InlineData("DEL ETE")]   // not a method token
    public async Task UseRouting_OnPreflightWithoutCandidate_ShouldAnswer405WithoutMatch(string requestedMethod)
    {
        // Arrange
        TestHttpContext context = CreatePreflight("/items", requestedMethod);
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/items"));

        // Act
        await app.ExecuteAsync(context);

        // Assert
        context.GetRouteMatch().ShouldBeNull();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        context.Response.Headers[HttpHeaderKey.Allow].ToString().ShouldBe("GET, HEAD");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: OPTIONS without CORS headers is not a preflight")]
    public async Task UseRouting_OnPlainOptions_ShouldNotResolveCandidate()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Options, "/items");
        context.Request.Headers[HttpHeaderKey.AccessControlRequestMethod] = "GET"; // no Origin
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/items"));

        // Act
        await app.ExecuteAsync(context);

        // Assert
        context.GetRouteMatch().ShouldBeNull();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    // ------------------------------------------------------------------ required middleware

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: Metadata naming a middleware that never ran fails the request instead of running the endpoint")]
    public async Task Dispatch_WithUnacknowledgedRequiredMiddleware_ShouldThrowAndNotRunEndpoint()
    {
        // Arrange
        RecordingRouterRouteHandler handler = new();
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(
            HttpMethod.Get, "/limited", handler, new RouterRouteMetadataCollection(new PolicyMetadata("UseRateLimiting"))));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => app.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/limited")));

        // Assert
        handler.WasInvoked.ShouldBeFalse();
        exception.Message.ShouldContain("UseRateLimiting()", Case.Sensitive);
        exception.Message.ShouldContain("GET /limited", Case.Sensitive);
        exception.Message.ShouldContain(nameof(PolicyMetadata), Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: An acknowledged required middleware lets the endpoint run")]
    public async Task Dispatch_WithAcknowledgedRequiredMiddleware_ShouldRunEndpoint()
    {
        // Arrange
        RecordingRouterRouteHandler handler = new();
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(
            HttpMethod.Get, "/limited", handler, new RouterRouteMetadataCollection(new PolicyMetadata("UseRateLimiting"))));
        app.Use((ctx, next) =>
        {
            ctx.AcknowledgeEndpointMiddleware("UseRateLimiting");
            return next.Invoke(ctx);
        });

        // Act
        await app.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/limited"));

        // Assert
        handler.WasInvoked.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: Metadata that names no middleware places no requirement")]
    public async Task Dispatch_WithNullRequiredMiddleware_ShouldRunEndpoint()
    {
        // Arrange
        RecordingRouterRouteHandler handler = new();
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(
            HttpMethod.Get, "/open", handler, new RouterRouteMetadataCollection(new PolicyMetadata(requiredMiddleware: null))));

        // Act
        await app.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/open"));

        // Assert
        handler.WasInvoked.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: A requirement that a later item of the same type replaces places no requirement")]
    public async Task Dispatch_WithSupersededRequiredMiddleware_ShouldRunEndpoint()
    {
        // Arrange — a group-level policy the route disables: the last item is what consumers apply.
        RecordingRouterRouteHandler handler = new();
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(
            HttpMethod.Get,
            "/exempt",
            handler,
            new RouterRouteMetadataCollection(new PolicyMetadata("UseRateLimiting"), new PolicyMetadata(requiredMiddleware: null))));

        // Act
        await app.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/exempt"));

        // Assert
        handler.WasInvoked.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Endpoint: A requirement that replaces a disabled item still fails closed")]
    public async Task Dispatch_WithRequirementAfterDisabledItem_ShouldThrow()
    {
        // Arrange — a route re-enabling what its group disabled.
        RecordingRouterRouteHandler handler = new();
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(
            HttpMethod.Get,
            "/limited",
            handler,
            new RouterRouteMetadataCollection(new PolicyMetadata(requiredMiddleware: null), new PolicyMetadata("UseRateLimiting"))));

        // Act
        await Should.ThrowAsync<InvalidOperationException>(
            () => app.ExecuteAsync(TestHttpContext.Create(HttpMethod.Get, "/limited")));

        // Assert
        handler.WasInvoked.ShouldBeFalse();
    }

    private static TestHttpContext CreatePreflight(string path, string requestedMethod)
    {
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Options, path);
        context.Request.Headers[HttpHeaderKey.Origin] = "https://app.example";
        context.Request.Headers[HttpHeaderKey.AccessControlRequestMethod] = requestedMethod;
        return context;
    }
}
