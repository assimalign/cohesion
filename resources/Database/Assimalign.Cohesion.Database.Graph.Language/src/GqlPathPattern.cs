using System.Collections.Generic;

using Assimalign.Cohesion.Database.Graph.Language.Internal;

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

    /// <summary>Gets the optional outer MATCH selector. Omission preserves exhaustive matching.</summary>
    public GqlPathSelector? Selector { get; init; }

    /// <summary>Gets the parenthesized path's WHERE prefilter, evaluated inside path search.</summary>
    public GqlExpression? Predicate { get; init; }

    /// <summary>
    /// Gets the explicit mode, or null for implicit TRAIL. Only the outer MATCH path may
    /// specify a mode; it governs every relationship and parenthesized or quantified segment.
    /// </summary>
    public GqlPathMode? Mode { get; init; }

    /// <summary>Renders the complete path as GQL, including its binding, selector, mode, predicates and repetition bounds.</summary>
    /// <returns>Canonical path syntax; source locations and optional keyword spellings do not affect rendering.</returns>
    public override string ToString() => GqlPathRenderer.Render(this);
}
