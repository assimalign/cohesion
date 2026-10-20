using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>A validated statement with each MATCH pattern's anchor.</summary>
/// <param name="Query">The validated statement.</param>
/// <param name="MatchClauses">The clause-ordered joins, filters and null-extension bindings.</param>
internal sealed record GraphPlan(GqlQueryExpression Query, IReadOnlyList<GraphMatchClausePlan> MatchClauses)
{
    internal IReadOnlyList<GraphProjectionPlan> Projections { get; init; } = [];
    internal IReadOnlyList<GraphStagePlan> Stages { get; init; } = [];
    internal bool Writes { get; init; }
    internal GraphDistinctPlan? Distinct { get; init; }
    internal GraphGroupingPlan? Grouping { get; init; }
    internal GraphSortPagePlan? SortPage { get; init; }
    /// <summary>Gets whether any mandatory MATCH stage is statically unsatisfiable. Execution uses the per-clause flags.</summary>
    internal bool MatchesNothing { get; init; }

    /// <summary>The normalized, execution-local snapshot of named scalar bindings.</summary>
    internal IReadOnlyDictionary<string, object?>? Parameters { get; init; }

    /// <summary>
    /// Gets the <c>COHDBG010</c>/<c>COHDBG011</c> warnings of a read-only statement that names a
    /// label or relationship type the database does not have, in first-mention order; empty for a
    /// statement that writes (#1228).
    /// </summary>
    internal IReadOnlyList<Diagnostic> Warnings { get; init; } = [];
}
