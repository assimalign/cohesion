using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>A matched path containing ordered graph entities from one statement snapshot.</summary>
/// <param name="Nodes">The nodes in traversal order, including repeated nodes.</param>
/// <param name="Relationships">The relationships connecting consecutive nodes, retaining their stored directions.</param>
/// <remarks>A path contains at least one node and one fewer relationships than nodes.</remarks>
public sealed record GraphPath(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphRelationship> Relationships);
