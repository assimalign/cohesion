using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;
using Shouldly;
using Xunit;
using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RouterTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Literal beats parameter registered first")]
    public void Match_LiteralRegisteredAfterParameter_ShouldPreferLiteral()
    {
        // Arrange — the confirmed defect: a parameter route registered before a literal must not shadow it.
        Route parameterRoute = new(HttpMethod.Get, "/api/{id}");
        Route literalRoute = new(HttpMethod.Get, "/api/status");
        Router router = new(new IRouterRoute[] { parameterRoute, literalRoute });
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/api/status");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(literalRoute);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Precedence order is independent of registration order")]
    public void Match_LiteralRegisteredBeforeParameter_ShouldPreferLiteral()
    {
        // Arrange
        Route literalRoute = new(HttpMethod.Get, "/api/status");
        Route parameterRoute = new(HttpMethod.Get, "/api/{id}");
        Router router = new(new IRouterRoute[] { literalRoute, parameterRoute });
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/api/status");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(literalRoute);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Parameter route matches when no literal applies")]
    public void Match_ParameterRoute_ShouldMatchWhenNoLiteralApplies()
    {
        // Arrange
        Route parameterRoute = new(HttpMethod.Get, "/api/{id}");
        Route literalRoute = new(HttpMethod.Get, "/api/status");
        Router router = new(new IRouterRoute[] { parameterRoute, literalRoute });
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/api/42");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(parameterRoute);
        match.Values["id"].ShouldBe("42");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Constrained parameter outranks unconstrained parameter")]
    public void Match_ConstrainedParameter_ShouldOutrankUnconstrainedParameter()
    {
        // Arrange
        Route unconstrained = new(HttpMethod.Get, "/api/{id}");
        Route constrained = new(HttpMethod.Get, "/api/{id:int}");
        Router router = new(new IRouterRoute[] { unconstrained, constrained });

        // Act
        RouteMatch numeric = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/42"));
        RouteMatch textual = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/abc"));

        // Assert
        numeric.Status.ShouldBe(RouteMatchStatus.Matched);
        numeric.Route.ShouldBeSameAs(constrained);

        textual.Status.ShouldBe(RouteMatchStatus.Matched);
        textual.Route.ShouldBeSameAs(unconstrained);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: A more specific method match outranks a higher-precedence path")]
    public void Match_MethodAcceptance_ShouldOverrideHigherPrecedencePath()
    {
        // Arrange — the literal has higher precedence but only accepts GET; a POST must fall to the param route.
        Route getLiteral = new(HttpMethod.Get, "/api/status");
        Route postParameter = new(HttpMethod.Post, "/api/{id}");
        Router router = new(new IRouterRoute[] { getLiteral, postParameter });
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Post, "/api/status");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(postParameter);
        match.Values["id"].ShouldBe("status");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Path match with wrong method yields 405")]
    public void Match_PathMatchWithWrongMethod_ShouldReturnMethodNotAllowed()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/users/{id}");
        Router router = new(route);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Post, "/users/5");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.MethodNotAllowed);
        match.Route.ShouldBeNull();
        match.AllowedMethods.ShouldContain(HttpMethod.Get);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: No path match yields no match")]
    public void Match_NoPathMatch_ShouldReturnNoMatch()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/users/{id}");
        Router router = new(route);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/orders/5");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.NoMatch);
        match.Route.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: A route accepting multiple methods matches each")]
    public void Match_MultiMethodRoute_ShouldMatchEachMethod()
    {
        // Arrange
        Route route = new(new[] { HttpMethod.Get, HttpMethod.Post }, "/items");
        Router router = new(route);

        // Act
        RouteMatch get = router.Match(TestHttpContext.Create(HttpMethod.Get, "/items"));
        RouteMatch post = router.Match(TestHttpContext.Create(HttpMethod.Post, "/items"));
        RouteMatch delete = router.Match(TestHttpContext.Create(HttpMethod.Delete, "/items"));

        // Assert
        get.Status.ShouldBe(RouteMatchStatus.Matched);
        post.Status.ShouldBe(RouteMatchStatus.Matched);

        delete.Status.ShouldBe(RouteMatchStatus.MethodNotAllowed);
        delete.AllowedMethods.ShouldContain(HttpMethod.Get);
        delete.AllowedMethods.ShouldContain(HttpMethod.Post);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: 405 across routes unions the allowed methods")]
    public void Match_MethodMismatchAcrossRoutes_ShouldUnionAllowedMethods()
    {
        // Arrange — two routes share a path but map different methods.
        Route getRoute = new(HttpMethod.Get, "/resource");
        Route putRoute = new(HttpMethod.Put, "/resource");
        Router router = new(new IRouterRoute[] { getRoute, putRoute });
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Delete, "/resource");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.MethodNotAllowed);
        match.AllowedMethods.ShouldContain(HttpMethod.Get);
        match.AllowedMethods.ShouldContain(HttpMethod.Put);
        match.AllowedMethods.ShouldContain(HttpMethod.Head);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: 405 Allow header advertises HEAD for GET routes")]
    public void Match_MethodMismatchOnGetRoute_ShouldAdvertiseHeadInAllowHeader()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/reports");
        Router router = new(route);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Post, "/reports");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.MethodNotAllowed);
        match.AllowedMethods.ShouldContain(HttpMethod.Head);

        string allow = match.ToAllowHeaderValue().Value;
        allow.ShouldContain("GET", Case.Sensitive);
        allow.ShouldContain("HEAD", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: HEAD falls back to a GET route")]
    public void Match_HeadRequest_ShouldFallBackToGetRoute()
    {
        // Arrange
        Route getRoute = new(HttpMethod.Get, "/page");
        Router router = new(getRoute);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Head, "/page");

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(getRoute);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Explicit HEAD route matches HEAD")]
    public void Match_ExplicitHeadRoute_ShouldMatchHead()
    {
        // Arrange
        Route headRoute = new(HttpMethod.Head, "/probe");
        Router router = new(headRoute);

        // Act
        RouteMatch head = router.Match(TestHttpContext.Create(HttpMethod.Head, "/probe"));
        RouteMatch get = router.Match(TestHttpContext.Create(HttpMethod.Get, "/probe"));

        // Assert
        head.Status.ShouldBe(RouteMatchStatus.Matched);
        head.Route.ShouldBeSameAs(headRoute);

        // A HEAD-only route does not answer GET.
        get.Status.ShouldBe(RouteMatchStatus.MethodNotAllowed);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Routes: Registration order is preserved")]
    public void Routes_ShouldPreserveRegistrationOrder()
    {
        // Arrange
        Route first = new(HttpMethod.Get, "/api/{id}");
        Route second = new(HttpMethod.Get, "/api/status");
        Router router = new(new IRouterRoute[] { first, second });

        // Act
        IRouterRoute[] routes = System.Linq.Enumerable.ToArray(router.Routes);

        // Assert
        routes[0].ShouldBeSameAs(first);
        routes[1].ShouldBeSameAs(second);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteAsync: Invokes handler on a successful match")]
    public async Task RouteAsync_OnMatch_ShouldInvokeHandlerAndStoreRoute()
    {
        // Arrange
        RecordingRouterRouteHandler handler = new();
        Route route = new(HttpMethod.Get, "/ping", handler);
        Router router = new(route);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/ping");

        // Act
        await router.RouteAsync(context);

        // Assert
        handler.WasInvoked.ShouldBeTrue();
        context.TryGetRoute(out IRouterRoute? matched).ShouldBeTrue();
        matched.ShouldBeSameAs(route);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteAsync: Emits 405 and Allow header on method mismatch")]
    public async Task RouteAsync_OnMethodMismatch_ShouldEmit405AndAllowHeader()
    {
        // Arrange
        RecordingRouterRouteHandler handler = new();
        Route route = new(HttpMethod.Get, "/ping", handler);
        Router router = new(route);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Post, "/ping");

        // Act
        await router.RouteAsync(context);

        // Assert
        handler.WasInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        context.Response.Headers.TryGetValue(HttpHeaderKey.Allow, out HttpHeaderValue allow).ShouldBeTrue();
        allow.Value.ShouldContain("GET", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - RouteAsync: Leaves the response untouched on no match")]
    public async Task RouteAsync_OnNoMatch_ShouldLeaveResponseUntouched()
    {
        // Arrange
        RecordingRouterRouteHandler handler = new();
        Route route = new(HttpMethod.Get, "/ping", handler);
        Router router = new(route);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/pong");

        // Act
        await router.RouteAsync(context);

        // Assert
        handler.WasInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.TryGetRoute(out _).ShouldBeFalse();
    }
}
