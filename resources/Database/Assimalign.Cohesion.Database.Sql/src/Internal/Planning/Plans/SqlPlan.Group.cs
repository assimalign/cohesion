using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Groups the filtered rows of an input plan. A grouped row holds the group keys, then the
/// aggregate results, then the projected outputs; <c>HAVING</c>, the projections and
/// <c>ORDER BY</c> are bound to those slots (<see cref="SqlBoundSlot"/>), never to an
/// arbitrary representative row.
/// </summary>
internal sealed record SqlGroupPlan(
    SqlPlan Input,
    IReadOnlyList<SqlCatalogColumn> SourceColumns,
    IReadOnlyList<SqlRelationBinding>? Bindings,
    IReadOnlyList<SqlBoundKey> Keys,
    IReadOnlyList<SqlGroupAggregate> Aggregates,
    IReadOnlyList<SqlProjection> Projections,
    SqlBoundExpression? Having,
    IReadOnlyList<SqlBoundOrdering> OrderBy,
    SqlRowCount? Limit,
    SqlRowCount? Offset,
    bool IsDistinct) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => SourceColumns;
    internal override IReadOnlyList<SqlRelationBinding>? OutputBindings => Bindings;
}
