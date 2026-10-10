using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Serialization.Tests;

/// <summary>
/// The read-only contract seam (<c>TryGetJsonTypeInfo</c>) that lets a describer see the exact
/// System.Text.Json contracts the built-in JSON writer serializes with.
/// </summary>
public class JsonContractLookupTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - TryGetJsonTypeInfo: Should return the writer's contract with the web defaults")]
    public void TryGetJsonTypeInfo_RegisteredType_ShouldReturnWriterContract()
    {
        // Arrange
        IHttpContentSerializationFeature feature = new ContentSerializationBuilder().AddJson(TestJsonContext.Default).Build();

        // Act
        bool found = feature.TryGetJsonTypeInfo(typeof(TestOrder), out JsonTypeInfo? typeInfo);

        // Assert
        found.ShouldBeTrue();
        typeInfo.ShouldNotBeNull();
        typeInfo.Type.ShouldBe(typeof(TestOrder));
        typeInfo.Options.IsReadOnly.ShouldBeTrue();
        typeInfo.Properties.Select(property => property.Name).ShouldBe(["id", "quantity"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - TryGetJsonTypeInfo: Should reflect the options the registration callback configured")]
    public void TryGetJsonTypeInfo_ConfiguredOptions_ShouldMatchTheWire()
    {
        // Arrange
        IHttpContentSerializationFeature feature = new ContentSerializationBuilder().AddJson(TestJsonContext.Default, options => options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower).Build();

        // Act
        bool found = feature.TryGetJsonTypeInfo(typeof(TestReceipt), out JsonTypeInfo? typeInfo);

        // Assert
        found.ShouldBeTrue();
        typeInfo.ShouldNotBeNull().Properties.Select(property => property.Name).ShouldBe(["order_id", "total", "expedited"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - TryGetJsonTypeInfo: Should report a type the resolver does not cover")]
    public void TryGetJsonTypeInfo_UnregisteredType_ShouldReturnFalse()
    {
        // Arrange
        IHttpContentSerializationFeature feature = new ContentSerializationBuilder().AddJson(TestJsonContext.Default).Build();

        // Act
        bool found = feature.TryGetJsonTypeInfo(typeof(UnregisteredModel), out JsonTypeInfo? typeInfo);

        // Assert
        found.ShouldBeFalse();
        typeInfo.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - TryGetJsonTypeInfo: Should report no contract when application/json resolves to another writer")]
    public void TryGetJsonTypeInfo_CustomJsonWriterFirst_ShouldReturnFalse()
    {
        // Arrange — the custom writer is registered first, so application/json resolves to it.
        IHttpContentSerializationFeature feature = new ContentSerializationBuilder()
            .AddWriter(new FakeContentWriter(HttpMediaType.ApplicationJson))
            .AddJson(TestJsonContext.Default)
            .Build();

        // Act
        bool found = feature.TryGetJsonTypeInfo(typeof(TestOrder), out JsonTypeInfo? typeInfo);

        // Assert
        found.ShouldBeFalse();
        typeInfo.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - TryGetJsonTypeInfo: Should report no contract when no JSON format is registered")]
    public void TryGetJsonTypeInfo_NoJsonWriter_ShouldReturnFalse()
    {
        // Arrange
        IHttpContentSerializationFeature feature = new ContentSerializationBuilder()
            .AddWriter(new FakeContentWriter(HttpMediaType.TextPlain))
            .Build();

        // Act
        bool found = feature.TryGetJsonTypeInfo(typeof(TestOrder), out JsonTypeInfo? typeInfo);

        // Assert
        found.ShouldBeFalse();
        typeInfo.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - TryGetJsonTypeInfo: Should reject a null type")]
    public void TryGetJsonTypeInfo_NullType_ShouldThrow()
    {
        // Arrange
        IHttpContentSerializationFeature feature = new ContentSerializationBuilder().AddJson(TestJsonContext.Default).Build();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => feature.TryGetJsonTypeInfo(null!, out _));
    }
}
