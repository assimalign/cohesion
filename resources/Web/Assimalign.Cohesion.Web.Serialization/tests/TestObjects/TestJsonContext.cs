using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Serialization.Tests.TestObjects;

/// <summary>An order model covered by the source-generated test contracts.</summary>
internal sealed record TestOrder(string Id, int Quantity);

/// <summary>A receipt model covered by the source-generated test contracts.</summary>
internal sealed record TestReceipt(string OrderId, decimal Total, bool Expedited);

/// <summary>A type deliberately absent from <see cref="TestJsonContext"/>, for contract-fault tests.</summary>
internal sealed record UnregisteredModel(string Value);

/// <summary>
/// The source-generated serialization contracts for the test models — the
/// <c>JsonTypeInfo</c>-resolver registration shape applications use with
/// <c>AddJsonSerialization</c>, so the tests exercise the same reflection-free path NativeAOT
/// runs.
/// </summary>
[JsonSerializable(typeof(TestOrder))]
[JsonSerializable(typeof(TestReceipt))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
