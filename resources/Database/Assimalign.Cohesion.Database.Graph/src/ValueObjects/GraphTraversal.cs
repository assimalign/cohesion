using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// A traversal specification: where to start, which relationships to follow,
/// in which direction, and how deep.
/// </summary>
/// <param name="Start">The node the traversal starts from.</param>
/// <param name="Direction">The relationship direction to follow.</param>
/// <param name="RelationshipType">When set, only relationships of this type are followed.</param>
/// <param name="MaxDepth">The maximum number of hops from the start node.</param>
public readonly record struct GraphTraversal(
    GraphNodeId Start,
    GraphDirection Direction = GraphDirection.Outgoing,
    string? RelationshipType = null,
    int MaxDepth = 1);
