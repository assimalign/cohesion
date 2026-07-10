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

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - UseRouting: installs a resolvable route-match feature on a match")]
    public async Task UseRouting_OnMatch_ShouldInstallResolvableFeatureAndInvokeHandler()
    {
        // Arrange
        AuthMetadata auth = new("admin");
        RecordingRouterRouteHandler handler = new();
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");

        bool downstreamRan = false;
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting().Map(new Route(HttpMethod.Get, "/users/{id:int}", handler, new RouterRouteMetadataCollection(auth)));
        app.Use((ctx, next) =>
        {
            downstreamRan = true;
            return next.Invoke(ctx);
        });

        // Act
        await app.ExecuteAsync(context);

        // Assert
        handler.WasInvoked.ShouldBeTrue();
        downstreamRan.ShouldBeFalse(); // a match is terminal
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

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - UseRouting: short-circuits 405 with no route-match feature")]
    public async Task UseRouting_OnMethodMismatch_ShouldShortCircuitWithoutFeature()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Post, "/users/42");

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
        downstreamRan.ShouldBeFalse(); // a 405 short-circuits
        context.Response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        context.GetRouteMatch().ShouldBeNull(); // no match feature installed on a 405
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
