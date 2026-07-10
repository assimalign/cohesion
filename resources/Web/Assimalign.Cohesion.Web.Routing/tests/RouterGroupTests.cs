using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Exceptions;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Policies;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RouterGroupTests
{
    private sealed class TestMetadata
    {
        public TestMetadata(string value) => Value = value;

        public string Value { get; }
    }

    private static TestHttpContext CreateContext(HttpMethod method, string path, string host)
    {
        TestHttpContext context = TestHttpContext.Create(method, path);
        context.Request.Host = new HttpHost(host);
        return context;
    }

    private sealed class ExactValueRouteParameterPolicy : RouteParameterPolicy
    {
        private readonly string _expected;

        public ExactValueRouteParameterPolicy(string expected) => _expected = expected;

        public override bool Applies(RouteParameterPolicyContext context)
        {
            return context.TryGetParameterValue(out object? value)
                && value is string text
                && string.Equals(text, _expected, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - MapGroup: Composes the prefix onto child templates at registration time")]
    public void MapGroup_WithPrefix_ShouldComposePrefixAtRegistrationTime()
    {
        // Arrange
        RouterBuilder builder = new();
        RecordingRouterRouteHandler handler = new();

        builder.MapGroup("api").Map(HttpMethod.Get, "orders", handler);

        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/orders"));
        RouteMatch unprefixed = router.Match(TestHttpContext.Create(HttpMethod.Get, "/orders"));

        // Assert — the registered route is a single fully-composed route; the bare child
        // template does not exist as a route of its own.
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeOfType<Route>().Pattern.RawText.ShouldBe("api/orders");
        unprefixed.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - MapGroup: Nested groups compose every ancestor prefix")]
    public void MapGroup_NestedGroups_ShouldComposeAllPrefixes()
    {
        // Arrange
        RouterBuilder builder = new();
        RecordingRouterRouteHandler handler = new();

        IRouterGroupBuilder nested = builder.MapGroup("api").MapGroup("v1");
        nested.Prefix.ShouldBe("api/v1");
        nested.Map(HttpMethod.Get, "orders/{id:int}", handler);

        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/v1/orders/42"));

        // Assert — the typed 'int' constraint still converts the captured value on the composed route.
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Values["id"].ShouldBeOfType<int>().ShouldBe(42);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - MapGroup: Template-parameter prefixes capture route values")]
    public void MapGroup_TemplateParameterPrefix_ShouldCapturePrefixParameters()
    {
        // Arrange
        RouterBuilder builder = new();
        RecordingRouterRouteHandler handler = new();

        builder.MapGroup("{tenant}/api").Map(HttpMethod.Get, "orders", handler);

        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/contoso/api/orders"));

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Values["tenant"].ShouldBe("contoso");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Match: Grouped literal beats a parameter at the same depth")]
    public void Match_GroupedLiteral_ShouldBeatParameterAtSameDepth()
    {
        // Arrange — the parameter route is registered FIRST and directly; the literal arrives
        // later through a group. Precedence must still prefer the literal.
        RouterBuilder builder = new();
        Route parameterRoute = new(HttpMethod.Get, "api/{id}");
        RecordingRouterRouteHandler groupedHandler = new();

        builder.Map(parameterRoute);
        builder.MapGroup("api").Map(HttpMethod.Get, "status", groupedHandler);

        IRouter router = builder.Build();

        // Act
        RouteMatch literalMatch = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/status"));
        RouteMatch parameterMatch = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/42"));

        // Assert
        literalMatch.Status.ShouldBe(RouteMatchStatus.Matched);
        literalMatch.Route!.Handler.ShouldBeSameAs(groupedHandler);
        parameterMatch.Status.ShouldBe(RouteMatchStatus.Matched);
        parameterMatch.Route.ShouldBeSameAs(parameterRoute);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - WithMetadata: Group metadata applies to every child route")]
    public void WithMetadata_OnGroup_ShouldApplyToAllChildRoutes()
    {
        // Arrange
        RouterBuilder builder = new();
        TestMetadata shared = new("group");

        builder.MapGroup("api")
            .WithMetadata(shared)
            .Map(HttpMethod.Get, "orders", new RecordingRouterRouteHandler())
            .Map(HttpMethod.Get, "customers", new RecordingRouterRouteHandler());

        IRouter router = builder.Build();

        // Act
        RouteMatch orders = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/orders"));
        RouteMatch customers = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/customers"));

        // Assert
        orders.Route!.Metadata.GetMetadata<TestMetadata>().ShouldBeSameAs(shared);
        customers.Route!.Metadata.GetMetadata<TestMetadata>().ShouldBeSameAs(shared);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - WithMetadata: Merge order is outer group, inner group, then route")]
    public void WithMetadata_NestedGroups_ShouldLayerOuterBeforeInnerBeforeRoute()
    {
        // Arrange
        RouterBuilder builder = new();
        TestMetadata outer = new("outer");
        TestMetadata inner = new("inner");
        TestMetadata route = new("route");

        builder.MapGroup("api")
            .WithMetadata(outer)
            .MapGroup("v1")
            .WithMetadata(inner)
            .Map(HttpMethod.Get, "orders", new RecordingRouterRouteHandler(), new RouterRouteMetadataCollection(route));

        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/v1/orders"));

        // Assert — broader scope first, narrower scope last; last-wins resolves the route level.
        IRouterRouteMetadataCollection metadata = match.Route!.Metadata;
        IReadOnlyList<TestMetadata> ordered = metadata.GetOrderedMetadata<TestMetadata>();
        ordered.Count.ShouldBe(3);
        ordered[0].ShouldBeSameAs(outer);
        ordered[1].ShouldBeSameAs(inner);
        ordered[2].ShouldBeSameAs(route);
        metadata.GetMetadata<TestMetadata>().ShouldBeSameAs(route);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - GetMetadata: Route-level metadata overrides group-level metadata")]
    public void GetMetadata_RouteLevelSameType_ShouldOverrideGroupLevel()
    {
        // Arrange
        RouterBuilder builder = new();
        TestMetadata groupLevel = new("group");
        TestMetadata routeLevel = new("route");

        builder.MapGroup("api")
            .WithMetadata(groupLevel)
            .Map(HttpMethod.Get, "orders", new RecordingRouterRouteHandler(), new RouterRouteMetadataCollection(routeLevel));

        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/orders"));

        // Assert
        match.Route!.Metadata.GetMetadata<TestMetadata>().ShouldBeSameAs(routeLevel);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - WithMetadata: Throws after a child route is registered")]
    public void WithMetadata_AfterChildRouteMapped_ShouldThrow()
    {
        // Arrange
        RouterBuilder builder = new();
        IRouterGroupBuilder group = builder.MapGroup("api");
        group.Map(HttpMethod.Get, "orders", new RecordingRouterRouteHandler());

        // Act & Assert — shared configuration is frozen once the group has produced a child.
        Should.Throw<InvalidOperationException>(() => group.WithMetadata(new TestMetadata("late")));
        Should.Throw<InvalidOperationException>(() =>
            group.WithParameterPolicy("late", new ExactValueRouteParameterPolicy("x")));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - WithMetadata: Throws after a nested group is created")]
    public void WithMetadata_AfterNestedGroupCreated_ShouldThrow()
    {
        // Arrange
        RouterBuilder builder = new();
        IRouterGroupBuilder group = builder.MapGroup("api");
        group.MapGroup("v1");

        // Act & Assert — the nested group snapshotted the parent's configuration, so the parent freezes.
        Should.Throw<InvalidOperationException>(() => group.WithMetadata(new TestMetadata("late")));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - WithParameterPolicy: Group policies apply to child routes")]
    public void WithParameterPolicy_OnGroup_ShouldApplyToChildRoutes()
    {
        // Arrange
        RouterBuilder builder = new();

        builder.MapGroup("api")
            .WithParameterPolicy("flavor", new ExactValueRouteParameterPolicy("vanilla"))
            .Map(HttpMethod.Get, "{value:flavor}", new RecordingRouterRouteHandler());

        IRouter router = builder.Build();

        // Act
        RouteMatch accepted = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/vanilla"));
        RouteMatch rejected = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/chocolate"));

        // Assert
        accepted.Status.ShouldBe(RouteMatchStatus.Matched);
        rejected.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Map: Route-level policy registration overrides the group's")]
    public void Map_RoutePolicyOverride_ShouldOverrideGroupPolicy()
    {
        // Arrange — the group accepts only 'vanilla'; one child re-registers the same policy
        // name to accept only 'chocolate'. The override must be scoped to that child.
        RouterBuilder builder = new();
        RecordingRouterRouteHandler overriddenHandler = new();
        RecordingRouterRouteHandler inheritedHandler = new();

        builder.MapGroup("api")
            .WithParameterPolicy("flavor", new ExactValueRouteParameterPolicy("vanilla"))
            .Map(
                new[] { HttpMethod.Get },
                "custom/{value:flavor}",
                overriddenHandler,
                metadata: null,
                policies: map => map.Add("flavor", new ExactValueRouteParameterPolicy("chocolate")))
            .Map(HttpMethod.Get, "shared/{value:flavor}", inheritedHandler);

        IRouter router = builder.Build();

        // Act
        RouteMatch overridden = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/custom/chocolate"));
        RouteMatch overriddenRejected = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/custom/vanilla"));
        RouteMatch inherited = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/shared/vanilla"));

        // Assert
        overridden.Status.ShouldBe(RouteMatchStatus.Matched);
        overridden.Route!.Handler.ShouldBeSameAs(overriddenHandler);
        overriddenRejected.Status.ShouldBe(RouteMatchStatus.NoMatch);
        inherited.Status.ShouldBe(RouteMatchStatus.Matched);
        inherited.Route!.Handler.ShouldBeSameAs(inheritedHandler);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Map: Duplicate parameter across prefix and child throws")]
    public void Map_DuplicateParameterAcrossPrefixAndChild_ShouldThrow()
    {
        // Arrange
        RouterBuilder builder = new();
        IRouterGroupBuilder group = builder.MapGroup("{id}");

        // Act & Assert — the composed template is re-parsed, so the parser's duplicate-parameter
        // rule governs prefix/child conflicts.
        Should.Throw<RoutePatternException>(() =>
            group.Map(HttpMethod.Get, "{id:int}", new RecordingRouterRouteHandler()));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Map: Child segments after a catch-all prefix throw")]
    public void Map_ChildAfterCatchAllPrefix_ShouldThrow()
    {
        // Arrange
        RouterBuilder builder = new();
        IRouterGroupBuilder group = builder.MapGroup("files/{*path}");

        // Act & Assert — catch-all must be the last segment of the composed template.
        Should.Throw<RoutePatternException>(() =>
            group.Map(HttpMethod.Get, "download", new RecordingRouterRouteHandler()));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - MapGroup: Invalid prefix syntax throws at group creation")]
    public void MapGroup_InvalidPrefixSyntax_ShouldThrow()
    {
        // Arrange
        RouterBuilder builder = new();

        // Act & Assert
        Should.Throw<RoutePatternException>(() => builder.MapGroup("{unclosed"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - MapGroup: Duplicate parameter across nesting levels throws at creation")]
    public void MapGroup_NestedDuplicateParameter_ShouldThrowAtCreation()
    {
        // Arrange
        RouterBuilder builder = new();
        IRouterGroupBuilder outer = builder.MapGroup("{id}");

        // Act & Assert — the composed prefix is validated when the nested group is created,
        // not deferred to the first child registration.
        Should.Throw<RoutePatternException>(() => outer.MapGroup("{id}"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Map: Unknown inline policy in the composed template throws at registration")]
    public void Map_UnknownPolicyName_ShouldThrowAtRegistration()
    {
        // Arrange
        RouterBuilder builder = new();
        IRouterGroupBuilder group = builder.MapGroup("api");

        // Act & Assert — unresolved policy references fail at builder time, not at request time.
        Should.Throw<InvalidOperationException>(() =>
            group.Map(HttpMethod.Get, "{id:unregistered}", new RecordingRouterRouteHandler()));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Map: Empty child template maps the group prefix itself")]
    public void Map_EmptyTemplate_ShouldMapGroupPrefixItself()
    {
        // Arrange
        RouterBuilder builder = new();
        RecordingRouterRouteHandler handler = new();

        builder.MapGroup("api/v1").Map(HttpMethod.Get, string.Empty, handler);

        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/v1"));

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route!.Handler.ShouldBeSameAs(handler);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - MapGroup: Empty prefix shares configuration without prefixing")]
    public void MapGroup_EmptyPrefix_ShouldShareConfigurationWithoutPrefix()
    {
        // Arrange
        RouterBuilder builder = new();
        TestMetadata shared = new("shared");

        builder.MapGroup(string.Empty)
            .WithMetadata(shared)
            .Map(HttpMethod.Get, "health", new RecordingRouterRouteHandler());

        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/health"));

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route!.Metadata.GetMetadata<TestMetadata>().ShouldBeSameAs(shared);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - MapGroup: Leading and trailing separators are normalized")]
    public void MapGroup_SeparatorNormalization_ShouldComposeCleanTemplates()
    {
        // Arrange
        RouterBuilder builder = new();
        RecordingRouterRouteHandler handler = new();

        IRouterGroupBuilder group = builder.MapGroup("/api/");
        group.Prefix.ShouldBe("api");
        group.Map(HttpMethod.Get, "/orders/", handler);

        IRouter router = builder.Build();

        // Act
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/orders"));

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeOfType<Route>().Pattern.RawText.ShouldBe("api/orders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - WithMetadata: Group-level host metadata constrains child routes")]
    public void WithMetadata_GroupHostMetadata_ShouldConstrainChildRoutes()
    {
        // Arrange — the sealed RouteHostMetadata carrier rides the group's shared metadata like
        // any other item; the router consults it on the composed child route.
        RouterBuilder builder = new();

        builder.MapGroup("api")
            .WithMetadata(new RouteHostMetadata("api.example.com"))
            .Map(HttpMethod.Get, "data", new RecordingRouterRouteHandler());

        IRouter router = builder.Build();

        // Act
        RouteMatch matchingHost = router.Match(CreateContext(HttpMethod.Get, "/api/data", "api.example.com"));
        RouteMatch otherHost = router.Match(CreateContext(HttpMethod.Get, "/api/data", "www.example.com"));

        // Assert
        matchingHost.Status.ShouldBe(RouteMatchStatus.Matched);
        otherHost.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - WithMetadata: Route-level host metadata replaces the group's, not merges")]
    public void WithMetadata_ChildHostMetadata_ShouldReplaceGroupHostMetadata()
    {
        // Arrange — the router resolves RouteHostMetadata last-wins, so a route-level declaration
        // overrides the group's rather than OR-combining with it.
        RouterBuilder builder = new();

        builder.MapGroup("api")
            .WithMetadata(new RouteHostMetadata("group.example.com"))
            .Map(
                HttpMethod.Get,
                "data",
                new RecordingRouterRouteHandler(),
                new RouterRouteMetadataCollection(new RouteHostMetadata("endpoint.example.com")));

        IRouter router = builder.Build();

        // Act
        RouteMatch endpointHost = router.Match(CreateContext(HttpMethod.Get, "/api/data", "endpoint.example.com"));
        RouteMatch groupHost = router.Match(CreateContext(HttpMethod.Get, "/api/data", "group.example.com"));

        // Assert
        endpointHost.Status.ShouldBe(RouteMatchStatus.Matched);
        groupHost.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - MapGroup: Sibling groups do not share configuration")]
    public void MapGroup_SiblingGroups_ShouldNotShareConfiguration()
    {
        // Arrange — each nested group snapshots its parent; siblings stay isolated.
        RouterBuilder builder = new();
        TestMetadata v1Metadata = new("v1");

        IRouterGroupBuilder api = builder.MapGroup("api");
        IRouterGroupBuilder v1 = api.MapGroup("v1").WithMetadata(v1Metadata);
        IRouterGroupBuilder v2 = api.MapGroup("v2");

        v1.Map(HttpMethod.Get, "orders", new RecordingRouterRouteHandler());
        v2.Map(HttpMethod.Get, "orders", new RecordingRouterRouteHandler());

        IRouter router = builder.Build();

        // Act
        RouteMatch v1Match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/v1/orders"));
        RouteMatch v2Match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/v2/orders"));

        // Assert
        v1Match.Route!.Metadata.GetMetadata<TestMetadata>().ShouldBeSameAs(v1Metadata);
        v2Match.Route!.Metadata.GetMetadata<TestMetadata>().ShouldBeNull();
    }
}
