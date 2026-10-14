using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

/// <summary>
/// Fallback routes (#1056): <c>MapFallback</c> answers GET/HEAD requests no other route matches, never
/// shadows an application route, never turns an unmatched path into a 405, and leaves missing files to
/// the 404 through the <c>nonfile</c> constraint.
/// </summary>
public class FallbackRouteTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Fallback: Answers unmatched GET and HEAD client routes")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/dashboard")]
    [InlineData("GET", "/users/42/settings")]
    [InlineData("HEAD", "/dashboard")]
    [InlineData("GET", "/releases/v1.2/")]
    public void MapFallback_UnmatchedNonFilePath_ShouldMatchFallback(string method, string path)
    {
        // Arrange
        RouterBuilder builder = new();
        RecordingRouterRouteHandler fallback = new();
        builder.Map(HttpMethod.Get, "/api/orders", new RecordingRouterRouteHandler());
        builder.MapFallback(fallback);
        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(new HttpMethod(method), path));

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route!.Handler.ShouldBeSameAs(fallback);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Fallback: A path naming a file is never answered by the fallback")]
    [InlineData("/app.js")]
    [InlineData("/assets/logo.png")]
    [InlineData("/v1.2/readme.md")]
    public void MapFallback_FilePath_ShouldNotMatch(string path)
    {
        // Arrange
        RouterBuilder builder = new();
        builder.MapFallback(new RecordingRouterRouteHandler());
        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, path));

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Fallback: A non-GET request to an unmatched path is a 404, not a 405")]
    public void MapFallback_PostToUnmatchedPath_ShouldBeNoMatch()
    {
        // Arrange
        RouterBuilder builder = new();
        builder.MapFallback(new RecordingRouterRouteHandler());
        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Post, "/dashboard"));

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Fallback: A real 405 is unaffected by the fallback")]
    public void MapFallback_MethodMismatchOnRealRoute_ShouldStay405WithoutFallbackMethods()
    {
        // Arrange
        RouterBuilder builder = new();
        builder.Map(HttpMethod.Post, "/orders", new RecordingRouterRouteHandler());
        builder.MapFallback(new RecordingRouterRouteHandler());
        IRouter router = builder.Build();

        // Act — GET /orders: the real route rejects GET, and the fallback must not answer it instead.
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Delete, "/orders"));

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.MethodNotAllowed);
        match.ToAllowHeaderValue().ToString().ShouldBe("POST");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Fallback: Loses to any application route, whatever the registration order")]
    public void MapFallback_RegisteredFirst_ShouldLoseToCatchAllRoute()
    {
        // Arrange — a fallback registered first and an application catch-all at the same precedence.
        RouterBuilder builder = new();
        RecordingRouterRouteHandler fallback = new();
        RecordingRouterRouteHandler application = new();
        builder.MapFallback(fallback);
        builder.Map(HttpMethod.Get, "/{**slug:nonfile}", application);
        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/about"));

        // Assert
        match.Route!.Handler.ShouldBeSameAs(application);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Fallback: A sub-path fallback wins over the site fallback for its paths")]
    public void MapFallback_SubPathTemplate_ShouldWinForItsPaths()
    {
        // Arrange
        RouterBuilder builder = new();
        RecordingRouterRouteHandler site = new();
        RecordingRouterRouteHandler admin = new();
        builder.MapFallback(site);
        builder.MapFallback("admin/{**path:nonfile}", admin);
        IRouter router = builder.Build();

        // Act
        RouteMatch adminMatch = router.Match(TestHttpContext.Create(HttpMethod.Get, "/admin/users"));
        RouteMatch siteMatch = router.Match(TestHttpContext.Create(HttpMethod.Get, "/shop"));

        // Assert
        adminMatch.Route!.Handler.ShouldBeSameAs(admin);
        siteMatch.Route!.Handler.ShouldBeSameAs(site);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Catch-all: An omitted catch-all matches and captures no value")]
    public void CatchAll_Omitted_ShouldMatchWithoutValue()
    {
        // Arrange — the collapsed form the link generator produces for '/files/{**path}' with no path.
        Route route = new(HttpMethod.Get, "/files/{**path}");

        // Act
        bool matched = route.TryMatch(TestHttpContext.Create(HttpMethod.Get, "/files"), out RouteValueDictionary values);

        // Assert
        matched.ShouldBeTrue();
        values.ContainsKey("path").ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - nonfile: Rejects a last segment with a file extension")]
    [InlineData("/docs/{**path:nonfile}", "/docs/guide", true)]
    [InlineData("/docs/{**path:nonfile}", "/docs/guide.html", false)]
    [InlineData("/docs/{**path:nonfile}", "/docs/v2.0/guide", true)]
    [InlineData("/docs/{**path:nonfile}", "/docs/trailing.", true)]
    public void NonFilePolicy_ShouldRejectFileNames(string template, string path, bool expected)
    {
        // Arrange
        Route route = new(HttpMethod.Get, template);

        // Act
        bool matched = route.TryMatch(TestHttpContext.Create(HttpMethod.Get, path), out _);

        // Assert
        matched.ShouldBe(expected);
    }
}
