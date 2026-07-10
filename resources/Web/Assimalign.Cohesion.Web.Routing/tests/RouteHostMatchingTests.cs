using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;
using Shouldly;
using Xunit;
using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RouteHostMatchingTests
{
    private static Route CreateRoute(HttpMethod method, string pattern, params string[] hosts)
    {
        return hosts.Length == 0
            ? new Route(method, pattern)
            : new Route(method, pattern, new RecordingRouterRouteHandler(), new RouterRouteMetadataCollection(new RouteHostMetadata(hosts)));
    }

    private static TestHttpContext CreateContext(HttpMethod method, string path, string host)
    {
        TestHttpContext context = TestHttpContext.Create(method, path);
        context.Request.Host = new HttpHost(host);
        return context;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Host-constrained route matches its declared host")]
    public void Match_HostConstrainedRoute_ShouldMatchDeclaredHost()
    {
        // Arrange
        Route route = CreateRoute(HttpMethod.Get, "/api/data", "api.example.com");
        Router router = new(route);
        TestHttpContext context = CreateContext(HttpMethod.Get, "/api/data", "api.example.com");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(route);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Non-matching host falls through to an unconstrained candidate")]
    public void Match_NonMatchingHost_ShouldFallThroughToUnconstrainedCandidate()
    {
        // Arrange — same pattern twice: one host-constrained, one open.
        Route constrained = CreateRoute(HttpMethod.Get, "/api/data", "api.example.com");
        Route unconstrained = CreateRoute(HttpMethod.Get, "/api/data");
        Router router = new(new IRouterRoute[] { constrained, unconstrained });

        // Act
        RouteMatch matchingHost = router.Match(CreateContext(HttpMethod.Get, "/api/data", "api.example.com"));
        RouteMatch otherHost = router.Match(CreateContext(HttpMethod.Get, "/api/data", "www.example.com"));

        // Assert — the declared host selects the constrained route; any other host falls through.
        matchingHost.Status.ShouldBe(RouteMatchStatus.Matched);
        matchingHost.Route.ShouldBeSameAs(constrained);

        otherHost.Status.ShouldBe(RouteMatchStatus.Matched);
        otherHost.Route.ShouldBeSameAs(unconstrained);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Host-constrained route outranks an unconstrained tie regardless of registration order")]
    public void Match_HostConstrainedTie_ShouldOutrankUnconstrainedRegardlessOfRegistrationOrder()
    {
        // Arrange — the unconstrained route is registered FIRST; the host-constrained tie must still win.
        Route unconstrained = CreateRoute(HttpMethod.Get, "/api/data");
        Route constrained = CreateRoute(HttpMethod.Get, "/api/data", "api.example.com");
        Router router = new(new IRouterRoute[] { unconstrained, constrained });
        TestHttpContext context = CreateContext(HttpMethod.Get, "/api/data", "api.example.com");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(constrained);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Path precedence still outranks host specificity")]
    public void Match_HigherPathPrecedence_ShouldOutrankHostConstrainedTie()
    {
        // Arrange — host rank only breaks ties WITHIN equal path precedence; a more specific
        // path (literal) still beats a host-constrained parameter route.
        Route constrainedParameter = CreateRoute(HttpMethod.Get, "/api/{id}", "api.example.com");
        Route literal = CreateRoute(HttpMethod.Get, "/api/status");
        Router router = new(new IRouterRoute[] { constrainedParameter, literal });
        TestHttpContext context = CreateContext(HttpMethod.Get, "/api/status", "api.example.com");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(literal);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Wildcard subdomain matches subdomains but not the apex")]
    public void Match_WildcardSubdomain_ShouldMatchSubdomainsButNotApex()
    {
        // Arrange
        Route wildcard = CreateRoute(HttpMethod.Get, "/", "*.example.com");
        Router router = new(wildcard);

        // Act
        RouteMatch subdomain = router.Match(CreateContext(HttpMethod.Get, "/", "api.example.com"));
        RouteMatch deepSubdomain = router.Match(CreateContext(HttpMethod.Get, "/", "a.b.example.com"));
        RouteMatch apex = router.Match(CreateContext(HttpMethod.Get, "/", "example.com"));

        // Assert
        subdomain.Status.ShouldBe(RouteMatchStatus.Matched);
        deepSubdomain.Status.ShouldBe(RouteMatchStatus.Matched);
        apex.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Port constraint requires the explicit request port")]
    public void Match_PortConstraint_ShouldRequireExplicitRequestPort()
    {
        // Arrange
        Route portBound = CreateRoute(HttpMethod.Get, "/", "internal.example.com:8080");
        Router router = new(portBound);

        // Act
        RouteMatch explicitPort = router.Match(CreateContext(HttpMethod.Get, "/", "internal.example.com:8080"));
        RouteMatch wrongPort = router.Match(CreateContext(HttpMethod.Get, "/", "internal.example.com:9090"));
        RouteMatch impliedPort = router.Match(CreateContext(HttpMethod.Get, "/", "internal.example.com"));

        // Assert
        explicitPort.Status.ShouldBe(RouteMatchStatus.Matched);
        wrongPort.Status.ShouldBe(RouteMatchStatus.NoMatch);
        impliedPort.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Host comparison is case-insensitive")]
    public void Match_HostCasingDiffers_ShouldStillMatch()
    {
        // Arrange
        Route route = CreateRoute(HttpMethod.Get, "/", "api.example.com");
        Router router = new(route);
        TestHttpContext context = CreateContext(HttpMethod.Get, "/", "API.EXAMPLE.COM");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(route);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: IPv6 literal hosts are matched with and without ports")]
    public void Match_IPv6LiteralHost_ShouldMatchBracketedForms()
    {
        // Arrange
        Route route = CreateRoute(HttpMethod.Get, "/", "[::1]:8080");
        Router router = new(route);

        // Act
        RouteMatch matching = router.Match(CreateContext(HttpMethod.Get, "/", "[::1]:8080"));
        RouteMatch wrongPort = router.Match(CreateContext(HttpMethod.Get, "/", "[::1]:9090"));
        RouteMatch noPort = router.Match(CreateContext(HttpMethod.Get, "/", "[::1]"));

        // Assert
        matching.Status.ShouldBe(RouteMatchStatus.Matched);
        wrongPort.Status.ShouldBe(RouteMatchStatus.NoMatch);
        noPort.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Host mismatch yields 404, not 405, and stays out of Allow")]
    public void Match_HostMismatchWithWrongMethod_ShouldNotContributeToMethodNotAllowed()
    {
        // Arrange — a POST-only route bound to another host: a GET from the wrong host must be a
        // clean no-match (404), not a 405 advertising POST for a host that cannot reach the route.
        Route postOnly = CreateRoute(HttpMethod.Post, "/api/data", "api.example.com");
        Router router = new(postOnly);
        TestHttpContext context = CreateContext(HttpMethod.Get, "/api/data", "www.example.com");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Matching host with wrong method still yields 405")]
    public void Match_MatchingHostWithWrongMethod_ShouldReturnMethodNotAllowed()
    {
        // Arrange
        Route postOnly = CreateRoute(HttpMethod.Post, "/api/data", "api.example.com");
        Router router = new(postOnly);
        TestHttpContext context = CreateContext(HttpMethod.Get, "/api/data", "api.example.com");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.MethodNotAllowed);
        match.AllowedMethods.ShouldContain(HttpMethod.Post);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Multiple host constraints are OR-combined")]
    public void Match_MultipleHostConstraints_ShouldMatchAnyDeclaredHost()
    {
        // Arrange
        Route route = CreateRoute(HttpMethod.Get, "/", "api.example.com", "*.internal.example.com");
        Router router = new(route);

        // Act
        RouteMatch exact = router.Match(CreateContext(HttpMethod.Get, "/", "api.example.com"));
        RouteMatch wildcard = router.Match(CreateContext(HttpMethod.Get, "/", "svc.internal.example.com"));
        RouteMatch neither = router.Match(CreateContext(HttpMethod.Get, "/", "www.example.com"));

        // Assert
        exact.Status.ShouldBe(RouteMatchStatus.Matched);
        wildcard.Status.ShouldBe(RouteMatchStatus.Matched);
        neither.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Empty host metadata behaves as unconstrained")]
    public void Match_EmptyHostMetadata_ShouldBehaveAsUnconstrained()
    {
        // Arrange — empty list = no constraint: matches any host and does NOT take the
        // host-constrained precedence rank over a genuinely constrained tie.
        Route emptyMetadata = new(
            HttpMethod.Get,
            "/api/data",
            new RecordingRouterRouteHandler(),
            new RouterRouteMetadataCollection(new RouteHostMetadata()));
        Route constrained = CreateRoute(HttpMethod.Get, "/api/data", "api.example.com");
        Router router = new(new IRouterRoute[] { emptyMetadata, constrained });

        // Act
        RouteMatch anyHost = router.Match(CreateContext(HttpMethod.Get, "/api/data", "www.example.com"));
        RouteMatch declaredHost = router.Match(CreateContext(HttpMethod.Get, "/api/data", "api.example.com"));

        // Assert
        anyHost.Status.ShouldBe(RouteMatchStatus.Matched);
        anyHost.Route.ShouldBeSameAs(emptyMetadata);

        declaredHost.Status.ShouldBe(RouteMatchStatus.Matched);
        declaredHost.Route.ShouldBeSameAs(constrained);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Last-registered host metadata wins over earlier declarations")]
    public void Match_LayeredHostMetadata_ShouldUseLastRegisteredDeclaration()
    {
        // Arrange — group-level metadata first, endpoint-level second: the endpoint-level
        // declaration overrides (last-wins), it does not combine.
        Route route = new(
            HttpMethod.Get,
            "/api/data",
            new RecordingRouterRouteHandler(),
            new RouterRouteMetadataCollection(
                new RouteHostMetadata("group.example.com"),
                new RouteHostMetadata("endpoint.example.com")));
        Router router = new(route);

        // Act
        RouteMatch endpointHost = router.Match(CreateContext(HttpMethod.Get, "/api/data", "endpoint.example.com"));
        RouteMatch groupHost = router.Match(CreateContext(HttpMethod.Get, "/api/data", "group.example.com"));

        // Assert
        endpointHost.Status.ShouldBe(RouteMatchStatus.Matched);
        groupHost.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Multi-tenant hosts select their own route for the same pattern")]
    public void Match_TwoHostConstrainedRoutes_ShouldSelectByRequestHost()
    {
        // Arrange — the motivating multi-tenant scenario: one pattern, one route per tenant host.
        Route tenantA = CreateRoute(HttpMethod.Get, "/dashboard", "a.example.com");
        Route tenantB = CreateRoute(HttpMethod.Get, "/dashboard", "b.example.com");
        Router router = new(new IRouterRoute[] { tenantA, tenantB });

        // Act
        RouteMatch first = router.Match(CreateContext(HttpMethod.Get, "/dashboard", "a.example.com"));
        RouteMatch second = router.Match(CreateContext(HttpMethod.Get, "/dashboard", "b.example.com"));
        RouteMatch unknown = router.Match(CreateContext(HttpMethod.Get, "/dashboard", "c.example.com"));

        // Assert
        first.Route.ShouldBeSameAs(tenantA);
        second.Route.ShouldBeSameAs(tenantB);
        unknown.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteAsync: Host selection dispatches the matching handler")]
    public async Task RouteAsync_HostConstrainedRoutes_ShouldInvokeMatchingHandler()
    {
        // Arrange
        RecordingRouterRouteHandler adminHandler = new();
        RecordingRouterRouteHandler publicHandler = new();
        Route admin = new(
            HttpMethod.Get,
            "/",
            adminHandler,
            new RouterRouteMetadataCollection(new RouteHostMetadata("admin.internal")));
        Route open = new(HttpMethod.Get, "/", publicHandler);
        Router router = new(new IRouterRoute[] { open, admin });
        TestHttpContext context = CreateContext(HttpMethod.Get, "/", "admin.internal");

        // Act
        await router.RouteAsync(context, CancellationToken.None);

        // Assert
        adminHandler.WasInvoked.ShouldBeTrue();
        publicHandler.WasInvoked.ShouldBeFalse();
    }
}
