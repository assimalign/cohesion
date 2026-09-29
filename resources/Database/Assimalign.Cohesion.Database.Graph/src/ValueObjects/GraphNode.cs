using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// A node in a property graph: identity, labels, and properties.
/// </summary>
/// <param name="Id">The node identity.</param>
/// <param name="Labels">The labels applied to the node.</param>
/// <param name="Properties">The node's properties.</param>
public readonly record struct GraphNode(
    GraphNodeId Id,
    IReadOnlyList<string> Labels,
    IReadOnlyDictionary<string, object?> Properties);
