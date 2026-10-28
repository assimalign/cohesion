using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Cached plans keep bound expressions. Only this execution's copy holds their values.
    private SqlPlan InitializeWindow(SqlPlan plan, CancellationToken cancellationToken)
    {
        bool needsValues = plan switch
        {
            SqlWindowAggregatePlan p => NeedsValues(p.Limit, p.Offset),
            SqlSelectPlan p => NeedsValues(p.Limit, p.Offset),
            SqlJoinPlan p => NeedsValues(p.Limit, p.Offset),
            SqlGroupPlan p => NeedsValues(p.Limit, p.Offset),
            SqlSystemViewPlan p => NeedsValues(p.Limit, p.Offset),
            SqlConstantPlan p => NeedsValues(p.Limit, p.Offset),
            SqlSetOperationPlan p => NeedsValues(p.Limit, p.Offset),
            _ => false,
        };
        if (!needsValues) { return plan; }
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        return plan switch
        {
            SqlWindowAggregatePlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlSelectPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlJoinPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlGroupPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlSystemViewPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlConstantPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlSetOperationPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            _ => plan,
        };
    }

    private static bool NeedsValues(SqlRowCount? limit, SqlRowCount? offset)
        => limit?.Expression is not null || offset?.Expression is not null;
}
