using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Inserts a complete query result into a bound target. The executor materializes
/// the source with the statement snapshot before applying any destination write.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlInsertSelectPlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlInsertSelectPlan.DebugView))]
internal sealed record SqlInsertSelectPlan(
    SqlCatalogTable Table,
    IReadOnlyList<int> TargetOrdinals,
    SqlPlan Source) : SqlPlan
{
    public Assimalign.Cohesion.Database.Sql.Language.SqlIdentityOverride Overriding { get; init; }
    public IReadOnlyList<SqlProjection>? Returning { get; init; }
    public SqlOnConflictPlan? OnConflict { get; init; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlInsertSelectPlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlInsertSelectPlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
        public global::System.Collections.Generic.IReadOnlyList<int> TargetOrdinals => _target.TargetOrdinals;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlan Source => _target.Source;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection>? Returning => _target.Returning;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlOnConflictPlan? OnConflict => _target.OnConflict;
    }
}
