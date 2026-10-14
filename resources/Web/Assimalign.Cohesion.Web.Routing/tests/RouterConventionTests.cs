using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

/// <summary>
/// The endpoint convention builders (#1055): <c>Map</c> returns an <see cref="IRouterRouteBuilder"/>,
/// groups are <see cref="IRouterConventionBuilder"/>s, and metadata attached through either composes
/// when the route table is built, whatever order the calls were made in.
/// </summary>
public class RouterConventionTests
{
    private sealed class TestMetadata
    {
        public TestMetadata(string value) => Value = value;

        public string Value { get; }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Conventions: Metadata attached after Map reaches the route")]
    public void WithMetadata_AfterMap_ShouldApplyToRoute()
    {
        // Arrange
        RouterBuilder builder = new();
        IRouterRouteBuilder route = builder.Map(HttpMethod.Get, "/orders/{id:int}", new RecordingRouterRouteHandler());
        TestMetadata metadata = new("route");

        // Act
        route.WithMetadata(metadata);
        IRouter router = builder.Build();

        // Assert
        RouteMatch match = router.Match(TestHttpContext.Create(HttpMethod.Get, "/orders/7"));
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route!.Metadata.GetMetadata<TestMetadata>().ShouldBeSameAs(metadata);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Conventions: WithName names the route for link generation")]
    public void WithName_OnRoute_ShouldGenerateLinks()
    {
        // Arrange
        RouterBuilder builder = new();
        builder.Map(HttpMethod.Get, "/users/{id:int}", new RecordingRouterRouteHandler()).WithName("user");

        // Act
        IRouter router = builder.Build();

        // Assert
        router.LinkGenerator.GetPathByName("user", new RouteValueDictionary { ["id"] = 42 }).ShouldBe("/users/42");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Conventions: A duplicate WithName fails when the route table is built")]
    public void WithName_Duplicate_ShouldFailAtBuild()
    {
        // Arrange
        RouterBuilder builder = new();
        builder.Map(HttpMethod.Get, "/a", new RecordingRouterRouteHandler()).WithName("same");
        builder.Map(HttpMethod.Get, "/b", new RecordingRouterRouteHandler()).WithName("same");

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Conventions: RequireHost on a group constrains every child, including later ones")]
    public void RequireHost_OnGroup_ShouldConstrainChildrenMappedBeforeAndAfter()
    {
        // Arrange
        RouterBuilder builder = new();
        IRouterGroupBuilder group = builder.MapGroup("api");
        group.Map(HttpMethod.Get, "before", new RecordingRouterRouteHandler());
        IRouterGroupBuilder returned = group.RequireHost("api.example.com");
        group.Map(HttpMethod.Get, "after", new RecordingRouterRouteHandler());

        // Act
        IRouter router = builder.Build();

        // Assert — the generic verb returns the group's own builder type, and applies to both children.
        returned.ShouldBeSameAs(group);
        foreach (string path in new[] { "/api/before", "/api/after" })
        {
            TestHttpContext matching = TestHttpContext.Create(HttpMethod.Get, path);
            matching.Request.Host = new HttpHost("api.example.com");
            TestHttpContext other = TestHttpContext.Create(HttpMethod.Get, path);
            other.Request.Host = new HttpHost("www.example.com");

            router.Match(matching).Status.ShouldBe(RouteMatchStatus.Matched);
            router.Match(other).Status.ShouldBe(RouteMatchStatus.NoMatch);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Conventions: RequireHost on a route returns the route builder")]
    public void RequireHost_OnRoute_ShouldReturnRouteBuilderAndConstrainRoute()
    {
        // Arrange
        RouterBuilder builder = new();
        IRouterRouteBuilder route = builder.Map(HttpMethod.Get, "/admin", new RecordingRouterRouteHandler());

        // Act
        IRouterRouteBuilder returned = route.RequireHost("admin.example.com");
        IRouter router = builder.Build();

        // Assert
        returned.ShouldBeSameAs(route);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/admin");
        context.Request.Host = new HttpHost("public.example.com");
        router.Match(context).Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Conventions: Composition order is outer group, inner group, then route, whatever the call order")]
    public void Metadata_CallsInReverseOrder_ShouldComposeOuterInnerRoute()
    {
        // Arrange — attach in the opposite order to the composition order.
        RouterBuilder builder = new();
        IRouterGroupBuilder outerGroup = builder.MapGroup("api");
        IRouterGroupBuilder innerGroup = outerGroup.MapGroup("v1");
        IRouterRouteBuilder route = innerGroup.Map(HttpMethod.Get, "orders", new RecordingRouterRouteHandler());
        TestMetadata outer = new("outer");
        TestMetadata inner = new("inner");
        TestMetadata own = new("route");

        route.WithMetadata(own);
        innerGroup.WithMetadata(inner);
        outerGroup.WithMetadata(outer);

        // Act
        IRouter router = builder.Build();

        // Assert
        IRouterRouteMetadataCollection metadata = router.Match(TestHttpContext.Create(HttpMethod.Get, "/api/v1/orders")).Route!.Metadata;
        IReadOnlyList<TestMetadata> ordered = metadata.GetOrderedMetadata<TestMetadata>();
        ordered.Count.ShouldBe(3);
        ordered[0].ShouldBeSameAs(outer);
        ordered[1].ShouldBeSameAs(inner);
        ordered[2].ShouldBeSameAs(own);
        metadata.GetMetadata<TestMetadata>().ShouldBeSameAs(own);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Conventions: Null metadata items are rejected")]
    public void WithMetadata_NullItem_ShouldThrow()
    {
        // Arrange
        RouterBuilder builder = new();
        IRouterRouteBuilder route = builder.Map(HttpMethod.Get, "/x", new RecordingRouterRouteHandler());

        // Act & Assert
        Should.Throw<ArgumentException>(() => route.WithMetadata(new object[] { null! }));
        Should.Throw<ArgumentNullException>(() => route.WithMetadata(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Conventions: Mapping a template route after the build throws")]
    public void Map_AfterBuild_ShouldThrow()
    {
        // Arrange
        RouterBuilder builder = new();
        builder.Build();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.Map(HttpMethod.Get, "/late", new RecordingRouterRouteHandler()));
    }
}
