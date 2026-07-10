using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Web.Routing.Metadata;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RouterRouteMetadataCollectionTests
{
    private interface ITestMetadata
    {
        string Value { get; }
    }

    private sealed class AuthMetadata : ITestMetadata
    {
        public AuthMetadata(string value) => Value = value;

        public string Value { get; }
    }

    private sealed class CorsMetadata : ITestMetadata
    {
        public CorsMetadata(string value) => Value = value;

        public string Value { get; }
    }

    private sealed class UnrelatedMetadata
    {
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - EndpointMetadata: Empty exposes zero items")]
    public void Empty_ShouldExposeNoItems()
    {
        // Act
        IRouterRouteMetadataCollection metadata = RouterRouteMetadataCollection.Empty;

        // Assert
        metadata.Count.ShouldBe(0);
        metadata.GetMetadata<ITestMetadata>().ShouldBeNull();
        metadata.GetOrderedMetadata<ITestMetadata>().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - EndpointMetadata: Preserves registration order and count")]
    public void Indexer_ShouldPreserveRegistrationOrder()
    {
        // Arrange
        AuthMetadata first = new("a");
        CorsMetadata second = new("b");

        // Act
        RouterRouteMetadataCollection metadata = new(first, second);

        // Assert
        metadata.Count.ShouldBe(2);
        metadata[0].ShouldBeSameAs(first);
        metadata[1].ShouldBeSameAs(second);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - EndpointMetadata: GetMetadata returns the last assignable item")]
    public void GetMetadata_OnMultipleMatches_ShouldReturnLastWins()
    {
        // Arrange
        AuthMetadata first = new("first");
        AuthMetadata last = new("last");
        RouterRouteMetadataCollection metadata = new(first, new CorsMetadata("cors"), last);

        // Act
        AuthMetadata? resolved = metadata.GetMetadata<AuthMetadata>();

        // Assert
        resolved.ShouldBeSameAs(last);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - EndpointMetadata: GetMetadata resolves by assignable interface")]
    public void GetMetadata_ByInterface_ShouldResolveMostRecentAssignable()
    {
        // Arrange
        RouterRouteMetadataCollection metadata = new(new AuthMetadata("auth"), new CorsMetadata("cors"));

        // Act
        ITestMetadata? resolved = metadata.GetMetadata<ITestMetadata>();

        // Assert
        resolved.ShouldNotBeNull();
        resolved!.Value.ShouldBe("cors");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - EndpointMetadata: GetMetadata returns null when absent")]
    public void GetMetadata_WhenAbsent_ShouldReturnNull()
    {
        // Arrange
        RouterRouteMetadataCollection metadata = new(new AuthMetadata("auth"));

        // Act & Assert
        metadata.GetMetadata<UnrelatedMetadata>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - EndpointMetadata: GetOrderedMetadata returns all matches in order")]
    public void GetOrderedMetadata_ShouldReturnAllMatchesInOrder()
    {
        // Arrange
        AuthMetadata first = new("first");
        CorsMetadata middle = new("middle");
        AuthMetadata third = new("third");
        RouterRouteMetadataCollection metadata = new(first, middle, third);

        // Act
        IReadOnlyList<ITestMetadata> ordered = metadata.GetOrderedMetadata<ITestMetadata>();
        IReadOnlyList<AuthMetadata> auth = metadata.GetOrderedMetadata<AuthMetadata>();

        // Assert
        ordered.Select(m => m.Value).ShouldBe(new[] { "first", "middle", "third" });
        auth.ShouldBe(new[] { first, third });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - EndpointMetadata: Enumeration yields items in order")]
    public void Enumeration_ShouldYieldItemsInRegistrationOrder()
    {
        // Arrange
        object first = new AuthMetadata("first");
        object second = new CorsMetadata("second");
        RouterRouteMetadataCollection metadata = new(first, second);

        // Act
        List<object> collected = new();
        foreach (object item in metadata)
        {
            collected.Add(item);
        }

        // Assert
        collected.ShouldBe(new[] { first, second });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - EndpointMetadata: Rejects null items")]
    public void Constructor_OnNullItem_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => new RouterRouteMetadataCollection(new object[] { new AuthMetadata("a"), null! }));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - EndpointMetadata: Rejects null source sequence")]
    public void Constructor_OnNullSequence_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => new RouterRouteMetadataCollection((IEnumerable<object>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - EndpointMetadata: Copies source array defensively")]
    public void Constructor_ShouldCopySourceArrayDefensively()
    {
        // Arrange
        AuthMetadata original = new("original");
        object[] source = { original };
        RouterRouteMetadataCollection metadata = new(source);

        // Act
        source[0] = new AuthMetadata("mutated");

        // Assert
        metadata[0].ShouldBeSameAs(original);
    }
}
