using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A path of nodes, relationships and parenthesized segments in source order.</summary>
/// <param name="Nodes">The ordered node patterns for a path without parenthesized elements; otherwise empty.</param>
/// <param name="Relationships">The relationships for a path without parenthesized elements; otherwise empty.</param>
public sealed record GqlPathPattern(IReadOnlyList<GqlNodePattern> Nodes,
    IReadOnlyList<GqlRelationshipPattern> Relationships)
{
    /// <summary>
    /// Gets the source-ordered elements, or null to derive them from Nodes and Relationships.
    /// When present without a parenthesized element, both representations must agree.
    /// A path with parenthesized elements carries empty Nodes and Relationships.
    /// </summary>
    public IReadOnlyList<GqlPathElement>? Elements { get; init; }

    /// <summary>Gets the optional variable bound to the complete MATCH path.</summary>
    public string? Variable { get; init; }
}
