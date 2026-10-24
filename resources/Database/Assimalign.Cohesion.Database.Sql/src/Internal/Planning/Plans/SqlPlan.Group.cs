using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Groups the filtered rows of an input plan. A grouped row holds the group keys, then the
/// aggregate results, then the projected outputs; <c>HAVING</c>, the projections and
/// <c>ORDER BY</c> are bound to those slots (<see cref="SqlBoundSlot"/>), never to an
/// arbitrary representative row.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlGroupPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlGroupPlan.DebugView))]
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

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlGroupPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlGroupPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlan Input => _target.Input;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogColumn> SourceColumns => _target.SourceColumns;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlRelationBinding>? Bindings => _target.Bindings;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundKey> Keys => _target.Keys;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlGroupAggregate> Aggregates => _target.Aggregates;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection> Projections => _target.Projections;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundExpression? Having => _target.Having;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundOrdering> OrderBy => _target.OrderBy;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCount? Limit => _target.Limit;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCount? Offset => _target.Offset;
        public bool IsDistinct => _target.IsDistinct;
    }
}
