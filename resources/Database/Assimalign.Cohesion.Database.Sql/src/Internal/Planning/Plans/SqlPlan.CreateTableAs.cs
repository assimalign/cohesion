using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[System.Diagnostics.DebuggerDisplay("CreateTableAs {Schema,nq}.{Name,nq}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlCreateTableAsPlan.DebugView))]
internal sealed record SqlCreateTableAsPlan(string Schema, string Name, IReadOnlyList<SqlCatalogColumn> Columns,
    SqlPlan? Source, bool WithData, bool IfNotExists) : SqlPlan
{
    private sealed class DebugView
    {
        private readonly SqlCreateTableAsPlan _plan;

        public DebugView(SqlCreateTableAsPlan plan) { _plan = plan; }

        public string Schema => _plan.Schema;
        public string Name => _plan.Name;
        public IReadOnlyList<SqlCatalogColumn> Columns => _plan.Columns;
        public SqlPlan? Source => _plan.Source;
        public bool WithData => _plan.WithData;
        public bool IfNotExists => _plan.IfNotExists;
    }
}
