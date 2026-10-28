using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Optional statement binding state; absent for ordinary stored-only queries.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlQueryScopeState")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlQueryScopeState.DebugView))]
internal sealed class SqlQueryScopeState
{
    // Share one optional reference slot: plain CTE/query scopes keep their original size.
    private object? _viewOrGrouping;

    internal Stack<(SqlSelectExpression? Select, SqlTableReference? Dml, SqlDerivedTableReference? Derived)> Scopes { get; } = new();
    internal SqlCorrelationScope? OuterScope { get; set; }
    internal HashSet<int>? OuterDependencies { get; set; }
    internal SqlPlanner.ViewImage? ViewImage
    {
        get => _viewOrGrouping is GroupingState grouping ? grouping.ViewImage : _viewOrGrouping as SqlPlanner.ViewImage;
        set
        {
            if (_viewOrGrouping is GroupingState grouping) { grouping.ViewImage = value; }
            else { _viewOrGrouping = value; }
        }
    }
    internal SqlPlanner.ViewBinding? ViewBinding { get; set; }
    internal int SetOperandDepth { get; set; }
    internal int NextScopeId { get; set; }
    internal bool DeferQueryEvaluation { get; set; }
    internal bool HasGroupingCalls { get; set; }
    internal Dictionary<SqlSelectExpression, List<SqlBoundGrouping>>? GroupingCalls
    {
        get => (_viewOrGrouping as GroupingState)?.Calls;
        set
        {
            if (_viewOrGrouping is not GroupingState grouping)
            { _viewOrGrouping = grouping = new GroupingState { ViewImage = ViewImage }; }
            grouping.Calls = value;
        }
    }
    internal Dictionary<(SqlSelectExpression Owner, SqlExpression Condition), SqlExpressionEvaluator> JoinScopes { get; } = new();
    internal Dictionary<(SqlSelectExpression? Owner, SqlRelationReference Reference), SqlRelationSource> Relations { get; } = new();

    private sealed class GroupingState
    {
        public SqlPlanner.ViewImage? ViewImage { get; set; }
        public Dictionary<SqlSelectExpression, List<SqlBoundGrouping>>? Calls { get; set; }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlQueryScopeState value)
        {
            _ = value;
        }
    }
}
