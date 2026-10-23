using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A persisted CHECK constraint parsed and bound against one table version: the predicate's tree,
/// the bound expression a validated write evaluates, and the ordinals of the columns it reads,
/// which a violation reports.
/// </summary>
/// <param name="Constraint">The catalog constraint the predicate was loaded from.</param>
/// <param name="Predicate">The parsed predicate, which DDL that changes the table binds again against the changed columns.</param>
/// <param name="ColumnOrdinals">The distinct column ordinals the predicate references, in first-use order.</param>
/// <param name="Bound">
/// The predicate bound to the table version's columns, once (<see cref="SqlExpressionEvaluator.Bind(SqlExpression)"/>).
/// Every session writing the version evaluates this one tree, which is immutable. For an unresolved
/// predicate, a failure that raises <c>CS01009</c> whatever the row.
/// </param>
/// <param name="Unresolved">
/// The call the predicate makes that the engine's function catalog does not resolve, when the
/// predicate is bound as unresolved (owner decision 65); null for a predicate that binds.
/// </param>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundCheck")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCheck.DebugView))]
internal sealed record SqlBoundCheck(SqlCatalogConstraint Constraint, SqlExpression Predicate, IReadOnlyList<int> ColumnOrdinals,
    SqlBoundExpression Bound, SqlUnresolvedDefinition? Unresolved = null)
{
    internal bool ContainsLike { get; } = SqlViewQuery.Nodes(Predicate).Any(node => node is SqlLikeExpression);

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCheck _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCheck value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogConstraint Constraint => _target.Constraint;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression Predicate => _target.Predicate;
        public global::System.Collections.Generic.IReadOnlyList<int> ColumnOrdinals => _target.ColumnOrdinals;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundExpression Bound => _target.Bound;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlUnresolvedDefinition? Unresolved => _target.Unresolved;
    }
}
