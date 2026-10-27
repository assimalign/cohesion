using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// MERGE's target/source association tree and ordered actions. Physical columns remain
/// target then source even when RIGHT normalizes to a source-driven LEFT join.
/// A FULL association always has a proven safe mandatory hash key, as for SELECT FULL JOIN.
/// The executor supplies the action name in ReturningActionOrdinal after selecting an action.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlMergePlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed record SqlMergePlan(SqlCatalogTable Table, SqlDmlSource Source,
    IReadOnlyList<SqlMergeActionPlan> Actions, SqlBoundExpression Condition, IReadOnlyList<SqlProjection>? Returning,
    int ReturningActionOrdinal) : SqlPlan
{
    private sealed class DebugView
    {
        private readonly SqlMergePlan _plan;
        public DebugView(SqlMergePlan plan) { _plan = plan; }
        public SqlCatalogTable Table => _plan.Table;
        public SqlDmlSource Source => _plan.Source;
        public IReadOnlyList<SqlMergeActionPlan> Actions => _plan.Actions;
        public IReadOnlyList<SqlProjection>? Returning => _plan.Returning;
        public int ReturningActionOrdinal => _plan.ReturningActionOrdinal;
    }
}

internal sealed record SqlMergeActionPlan(SqlMergeMatchKind MatchKind, SqlMergeAction Action,
    SqlBoundExpression? Condition, IReadOnlyList<(int Ordinal, SqlBoundExpression Value)> Assignments,
    IReadOnlyList<int> InsertOrdinals, IReadOnlyList<SqlBoundExpression> InsertValues, bool IsDefaultValues,
    IReadOnlyList<SqlSubqueryBinding> Queries);
