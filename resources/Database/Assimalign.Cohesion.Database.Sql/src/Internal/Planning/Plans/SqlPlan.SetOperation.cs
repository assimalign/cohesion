using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A materialized set operation, with compatible UNION chains stored as N inputs.</summary>
internal sealed record SqlSetOperationPlan(
    List<SqlSetOperand> Operands, SqlSetOperator Operator, bool IsAll,
    IReadOnlyList<SqlProjection> Projections, IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<SqlBoundOrdering> OrderBy, SqlRowCount? Limit, SqlRowCount? Offset) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Columns;
}
