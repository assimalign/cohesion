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
/// Verifies that the real <c>AddRouting()</c> / <c>UseRouting()</c> chain dispatches through the
/// per-application router and that the route-match state it installs (via the #150 Features-based
/// <c>SetRouteMatch</c>) is resolvable downstream. Composed over <see cref="TestWebApplication"/>,
/// which mirrors production feature seeding.
/// </summary>
public class UseRoutingMiddlewareTests
{
    private sealed class AuthMetadata
    {
        public AuthMetadata(string policy) => Policy = policy;

        public string Policy { get; }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - UseRouting: publishes the match to downstream middleware, and the terminal runs the handler after them")]
    public async Task UseRouting_OnMatch_ShouldPublishMatchToDownstreamThenRunHandlerAtTerminal()
    {
        // Arrange
        AuthMetadata auth = new("admin");
        RecordingRouterRouteHandler handler = new();
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");

        bool downstreamRan = false;
        bool handlerRanBeforeDownstream = true;
        AuthMetadata? metadataSeenDownstream = null;
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/users/{id:int}", handler, new RouterRouteMetadataCollection(auth)));
        app.Use((ctx, next) =>
        {
            downstreamRan = true;
            handlerRanBeforeDownstream = handler.WasInvoked;
            metadataSeenDownstream = ctx.GetEndpointMetadata<AuthMetadata>();
            return next.Invoke(ctx);
        });

        // Act
        await app.ExecuteAsync(context);

        // Assert — routing is non-terminal (#1054): downstream middleware runs with the endpoint known,
        // and the handler runs at the terminal, after it.
        downstreamRan.ShouldBeTrue();
        handlerRanBeforeDownstream.ShouldBeFalse();
        metadataSeenDownstream.ShouldBeSameAs(auth);
        handler.WasInvoked.ShouldBeTrue();
        context.TryGetRoute(out IRouterRoute? matched).ShouldBeTrue();
        matched.ShouldNotBeNull();
        context.TryGetRouteValues(out RouteValueDictionary? values).ShouldBeTrue();
        values!["id"].ShouldBe(42); // typed conversion flows through the match feature
        context.GetEndpointMetadata<AuthMetadata>().ShouldBeSameAs(auth);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - UseRouting: falls through with no feature when nothing matches")]
    public async Task UseRouting_OnNoMatch_ShouldFallThroughWithoutFeature()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/nope");

        bool downstreamRan = false;
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/users/{id:int}"));
        app.Use((ctx, next) =>
        {
            downstreamRan = true;
            return next.Invoke(ctx);
        });

        // Act
        await app.ExecuteAsync(context);

        // Assert
        downstreamRan.ShouldBeTrue();
        context.GetRouteMatch().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - UseRouting: answers 405 at the terminal, after downstream middleware, with no route-match feature")]
    public async Task UseRouting_OnMethodMismatch_ShouldAnswer405AtTerminalWithoutFeature()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Post, "/users/42");

        bool downstreamRan = false;
        bool downstreamSawRouteMatch = true;
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/users/{id:int}"));
        app.Use((ctx, next) =>
        {
            downstreamRan = true;
            downstreamSawRouteMatch = ctx.GetRouteMatch() is not null;
            return next.Invoke(ctx);
        });

        // Act
        await app.ExecuteAsync(context);

        // Assert
        downstreamRan.ShouldBeTrue(); // routing never short-circuits (#1054)
        downstreamSawRouteMatch.ShouldBeFalse(); // a 405 selects no route, so no endpoint metadata
        context.Response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        context.Response.Headers[HttpHeaderKey.Allow].ToString().ShouldBe("GET, HEAD");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - UseRouting: throws when AddRouting was not called")]
    public void UseRouting_WithoutAddRouting_ShouldThrow()
    {
        // Arrange
        TestWebApplication app = new();

        // Act & Assert — UseRouting must resolve the feature AddRouting registers.
        Should.Throw<InvalidOperationException>(() => app.UseRouting());
    }
}
