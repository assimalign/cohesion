using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

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

/// <summary>
/// A child query, the slot its values fill, and the result type and collation its values carry.
/// </summary>
internal sealed record SqlSubqueryBinding(
    SqlPlan Query,
    int Slot,
    DatabaseType Type,
    Collation Collation,
    SqlSubqueryKind Kind,
    bool IsNegated);

/// <summary>
/// A subquery use site as the expression binder sees it: the slot its values fill, how its use site
/// reduces them, and the collation they carry, which a comparison against them uses.
/// </summary>
/// <param name="Id">The slot, numbered from zero across every nesting level of one statement.</param>
/// <param name="Kind">How the use site reduces the subquery's rows.</param>
/// <param name="Collation">The collation of the subquery's output column.</param>
/// <param name="Type">
/// The type of the subquery's values: its output column's, or BOOLEAN for <c>EXISTS</c>; what a
/// function call over a scalar subquery resolves its overload by.
/// </param>
internal sealed record SqlSubquerySlot(int Id, SqlSubqueryKind Kind, Collation Collation, DatabaseType Type);

/// <summary>The cardinality and reduction required by a subquery's use site.</summary>
internal enum SqlSubqueryKind { Scalar, Exists, Set }
