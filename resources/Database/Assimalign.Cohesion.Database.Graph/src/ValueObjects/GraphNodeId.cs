using System;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// A strongly-typed node identity, unique within a graph database.
/// </summary>
/// <param name="Value">The underlying identity value.</param>
public readonly record struct GraphNodeId(ulong Value) : IComparable<GraphNodeId>
{
    /// <inheritdoc />
    public int CompareTo(GraphNodeId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
