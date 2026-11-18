using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Fabric.Tests;

public class FabricRefTests
{
    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricRef: Should read a $ref object")]
    public void TryRead_RefObject_ShouldReadReference()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{ "$ref": "Cohesion.Database/databases/orders-db", "path": "outputs.connectionString" }""");

        // Act
        var found = FabricRef.TryRead(document.RootElement, out var reference);

        // Assert
        found.ShouldBeTrue();
        reference!.Type.ShouldBe(new ResourceTypeName("Cohesion.Database/databases"));
        reference.Name.ShouldBe(new ResourceName("orders-db"));
        reference.Path.ShouldBe("outputs.connectionString");
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricRef: Should treat a value without $ref as a literal")]
    public void TryRead_Literal_ShouldReturnFalse()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{ "origins": "https://shop.acme.example" }""");

        // Act & Assert
        FabricRef.TryRead(document.RootElement, out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Fabric] - FabricRef: Should refuse a malformed $ref object")]
    [InlineData("""{ "$ref": "Cohesion.Database/databases/orders-db", "extra": 1 }""")]
    [InlineData("""{ "$ref": 3 }""")]
    [InlineData("""{ "$ref": "orders-db" }""")]
    public void TryRead_MalformedRef_ShouldThrow(string json)
    {
        // Arrange
        using var document = JsonDocument.Parse(json);

        // Act & Assert
        Should.Throw<JsonException>(() => FabricRef.TryRead(document.RootElement, out _));
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricValue: Should round-trip a literal and a reference")]
    public void Converter_LiteralAndRef_ShouldRoundTrip()
    {
        // Arrange
        var options = new JsonSerializerOptions { Converters = { new FabricStringValueJsonConverter() } };
        FabricValue<string> literal = "https://shop.acme.example";
        FabricValue<string> reference = new FabricRef(new ResourceTypeName("Cohesion.Database/databases"), new ResourceName("orders-db"), "outputs.url");

        // Act
        var literalJson = JsonSerializer.Serialize(literal, options);
        var referenceJson = JsonSerializer.Serialize(reference, options);

        // Assert
        literalJson.ShouldBe("\"https://shop.acme.example\"");
        referenceJson.ShouldBe("""{"$ref":"Cohesion.Database/databases/orders-db","path":"outputs.url"}""");
        JsonSerializer.Deserialize<FabricValue<string>>(literalJson, options)!.Literal.ShouldBe("https://shop.acme.example");
        JsonSerializer.Deserialize<FabricValue<string>>(referenceJson, options)!.Ref.ShouldBe(reference.Ref);
    }
}
