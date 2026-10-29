using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Anchor-typed fixpoint; the working reference has an independent identity.</summary>
[System.Diagnostics.DebuggerDisplay("SqlRecursiveUnionPlan")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed record SqlRecursiveUnionPlan(SqlDerivedRelation Working, SqlSetOperand Anchor,
    SqlSetOperand Recursive, bool IsAll, IReadOnlyList<SqlProjection> Projections,
    IReadOnlyList<SqlCatalogColumn> Columns) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Columns;

    private sealed class DebugView
    {
        public DebugView(SqlRecursiveUnionPlan value) { _ = value; }
    }
}
