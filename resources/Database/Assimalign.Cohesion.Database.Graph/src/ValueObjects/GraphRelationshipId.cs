using System;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// A strongly-typed relationship identity, unique within a graph database.
/// </summary>
/// <param name="Value">The underlying identity value.</param>
public readonly record struct GraphRelationshipId(ulong Value) : IComparable<GraphRelationshipId>
{
    /// <inheritdoc />
    public int CompareTo(GraphRelationshipId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
