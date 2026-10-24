using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A child query's output schema and plan, without a storage identity.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlDerivedRelation {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDerivedRelation.DebugView))]
internal sealed record SqlDerivedRelation(string Name, SqlPlan Query, IReadOnlyList<SqlCatalogColumn> Columns, bool IsCorrelated = false)
{
    internal bool IsCommonTable { get; init; }
    internal SqlCatalogView? CatalogView { get; init; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlDerivedRelation _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDerivedRelation value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlan Query => _target.Query;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogColumn> Columns => _target.Columns;
        public bool IsCorrelated => _target.IsCorrelated;
    }
}
