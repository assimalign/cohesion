using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphAnchor(int NodeIndex, string? Label, string? Property, object? Value, IReadOnlyList<string>? UnionLabels = null)
{
    internal IReadOnlyList<string>? RelationshipTypes { get; init; }
    internal long? EstimatedRows { get; init; }
}
