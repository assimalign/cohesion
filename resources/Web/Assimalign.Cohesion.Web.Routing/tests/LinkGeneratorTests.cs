using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Routing.Policies;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class LinkGeneratorTests
{
    #region Route-name registration (build time)

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Route names: duplicates fail when the route table is built")]
    public void Build_DuplicateRouteNames_ShouldThrowAtBuildTime()
    {
        // Arrange — route names are case-insensitive, so 'users' and 'USERS' collide.
        IRouterBuilder builder = new RouterBuilder()
            .Map(NamedRoute("users", "/users/{id}"))
            .Map(NamedRoute("USERS", "/customers/{id}"));

        // Act / Assert
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());
        exception.Message.ShouldContain("users");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Route names: unique names build and resolve")]
    public void Build_UniqueRouteNames_ShouldSucceed()
    {
        // Arrange
        IRouter router = new RouterBuilder()
            .Map(NamedRoute("users", "/users/{id}"))
            .Map(NamedRoute("orders", "/orders/{id}"))
            .Build();

        // Act
        string usersPath = router.LinkGenerator.GetPathByName("users", new RouteValueDictionary { ["id"] = 1 });
        string ordersPath = router.LinkGenerator.GetPathByName("orders", new RouteValueDictionary { ["id"] = 2 });

        // Assert
        usersPath.ShouldBe("/users/1");
        ordersPath.ShouldBe("/orders/2");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Route names: lookup is case-insensitive")]
    public void GetPathByName_DifferentCasing_ShouldResolveRoute()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("Users", "/users/{id}"));

        // Act
        string path = router.LinkGenerator.GetPathByName("users", new RouteValueDictionary { ["id"] = 42 });

        // Assert
        path.ShouldBe("/users/42");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Route names: a named route without a pattern fails at build time")]
    public void Build_NamedRouteWithoutPattern_ShouldThrowAtBuildTime()
    {
        // Arrange
        IRouterBuilder builder = new RouterBuilder()
            .Map(new PatternlessRoute(new RouterRouteMetadataCollection(new RouteNameMetadata("custom"))));

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Route names: the last-registered name metadata wins")]
    public void Build_MultipleNameMetadata_ShouldUseLastRegisteredName()
    {
        // Arrange — mirrors group-level metadata being overridden by endpoint-level metadata.
        Route route = new(
            new[] { HttpMethod.Get },
            RoutePatternParser.Parse("/users/{id}"),
            RouteParameterPolicyMap.CreateDefault(),
            new RecordingRouterRouteHandler(),
            new RouterRouteMetadataCollection(new RouteNameMetadata("group-name"), new RouteNameMetadata("endpoint-name")));
        Router router = CreateRouter(route);

        // Act / Assert
        router.LinkGenerator.TryGetPathByName("endpoint-name", new RouteValueDictionary { ["id"] = 1 }, out string? path).ShouldBeTrue();
        path.ShouldBe("/users/1");
        router.LinkGenerator.TryGetPathByName("group-name", new RouteValueDictionary { ["id"] = 1 }, out _).ShouldBeFalse();
    }

    #endregion

    #region Path generation by name

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - GetPathByName: fills parameters into the template")]
    public void GetPathByName_LiteralAndParameter_ShouldGeneratePath()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("user", "/users/{id}"));

        // Act
        string path = router.LinkGenerator.GetPathByName("user", new RouteValueDictionary { ["id"] = 42 });

        // Assert
        path.ShouldBe("/users/42");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - GetPathByName: an unknown route name throws; Try returns false")]
    public void GetPathByName_UnknownName_ShouldThrow()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("user", "/users/{id}"));

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => router.LinkGenerator.GetPathByName("missing"))
            .Message.ShouldContain("missing");
        router.LinkGenerator.TryGetPathByName("missing", null, out string? path).ShouldBeFalse();
        path.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - GetPathByName: a missing required value throws; Try returns false")]
    public void GetPathByName_MissingRequiredValue_ShouldThrow()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("user", "/users/{id}"));

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => router.LinkGenerator.GetPathByName("user"));
        router.LinkGenerator.TryGetPathByName("user", null, out _).ShouldBeFalse();
    }

    #endregion

    #region Defaults and optional parameters

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Defaults: all-default values collapse to the root path")]
    public void GetPathByName_AllDefaults_ShouldCollapseToRoot()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("mvc", "/{controller=Home}/{action=Index}/{id?}"));

        // Act
        string path = router.LinkGenerator.GetPathByName("mvc");

        // Assert
        path.ShouldBe("/");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Defaults: trailing segments equal to their defaults are trimmed")]
    public void GetPathByName_TrailingDefaults_ShouldTrim()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("mvc", "/{controller=Home}/{action=Index}/{id?}"));

        // Act / Assert — the trailing default run trims; explicit values equal to defaults trim too.
        router.LinkGenerator.GetPathByName("mvc", new RouteValueDictionary { ["controller"] = "Store" })
            .ShouldBe("/Store");
        router.LinkGenerator.GetPathByName("mvc", new RouteValueDictionary { ["controller"] = "Store", ["action"] = "About" })
            .ShouldBe("/Store/About");
        router.LinkGenerator.GetPathByName("mvc", new RouteValueDictionary { ["controller"] = "Home", ["action"] = "Index" })
            .ShouldBe("/");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Defaults: a later value forces intermediate defaults to render")]
    public void GetPathByName_ValueAfterDefaults_ShouldRenderIntermediateDefaults()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("mvc", "/{controller=Home}/{action=Index}/{id?}"));

        // Act
        string path = router.LinkGenerator.GetPathByName("mvc", new RouteValueDictionary { ["id"] = 17 });

        // Assert — id is not trimmable, so the default-valued segments before it must render.
        path.ShouldBe("/Home/Index/17");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Optional: an omitted trailing optional segment collapses")]
    public void GetPathByName_OmittedOptional_ShouldCollapseSegment()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("item", "/api/items/{id?}"));

        // Act / Assert
        router.LinkGenerator.GetPathByName("item").ShouldBe("/api/items");
        router.LinkGenerator.GetPathByName("item", new RouteValueDictionary { ["id"] = 3 }).ShouldBe("/api/items/3");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Optional: a complex-segment optional drops with its separator")]
    public void GetPathByName_ComplexSegmentOptional_ShouldDropSeparatorWhenOmitted()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("file", "/files/{name}.{ext?}"));

        // Act / Assert
        router.LinkGenerator.GetPathByName("file", new RouteValueDictionary { ["name"] = "report" })
            .ShouldBe("/files/report");
        router.LinkGenerator.GetPathByName("file", new RouteValueDictionary { ["name"] = "report", ["ext"] = "txt" })
            .ShouldBe("/files/report.txt");
    }

    #endregion

    #region Catch-all encoding

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Catch-all: '{*path}' encodes slashes in the value")]
    public void GetPathByName_CatchAll_ShouldEncodeSlashes()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("docs", "/docs/{*path}"));

        // Act
        string path = router.LinkGenerator.GetPathByName("docs", new RouteValueDictionary { ["path"] = "guides/intro v2" });

        // Assert
        path.ShouldBe("/docs/guides%2Fintro%20v2");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Catch-all: '{**path}' keeps slashes as segment separators")]
    public void GetPathByName_DoubleCatchAll_ShouldPreserveSlashes()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("docs", "/docs/{**path}"));

        // Act
        string path = router.LinkGenerator.GetPathByName("docs", new RouteValueDictionary { ["path"] = "guides/intro v2" });

        // Assert
        path.ShouldBe("/docs/guides/intro%20v2");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Catch-all: an omitted catch-all segment collapses")]
    public void GetPathByName_OmittedCatchAll_ShouldCollapse()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("docs", "/docs/{*path}"));

        // Act / Assert
        router.LinkGenerator.GetPathByName("docs").ShouldBe("/docs");
    }

    #endregion

    #region Encoding and query string

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Encoding: parameter values are escaped per path segment")]
    public void GetPathByName_ParameterValue_ShouldEscapePathSegment()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("tag", "/tags/{tag}"));

        // Act
        string path = router.LinkGenerator.GetPathByName("tag", new RouteValueDictionary { ["tag"] = "c# 10/x" });

        // Assert
        path.ShouldBe("/tags/c%23%2010%2Fx");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Query: surplus values append as a query string in supplied order")]
    public void GetPathByName_SurplusValues_ShouldAppendQueryString()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("user", "/users/{id}"));

        // Act
        string path = router.LinkGenerator.GetPathByName("user", new RouteValueDictionary
        {
            ["id"] = 42,
            ["sort"] = "asc",
            ["page"] = 2,
        });

        // Assert
        path.ShouldBe("/users/42?sort=asc&page=2");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Query: keys and values use query encoding")]
    public void GetPathByName_SurplusValues_ShouldUseQueryEncoding()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("search", "/search"));

        // Act
        string path = router.LinkGenerator.GetPathByName("search", new RouteValueDictionary { ["q"] = "a b&c=d" });

        // Assert
        path.ShouldBe("/search?q=a%20b%26c%3Dd");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Query: null surplus values are skipped")]
    public void GetPathByName_NullSurplusValue_ShouldBeSkipped()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("search", "/search"));

        // Act
        string path = router.LinkGenerator.GetPathByName("search", new RouteValueDictionary
        {
            ["q"] = "widgets",
            ["filter"] = null,
        });

        // Assert
        path.ShouldBe("/search?q=widgets");
    }

    #endregion

    #region Constraints during generation

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraints: a violating value fails generation by name")]
    public void TryGetPathByName_ConstraintViolation_ShouldReturnFalse()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("api", "/api/{id:int}"));

        // Act / Assert
        router.LinkGenerator.TryGetPathByName("api", new RouteValueDictionary { ["id"] = "abc" }, out _).ShouldBeFalse();
        router.LinkGenerator.TryGetPathByName("api", new RouteValueDictionary { ["id"] = "42" }, out string? path).ShouldBeTrue();
        path.ShouldBe("/api/42");
    }

    #endregion

    #region Generation by values (outbound precedence)

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - OutboundPrecedence: the most specific satisfiable route wins")]
    public void TryGetPathByValues_MoreSpecificRoute_ShouldWin()
    {
        // Arrange — both routes are satisfiable by {category, id} (the shallow one would push id to
        // the query string); higher outbound precedence must pick the deeper route.
        Router router = CreateRouter(
            new Route(HttpMethod.Get, "/products/{category}"),
            new Route(HttpMethod.Get, "/products/{category}/{id}"));

        // Act / Assert
        RouteValueDictionary both = new() { ["category"] = "tools", ["id"] = 5 };
        router.LinkGenerator.TryGetPathByValues(both, out string? deep).ShouldBeTrue();
        deep.ShouldBe("/products/tools/5");

        RouteValueDictionary categoryOnly = new() { ["category"] = "tools" };
        router.LinkGenerator.TryGetPathByValues(categoryOnly, out string? shallow).ShouldBeTrue();
        shallow.ShouldBe("/products/tools");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - OutboundPrecedence: a failed constraint falls through to the next candidate")]
    public void TryGetPathByValues_ConstraintViolation_ShouldFallThroughToNextCandidate()
    {
        // Arrange — the constrained route has higher outbound precedence and is tried first.
        Router router = CreateRouter(
            new Route(HttpMethod.Get, "/api/{id}"),
            new Route(HttpMethod.Get, "/api/{id:int}"));

        // Act / Assert
        router.LinkGenerator.TryGetPathByValues(new RouteValueDictionary { ["id"] = 42 }, out string? typed).ShouldBeTrue();
        typed.ShouldBe("/api/42");

        router.LinkGenerator.TryGetPathByValues(new RouteValueDictionary { ["id"] = "abc" }, out string? fallback).ShouldBeTrue();
        fallback.ShouldBe("/api/abc");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - OutboundPrecedence: equal precedence ties break by registration order")]
    public void TryGetPathByValues_EqualPrecedence_ShouldPreferRegistrationOrder()
    {
        // Arrange — identical shape (literal + parameter), so precedence ties.
        Router router = CreateRouter(
            new Route(HttpMethod.Get, "/alpha/{x}"),
            new Route(HttpMethod.Get, "/beta/{x}"));

        // Act
        router.LinkGenerator.TryGetPathByValues(new RouteValueDictionary { ["x"] = "1" }, out string? path).ShouldBeTrue();

        // Assert
        path.ShouldBe("/alpha/1");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - By values: unsatisfiable values generate nothing")]
    public void TryGetPathByValues_NoSatisfiableRoute_ShouldReturnFalse()
    {
        // Arrange
        Router router = CreateRouter(new Route(HttpMethod.Get, "/users/{id}"));

        // Act / Assert — 'id' has no value and no default, so no candidate is satisfiable.
        router.LinkGenerator.TryGetPathByValues(new RouteValueDictionary { ["other"] = "x" }, out string? path).ShouldBeFalse();
        path.ShouldBeNull();
    }

    #endregion

    #region Absolute URIs

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - GetUriByName: composes scheme, host, path, and query")]
    public void GetUriByName_WithSchemeAndHost_ShouldComposeAbsoluteUri()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("user", "/users/{id}"));

        // Act
        string uri = router.LinkGenerator.GetUriByName(
            "user",
            HttpScheme.Https,
            new HttpHost("example.com:8443"),
            new RouteValueDictionary { ["id"] = 42, ["expand"] = "orders" });

        // Assert
        uri.ShouldBe("https://example.com:8443/users/42?expand=orders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - GetUriByName: requires an http(s) scheme and a host")]
    public void GetUriByName_InvalidAuthority_ShouldThrowArgumentException()
    {
        // Arrange
        Router router = CreateRouter(NamedRoute("user", "/users/{id}"));
        RouteValueDictionary values = new() { ["id"] = 42 };

        // Act / Assert
        Should.Throw<ArgumentException>(() => router.LinkGenerator.GetUriByName("user", HttpScheme.None, new HttpHost("example.com"), values));
        Should.Throw<ArgumentException>(() => router.LinkGenerator.GetUriByName("user", HttpScheme.Https, HttpHost.Empty, values));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryGetUriByValues: composes an absolute URI over the by-values path")]
    public void TryGetUriByValues_SatisfiableValues_ShouldComposeAbsoluteUri()
    {
        // Arrange
        Router router = CreateRouter(new Route(HttpMethod.Get, "/products/{category}"));

        // Act
        bool generated = router.LinkGenerator.TryGetUriByValues(
            HttpScheme.Http,
            new HttpHost("localhost:5000"),
            new RouteValueDictionary { ["category"] = "tools" },
            out string? uri);

        // Assert
        generated.ShouldBeTrue();
        uri.ShouldBe("http://localhost:5000/products/tools");
    }

    #endregion

    #region Round-trips (generate → match)

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Round-trip: a typed constraint matches back to the same route and typed value")]
    public void RoundTrip_TypedConstraint_ShouldMatchSameRouteAndValues()
    {
        // Arrange
        Route route = NamedRoute("api", "/api/{id:int}");
        Router router = CreateRouter(route);
        string path = router.LinkGenerator.GetPathByName("api", new RouteValueDictionary { ["id"] = 42 });

        // Act
        RouteMatch match = MatchGeneratedPath(router, path);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(route);
        match.Values["id"].ShouldBe(42);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Round-trip: collapsed defaults re-apply on match")]
    public void RoundTrip_CollapsedDefaults_ShouldMatchSameRouteAndValues()
    {
        // Arrange
        Route route = NamedRoute("mvc", "/{controller=Home}/{action=Index}/{id?}");
        Router router = CreateRouter(route);
        string path = router.LinkGenerator.GetPathByName(
            "mvc",
            new RouteValueDictionary { ["controller"] = "Home", ["action"] = "Index" });

        // Act
        RouteMatch match = MatchGeneratedPath(router, path);

        // Assert — the generated short form ('/') matches back with the defaults re-applied.
        path.ShouldBe("/");
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(route);
        match.Values["controller"].ShouldBe("Home");
        match.Values["action"].ShouldBe("Index");
        match.Values.ContainsKey("id").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Round-trip: '{**path}' round-trips slash-containing values exactly")]
    public void RoundTrip_DoubleCatchAll_ShouldMatchSameValues()
    {
        // Arrange
        Route route = NamedRoute("docs", "/docs/{**path}");
        Router router = CreateRouter(route);
        string generated = router.LinkGenerator.GetPathByName("docs", new RouteValueDictionary { ["path"] = "guides/intro" });

        // Act
        RouteMatch match = MatchGeneratedPath(router, generated);

        // Assert
        generated.ShouldBe("/docs/guides/intro");
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(route);
        match.Values["path"].ShouldBe("guides/intro");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Round-trip: '{*path}' keeps an encoded slash opaque end-to-end")]
    public void RoundTrip_SingleCatchAll_ShouldKeepEncodedSlashOpaque()
    {
        // Arrange
        Route route = NamedRoute("docs", "/docs/{*path}");
        Router router = CreateRouter(route);
        string generated = router.LinkGenerator.GetPathByName("docs", new RouteValueDictionary { ["path"] = "a/b" });

        // Act
        RouteMatch match = MatchGeneratedPath(router, generated);

        // Assert — the transports deliberately never decode %2F (an encoded slash must not create a
        // phantom segment boundary), so the generated URL addresses the same route but the captured
        // value keeps the slash encoded. Identity round-trips for slash values use '{**path}'.
        generated.ShouldBe("/docs/a%2Fb");
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(route);
        match.Values["path"].ShouldBe("a%2Fb");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Round-trip: escaped parameter values decode back to the same value")]
    public void RoundTrip_EscapedParameterValue_ShouldMatchSameValues()
    {
        // Arrange
        Route route = NamedRoute("tag", "/tags/{tag}");
        Router router = CreateRouter(route);
        string generated = router.LinkGenerator.GetPathByName("tag", new RouteValueDictionary { ["tag"] = "a&b:c" });

        // Act
        RouteMatch match = MatchGeneratedPath(router, generated);

        // Assert
        generated.ShouldBe("/tags/a%26b%3Ac");
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(route);
        match.Values["tag"].ShouldBe("a&b:c");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Round-trip: an omitted constrained optional still matches")]
    public void RoundTrip_OmittedConstrainedOptional_ShouldMatchSameRoute()
    {
        // Arrange — the constraint governs the value when present; it must not make it required.
        Route route = NamedRoute("item", "/api/items/{id:int?}");
        Router router = CreateRouter(route);

        // Act
        string omitted = router.LinkGenerator.GetPathByName("item");
        RouteMatch omittedMatch = MatchGeneratedPath(router, omitted);

        string supplied = router.LinkGenerator.GetPathByName("item", new RouteValueDictionary { ["id"] = 7 });
        RouteMatch suppliedMatch = MatchGeneratedPath(router, supplied);

        // Assert
        omitted.ShouldBe("/api/items");
        omittedMatch.Status.ShouldBe(RouteMatchStatus.Matched);
        omittedMatch.Route.ShouldBeSameAs(route);
        omittedMatch.Values.ContainsKey("id").ShouldBeFalse();

        supplied.ShouldBe("/api/items/7");
        suppliedMatch.Status.ShouldBe(RouteMatchStatus.Matched);
        suppliedMatch.Values["id"].ShouldBe(7);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Round-trip: complex segments match back with and without the optional part")]
    public void RoundTrip_ComplexSegment_ShouldMatchSameRouteAndValues()
    {
        // Arrange
        Route route = NamedRoute("file", "/files/{name}.{ext?}");
        Router router = CreateRouter(route);

        // Act
        RouteMatch withoutExtension = MatchGeneratedPath(
            router,
            router.LinkGenerator.GetPathByName("file", new RouteValueDictionary { ["name"] = "report" }));
        RouteMatch withExtension = MatchGeneratedPath(
            router,
            router.LinkGenerator.GetPathByName("file", new RouteValueDictionary { ["name"] = "report", ["ext"] = "txt" }));

        // Assert
        withoutExtension.Status.ShouldBe(RouteMatchStatus.Matched);
        withoutExtension.Route.ShouldBeSameAs(route);
        withoutExtension.Values["name"].ShouldBe("report");
        withoutExtension.Values.ContainsKey("ext").ShouldBeFalse();

        withExtension.Status.ShouldBe(RouteMatchStatus.Matched);
        withExtension.Values["name"].ShouldBe("report");
        withExtension.Values["ext"].ShouldBe("txt");
    }

    #endregion

    #region HttpContext extension

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - GetLinkGenerator: resolves the application's link generator from the router feature")]
    public void GetLinkGenerator_WithRouterFeature_ShouldGenerateFromApplicationRoutes()
    {
        // Arrange
        IRouterFeature feature = new RouterFeature();
        feature.Builder.Map(NamedRoute("user", "/users/{id}"));

        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, HttpPath.Root);
        context.Features.Set(feature);

        // Act
        ILinkGenerator generator = context.GetLinkGenerator();

        // Assert
        generator.GetPathByName("user", new RouteValueDictionary { ["id"] = 42 }).ShouldBe("/users/42");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - GetLinkGenerator: throws when routing has not been registered")]
    public void GetLinkGenerator_WithoutRouterFeature_ShouldThrow()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, HttpPath.Root);

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => context.GetLinkGenerator());
    }

    #endregion

    private static Route NamedRoute(string name, string pattern)
    {
        return new Route(
            new[] { HttpMethod.Get },
            RoutePatternParser.Parse(pattern),
            RouteParameterPolicyMap.CreateDefault(),
            new RecordingRouterRouteHandler(),
            new RouterRouteMetadataCollection(new RouteNameMetadata(name)));
    }

    private static Router CreateRouter(params IRouterRoute[] routes)
    {
        return new Router(routes);
    }

    /// <summary>
    /// Matches a generated link the way a transport would: the query string is not part of the
    /// path, and the path arrives percent-decoded (<see cref="HttpPath.FromUriComponent"/> is what
    /// the HTTP/1.1/2/3 request parsers use).
    /// </summary>
    private static RouteMatch MatchGeneratedPath(Router router, string generated)
    {
        int queryIndex = generated.IndexOf('?');
        string pathText = queryIndex < 0 ? generated : generated[..queryIndex];

        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, HttpPath.FromUriComponent(pathText));
        return router.Match(context);
    }
}
