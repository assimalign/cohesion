using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Materializes uncorrelated child queries under the enclosing statement's snapshot,
/// then runs the bound input with those values available to scalar evaluation.
/// </summary>
/// <remarks>
/// The input plan keeps the parsed expression trees untouched. Each binding names the
/// source AST node its query came from, and the evaluator resolves that node by identity.
/// Substituting values into a rebuilt tree would mean reconstructing the language
/// package's nodes from here — which needs its internal constructors, and silently drops
/// any expression or plan shape the rebuilder does not yet know about.
/// </remarks>
internal sealed record SqlSubqueryPlan(SqlPlan Input, IReadOnlyList<SqlSubqueryBinding> Queries) : SqlPlan;

/// <summary>
/// A child query, the expression node in the enclosing statement that produced it, and
/// the result type and collation its values carry.
/// </summary>
internal sealed record SqlSubqueryBinding(
    SqlPlan Query,
    SqlExpression Source,
    DatabaseType Type,
    Collation Collation,
    SqlSubqueryKind Kind,
    bool IsNegated);

/// <summary>The cardinality and reduction required by a subquery's use site.</summary>
internal enum SqlSubqueryKind { Scalar, Exists, Set }

/// <summary>A typed runtime constant produced by a relational operator.</summary>
internal sealed class SqlConstantExpression(object? value, DatabaseType type, Collation? collation = null) : SqlExpression(null)
{
    internal object? Value { get; } = value;
    internal DatabaseType Type { get; } = type;
    internal Collation? Collation { get; } = collation;
}
