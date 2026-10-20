using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlDeletePlan(SqlCatalogTable Table, SqlBoundExpression? Where, SqlAccessPath Access) : SqlPlan
{
    public IReadOnlyList<SqlProjection>? Returning { get; init; }
}
