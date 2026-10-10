using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.OpenApi.Tests.TestObjects;

/// <summary>An order's fulfilment state, serialized as its number by default.</summary>
internal enum OrderStatus
{
    Pending,
    Shipped
}

/// <summary>A postal address; its city is optional.</summary>
internal sealed record Address(string Street, string? City);

/// <summary>A customer with a list of addresses.</summary>
internal sealed class Customer
{
    public required string Name { get; init; }

    public List<Address> Addresses { get; init; } = [];
}

/// <summary>One line of an order.</summary>
internal sealed record OrderLine(string Sku, int Quantity);

/// <summary>The order the API returns.</summary>
internal sealed record Order(long Id, string Item, decimal Total, OrderStatus Status, Customer? Customer, IReadOnlyList<OrderLine> Lines, DateTimeOffset Created);

/// <summary>The body that creates an order.</summary>
internal sealed record CreateOrder(string Item, int Quantity);

/// <summary>A self-referencing tree node.</summary>
internal sealed class TreeNode
{
    public int Value { get; init; }

    public TreeNode? Parent { get; init; }

    public List<TreeNode> Children { get; init; } = [];
}

/// <summary>A polymorphic shape with a type discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Circle), "circle")]
[JsonDerivedType(typeof(Square), "square")]
internal abstract class Shape
{
    public string? Label { get; init; }
}

/// <summary>A circle.</summary>
internal sealed class Circle : Shape
{
    public double Radius { get; init; }
}

/// <summary>A square.</summary>
internal sealed class Square : Shape
{
    public double Side { get; init; }
}

/// <summary>A value-type object.</summary>
internal struct Point
{
    public int X { get; set; }

    public int Y { get; set; }
}

/// <summary>A payload exercising collections, dictionaries, enums, binary data and value-type objects.</summary>
internal sealed class Envelope
{
    public Dictionary<string, List<int>> Buckets { get; init; } = [];

    public List<List<int>> Matrix { get; init; } = [];

    [JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
    public OrderStatus Named { get; init; }

    public OrderStatus? Maybe { get; init; }

    public byte[] Blob { get; init; } = [];

    public Point Where { get; init; }

    public Point? MaybeWhere { get; init; }
}

/// <summary>A generic page of results.</summary>
internal sealed record Page<T>(IReadOnlyList<T> Items, int Total);

/// <summary>A type deliberately absent from <see cref="OpenApiTestJsonContext"/>.</summary>
internal sealed record Unregistered(string Value);

/// <summary>
/// The source-generated serialization contracts the test applications register with
/// <c>AddJsonSerialization</c>: the same reflection-free resolver shape a NativeAOT application uses.
/// </summary>
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(IReadOnlyList<Order>))]
[JsonSerializable(typeof(CreateOrder))]
[JsonSerializable(typeof(TreeNode))]
[JsonSerializable(typeof(Shape))]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(Page<Order>))]
[JsonSerializable(typeof(int))]
internal sealed partial class OpenApiTestJsonContext : JsonSerializerContext;
