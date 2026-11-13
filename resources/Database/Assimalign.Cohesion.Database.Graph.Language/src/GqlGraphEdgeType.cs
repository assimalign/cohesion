using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A directed edge element declaration in a graph-type command.</summary>
/// <param name="Type">The relationship type.</param>
/// <param name="Source">The source node type's identifying label.</param>
/// <param name="Target">The target node type's identifying label.</param>
/// <param name="Properties">Scalar property declarations.</param>
public sealed record GqlGraphEdgeType(string Type, string Source, string Target, IReadOnlyList<GqlGraphPropertyType> Properties);
