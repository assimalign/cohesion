using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Materializes the reachable WITH definitions before the statement's first write.</summary>
internal sealed record SqlWithPlan(SqlPlan Input, IReadOnlyList<SqlDerivedRelation> Relations) : SqlPlan;

/// <summary>The singleton input of a scalar SELECT body with no FROM clause.</summary>
internal sealed record SqlConstantPlan(IReadOnlyList<SqlProjection> Projections, SqlBoundExpression? Where,
    IReadOnlyList<SqlBoundOrdering> OrderBy, SqlRowCount? Limit, SqlRowCount? Offset, bool IsDistinct,
    IReadOnlyDictionary<SqlExpression, int>? OrderByProjections) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => [];
}
