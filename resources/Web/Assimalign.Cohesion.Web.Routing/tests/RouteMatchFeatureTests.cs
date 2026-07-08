using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RouteMatchFeatureTests
{
    private sealed class AuthMetadata
    {
        public AuthMetadata(string policy) => Policy = policy;

        public string Policy { get; }
    }

    private static Route CreateRoute(RouterRouteMetadataCollection? metadata = null)
    {
        return new Route(
            HttpMethod.Get,
            "/users/{id:int}",
            new RecordingRouterRouteHandler(),
            metadata ?? RouterRouteMetadataCollection.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteMatch: SetRouteMatch installs a typed feature")]
    public void SetRouteMatch_ShouldInstallTypedFeature()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");
        Route route = CreateRoute();
        RouteValueDictionary values = new() { ["id"] = "42" };

        // Act
        context.SetRouteMatch(route, values);

        // Assert
        IRouteMatchFeature? feature = context.Features.Get<IRouteMatchFeature>();
        feature.ShouldNotBeNull();
        feature!.Route.ShouldBeSameAs(route);
        feature.Values.ShouldBeSameAs(values);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteMatch: SetRouteMatch does not use the Items bag")]
    public void SetRouteMatch_ShouldNotStoreInItems()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");
        Route route = CreateRoute();
        RouteValueDictionary values = new() { ["id"] = "42" };

        // Act
        context.SetRouteMatch(route, values);

        // Assert
        context.Items.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteMatch: TryGetRoute returns the matched route")]
    public void TryGetRoute_AfterMatch_ShouldReturnRoute()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");
        Route route = CreateRoute();
        context.SetRouteMatch(route, new RouteValueDictionary());

        // Act
        bool found = context.TryGetRoute(out IRouterRoute? matched);

        // Assert
        found.ShouldBeTrue();
        matched.ShouldBeSameAs(route);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteMatch: TryGetRouteValues returns the captured values")]
    public void TryGetRouteValues_AfterMatch_ShouldReturnValues()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");
        RouteValueDictionary values = new() { ["id"] = "42" };
        context.SetRouteMatch(CreateRoute(), values);

        // Act
        bool found = context.TryGetRouteValues(out RouteValueDictionary? matched);

        // Assert
        found.ShouldBeTrue();
        matched.ShouldBeSameAs(values);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteMatch: TryGetRoute returns false without a match")]
    public void TryGetRoute_WithoutMatch_ShouldReturnFalse()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");

        // Act
        bool found = context.TryGetRoute(out IRouterRoute? matched);

        // Assert
        found.ShouldBeFalse();
        matched.ShouldBeNull();
        context.GetRouteMatch().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteMatch: GetEndpointMetadata surfaces the matched route metadata")]
    public void GetEndpointMetadata_AfterMatch_ShouldReturnRouteMetadata()
    {
        // Arrange
        AuthMetadata auth = new("admin");
        Route route = CreateRoute(new RouterRouteMetadataCollection(auth));
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");
        context.SetRouteMatch(route, new RouteValueDictionary());

        // Act
        IRouterRouteMetadataCollection metadata = context.GetEndpointMetadata();
        AuthMetadata? resolved = context.GetEndpointMetadata<AuthMetadata>();

        // Assert
        metadata.Count.ShouldBe(1);
        resolved.ShouldBeSameAs(auth);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteMatch: GetEndpointMetadata returns empty without a match")]
    public void GetEndpointMetadata_WithoutMatch_ShouldReturnEmpty()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");

        // Act & Assert
        context.GetEndpointMetadata().ShouldBeSameAs(RouterRouteMetadataCollection.Empty);
        context.GetEndpointMetadata<AuthMetadata>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteMatch: SetRouteMatch replaces a previous match")]
    public void SetRouteMatch_Twice_ShouldReplacePreviousMatch()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");
        Route first = CreateRoute();
        Route second = CreateRoute();

        // Act
        context.SetRouteMatch(first, new RouteValueDictionary());
        context.SetRouteMatch(second, new RouteValueDictionary());

        // Assert
        context.GetRouteMatch()!.Route.ShouldBeSameAs(second);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteMatch: Router installs the feature and invokes the handler")]
    public async Task RouteAsync_OnMatch_ShouldInstallFeatureAndInvokeHandler()
    {
        // Arrange
        AuthMetadata auth = new("admin");
        RecordingRouterRouteHandler handler = new();
        Route route = new(HttpMethod.Get, "/users/{id:int}", handler, new RouterRouteMetadataCollection(auth));
        Router router = new(route);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/users/42");

        // Act
        await router.RouteAsync(context);

        // Assert
        handler.InvocationCount.ShouldBe(1);
        context.GetEndpointMetadata<AuthMetadata>().ShouldBeSameAs(auth);
        context.TryGetRouteValues(out RouteValueDictionary? values).ShouldBeTrue();
        values!["id"].ShouldBe(42);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteMatch: Surfaces the precedence-selected route's metadata among competing routes")]
    public async Task RouteAsync_OnCompetingRoutes_ShouldSurfacePrecedenceWinnerMetadata()
    {
        // Arrange — register the lower-precedence parameter route FIRST so a naive insertion-order
        // matcher would surface the wrong metadata; precedence must still pick the literal route.
        AuthMetadata literalMetadata = new("literal");
        AuthMetadata parameterMetadata = new("parameter");
        Route parameterRoute = new(HttpMethod.Get, "/api/{name}", new RecordingRouterRouteHandler(), new RouterRouteMetadataCollection(parameterMetadata));
        Route literalRoute = new(HttpMethod.Get, "/api/status", new RecordingRouterRouteHandler(), new RouterRouteMetadataCollection(literalMetadata));
        Router router = new(new IRouterRoute[] { parameterRoute, literalRoute });
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/api/status");

        // Act
        await router.RouteAsync(context);

        // Assert — the literal route wins on precedence, so its metadata is what surfaces.
        context.TryGetRoute(out IRouterRoute? matched).ShouldBeTrue();
        matched.ShouldBeSameAs(literalRoute);
        context.GetEndpointMetadata<AuthMetadata>().ShouldBeSameAs(literalMetadata);
    }
}
