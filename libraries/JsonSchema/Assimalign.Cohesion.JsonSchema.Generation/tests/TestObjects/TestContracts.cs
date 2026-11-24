using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

/// <summary>The source-generated contracts of every test type, with the serializer's default options.</summary>
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(List<Order>))]
[JsonSerializable(typeof(Customer))]
[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(Report))]
[JsonSerializable(typeof(Shape))]
[JsonSerializable(typeof(Animal))]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(Node))]
[JsonSerializable(typeof(TreeNode))]
[JsonSerializable(typeof(NodePair))]
[JsonSerializable(typeof(Nested))]
[JsonSerializable(typeof(NestedPair))]
[JsonSerializable(typeof(Point))]
[JsonSerializable(typeof(Segment))]
[JsonSerializable(typeof(Route))]
[JsonSerializable(typeof(Invoice))]
[JsonSerializable(typeof(Strict))]
[JsonSerializable(typeof(Flexible))]
[JsonSerializable(typeof(Primitives))]
[JsonSerializable(typeof(Stock))]
[JsonSerializable(typeof(Catalog.Item), TypeInfoPropertyName = "CatalogItem")]
[JsonSerializable(typeof(Inventory.Item), TypeInfoPropertyName = "InventoryItem")]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class TestContracts : JsonSerializerContext
{
}

/// <summary>The web defaults: camelCase names, numbers read from strings, and enums written as strings.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Order))]
internal sealed partial class WebContracts : JsonSerializerContext
{
}

/// <summary>A naming policy: snake_case member names.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Order))]
internal sealed partial class SnakeCaseContracts : JsonSerializerContext
{
}
