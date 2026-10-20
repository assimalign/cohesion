using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlSelectPlan(
    SqlCatalogTable Table,
    IReadOnlyList<SqlProjection> Projections,
    SqlBoundExpression? Where,
    IReadOnlyList<SqlBoundOrdering> OrderBy,
    SqlRowCount? Limit,
    SqlRowCount? Offset,
    bool IsDistinct,
    SqlAccessPath Access,
    IReadOnlyDictionary<SqlExpression, int>? OrderByProjections = null) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Table.Columns;
}
