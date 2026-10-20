using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A left-deep N-relation join. Physical slots retain FROM-then-JOIN order even
/// when the selected index reverses which relation drives the nested loop.
/// </summary>
internal sealed record SqlJoinPlan(
    IReadOnlyList<SqlRelationBinding> Bindings,
    IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<SqlJoinStep> Steps,
    IReadOnlyList<SqlProjection> Projections,
    SqlBoundExpression? Where,
    IReadOnlyList<SqlBoundOrdering> OrderBy,
    SqlRowCount? Limit,
    SqlRowCount? Offset,
    bool IsDistinct,
    IReadOnlyDictionary<SqlExpression, int>? OrderByProjections = null) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Columns;
    internal override IReadOnlyList<SqlRelationBinding>? OutputBindings => Bindings;
}
