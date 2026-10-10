using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Api.Tests.TestObjects;

/// <summary>A body model bound from JSON in the binding end-to-end tests.</summary>
internal sealed record Widget(string Name, int Quantity);

/// <summary>A type deliberately absent from <see cref="ApiTestJsonContext"/>, for the missing-contract fault tests (body and returned value).</summary>
internal sealed record Unregistered(string Value);

/// <summary>A response model the return-value end-to-end tests write through the serialization registry.</summary>
internal sealed record Order(long Id, string Item);

/// <summary>
/// The source-generated serialization contracts for the binding test models — the
/// <c>JsonTypeInfo</c> resolver shape applications register with <c>AddJsonSerialization</c>.
/// </summary>
[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(int))]
internal sealed partial class ApiTestJsonContext : JsonSerializerContext;
