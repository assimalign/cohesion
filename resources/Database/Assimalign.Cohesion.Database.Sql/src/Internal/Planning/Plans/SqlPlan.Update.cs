using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlUpdatePlan(
    SqlCatalogTable Table,
    IReadOnlyList<(int Ordinal, SqlBoundExpression Value)> Assignments,
    SqlBoundExpression? Where,
    SqlAccessPath Access) : SqlPlan
{
    public IReadOnlyList<SqlProjection>? Returning { get; init; }
}
