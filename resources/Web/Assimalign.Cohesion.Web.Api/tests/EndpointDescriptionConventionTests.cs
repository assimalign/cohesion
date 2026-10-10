using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Web.Api.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Api.Tests;

/// <summary>
/// The endpoint-description convention verbs (#152): <c>WithTags</c>, <c>WithSummary</c>,
/// <c>WithDescription</c> and <c>ExcludeFromDescription</c> attach neutral description carriers to routes
/// and groups, composed when the route table is built.
/// </summary>
public class EndpointDescriptionConventionTests
{
    private static WebApplicationTestFactory CreateFactory()
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddJsonSerialization(ApiTestJsonContext.Default);
        return factory;
    }

    // Builds the route table, which composes group and route metadata, and finds a named route.
    private static IRouterRoute GetRoute(WebApplicationTestFactory factory, string routeName)
    {
        IRouter router = factory.Application.Context.Features.OfType<IRouterFeature>().Single().Router;

        return router.Routes.Single(route => route.Metadata.GetMetadata<RouteNameMetadata>()?.RouteName == routeName);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description conventions: tags from a group and its route compose, outer first")]
    public async Task WithTags_OnGroupAndRoute_ShouldComposeOuterFirst()
    {
        // Arrange — the group's tags are attached after its child is mapped.
        await using WebApplicationTestFactory factory = CreateFactory();
        IRouterGroupBuilder api = factory.Application.MapGroup("api");
        api.MapGet("orders/{id:long}", (long id) => new Order(id, "book")).WithName("order").WithTags("orders", "api");
        api.WithTags("api");

        // Act
        IReadOnlyList<EndpointTagsMetadata> tags = GetRoute(factory, "order").Metadata.GetOrderedMetadata<EndpointTagsMetadata>();

        // Assert
        tags.Count.ShouldBe(2);
        tags[0].Tags.ShouldBe(["api"]);
        tags[1].Tags.ShouldBe(["orders", "api"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description conventions: the route's summary and description override its group's")]
    public async Task WithSummaryAndDescription_RouteOverGroup_ShouldWin()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory();
        IRouterGroupBuilder api = factory.Application.MapGroup("api")
            .WithSummary("Group summary")
            .WithDescription("Group description");
        api.MapGet("orders/{id:long}", (long id) => new Order(id, "book"))
            .WithName("order")
            .WithSummary("Gets an order")
            .WithDescription("Returns the order with the **given** identifier.");
        api.MapGet("health", () => "ok").WithName("health");

        // Act
        IRouterRouteMetadataCollection order = GetRoute(factory, "order").Metadata;
        IRouterRouteMetadataCollection health = GetRoute(factory, "health").Metadata;

        // Assert
        order.GetMetadata<EndpointSummaryMetadata>().ShouldNotBeNull().Summary.ShouldBe("Gets an order");
        order.GetMetadata<EndpointDescriptionMetadata>().ShouldNotBeNull().Description.ShouldBe("Returns the order with the **given** identifier.");
        health.GetMetadata<EndpointSummaryMetadata>().ShouldNotBeNull().Summary.ShouldBe("Group summary");
        health.GetMetadata<EndpointDescriptionMetadata>().ShouldNotBeNull().Description.ShouldBe("Group description");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description conventions: excluding a group marks every child")]
    public async Task ExcludeFromDescription_OnGroup_ShouldMarkEveryChild()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory();
        IRouterGroupBuilder internals = factory.Application.MapGroup("internal").ExcludeFromDescription();
        internals.MapGet("cache", () => "cleared").WithName("cache");
        factory.Application.MapGet("/public", () => "hello").WithName("public");

        // Act
        IRouterRouteMetadataCollection cache = GetRoute(factory, "cache").Metadata;
        IRouterRouteMetadataCollection open = GetRoute(factory, "public").Metadata;

        // Assert
        cache.GetMetadata<ExcludeFromDescriptionMetadata>().ShouldBeSameAs(ExcludeFromDescriptionMetadata.Instance);
        open.GetMetadata<ExcludeFromDescriptionMetadata>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description conventions: verbs return the receiver's own builder type")]
    public async Task Verbs_OnRouteBuilder_ShouldChainWithRouteVerbs()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory();

        // Act — WithName is a route-builder-only verb, so this compiles only if every verb returns IRouterRouteBuilder.
        IRouterRouteBuilder route = factory.Application.MapGet("/ping", () => "pong")
            .WithTags("diagnostics")
            .WithSummary("Ping")
            .WithDescription("Answers pong.")
            .ExcludeFromDescription()
            .WithName("ping");

        // Assert
        route.ShouldNotBeNull();
        GetRoute(factory, "ping").Metadata.GetMetadata<EndpointTagsMetadata>().ShouldNotBeNull().Tags.ShouldBe(["diagnostics"]);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Api] - Description conventions: a blank tag, summary or description is rejected")]
    [InlineData("")]
    [InlineData("  ")]
    public void Carriers_BlankText_ShouldThrow(string text)
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentException>(() => new EndpointTagsMetadata("orders", text));
        Should.Throw<ArgumentException>(() => new EndpointSummaryMetadata(text));
        Should.Throw<ArgumentException>(() => new EndpointDescriptionMetadata(text));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description conventions: tags require at least one name")]
    public void EndpointTagsMetadata_NoTags_ShouldThrow()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentException>(() => new EndpointTagsMetadata());
        Should.Throw<ArgumentNullException>(() => new EndpointTagsMetadata(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description conventions: tag metadata keeps a copy of the declared tags")]
    public void EndpointTagsMetadata_SourceArrayChanged_ShouldKeepDeclaredTags()
    {
        // Arrange
        string[] declared = ["orders"];
        EndpointTagsMetadata tags = new(declared);

        // Act
        declared[0] = "changed";

        // Assert
        tags.Tags.ShouldBe(["orders"]);
    }
}
