using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Materializes uncorrelated child queries under the enclosing statement's snapshot,
/// then runs the bound input with those values available to scalar evaluation.
/// </summary>
/// <remarks>
/// The planner lowers each subquery's use site to a slot of the bound expression tree
/// (<see cref="SqlBoundSubquery"/>, <see cref="SqlBoundInSubquery"/>), numbered by the
/// binding that produces its values. The executor stores each child's values under that
/// number in the statement's <see cref="SqlSubqueryValues"/> before the input runs, so no
/// expression tree is rebuilt with the values in it: that would mean reconstructing the
/// language package's AST nodes from here, which needs their internal constructors.
/// </remarks>
internal sealed record SqlSubqueryPlan(SqlPlan Input, IReadOnlyList<SqlSubqueryBinding> Queries) : SqlPlan;
