using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Optional statement binding state; absent for ordinary stored-only queries.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlQueryScopeState")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlQueryScopeState.DebugView))]
internal sealed class SqlQueryScopeState
{
    internal Stack<(SqlSelectExpression? Select, SqlTableReference? Dml, SqlDerivedTableReference? Derived)> Scopes { get; } = new();
    internal SqlCorrelationScope? OuterScope { get; set; }
    internal HashSet<int>? OuterDependencies { get; set; }
    internal SqlPlanner.ViewImage? ViewImage { get; set; }
    internal SqlPlanner.ViewBinding? ViewBinding { get; set; }
    internal int SetOperandDepth { get; set; }
    internal int NextScopeId { get; set; }
    internal Dictionary<(SqlSelectExpression? Owner, SqlRelationReference Reference), SqlRelationSource> Relations { get; } = new();

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlQueryScopeState value)
        {
            _ = value;
        }
    }
}
