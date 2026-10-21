using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>An <c>INSERT ... VALUES</c>: each row's values bound with no columns in scope.</summary>
internal sealed record SqlInsertPlan(
    SqlCatalogTable Table,
    IReadOnlyList<int> TargetOrdinals,
    IReadOnlyList<SqlBoundExpression[]> Rows) : SqlPlan
{
    public IReadOnlyList<SqlProjection>? Returning { get; init; }
    public SqlOnConflictPlan? OnConflict { get; init; }
}
