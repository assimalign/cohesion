using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// A typed, directed relationship between two nodes in a property graph.
/// </summary>
/// <param name="Id">The relationship identity.</param>
/// <param name="Type">The relationship type.</param>
/// <param name="From">The origin node.</param>
/// <param name="To">The target node.</param>
/// <param name="Properties">The relationship's properties.</param>
public readonly record struct GraphRelationship(
    GraphRelationshipId Id,
    string Type,
    GraphNodeId From,
    GraphNodeId To,
    IReadOnlyDictionary<string, object?> Properties);
