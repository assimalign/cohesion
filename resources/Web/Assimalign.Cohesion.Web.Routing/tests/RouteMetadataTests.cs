using System;

using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RouteMetadataTests
{
    private sealed class SampleMetadata
    {
        public SampleMetadata(string value) => Value = value;

        public string Value { get; }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Route: Exposes empty metadata by default")]
    public void Route_WithoutMetadata_ShouldExposeEmptyMetadata()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/users/{id:int}");

        // Assert
        route.Metadata.ShouldNotBeNull();
        route.Metadata.Count.ShouldBe(0);
        route.Metadata.ShouldBeSameAs(RouterRouteMetadataCollection.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Route: Carries endpoint metadata supplied at construction")]
    public void Route_WithMetadata_ShouldExposeMetadata()
    {
        // Arrange
        SampleMetadata sample = new("sample");
        RouterRouteMetadataCollection metadata = new(sample);
        Route route = new(HttpMethod.Get, "/users/{id:int}", new RecordingRouterRouteHandler(), metadata);

        // Assert
        route.Metadata.Count.ShouldBe(1);
        route.Metadata.GetMetadata<SampleMetadata>().ShouldBeSameAs(sample);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Route: Surfaces metadata through the IRouterRoute contract")]
    public void Route_AsIRouterRoute_ShouldExposeMetadata()
    {
        // Arrange
        SampleMetadata sample = new("sample");
        IRouterRoute route = new Route(HttpMethod.Get, "/orders", new RecordingRouterRouteHandler(), new RouterRouteMetadataCollection(sample));

        // Assert
        route.Metadata.GetMetadata<SampleMetadata>().ShouldBeSameAs(sample);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Route: Rejects null metadata")]
    public void Route_WithNullMetadata_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() =>
            new Route(HttpMethod.Get, "/x", new RecordingRouterRouteHandler(), null!));
    }
}
