using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Api.Tests.TestObjects;

/// <summary>A body model bound from JSON in the binding end-to-end tests.</summary>
internal sealed record Widget(string Name, int Quantity);

/// <summary>A type deliberately absent from <see cref="ApiTestJsonContext"/>, for 415 tests.</summary>
internal sealed record Unregistered(string Value);

/// <summary>
/// The source-generated serialization contracts for the binding test models — the
/// <c>JsonTypeInfo</c> resolver shape applications register with <c>AddJsonSerialization</c>.
/// </summary>
[JsonSerializable(typeof(Widget))]
internal sealed partial class ApiTestJsonContext : JsonSerializerContext;
