namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Cached plans keep bound expressions. Only this execution's copy holds their values.
    private static SqlPlan InitializeWindow(SqlPlan plan, SqlExpressionEvaluator evaluator)
        => plan switch
        {
            SqlSelectPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlJoinPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlGroupPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlSystemViewPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlConstantPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            SqlSetOperationPlan p when NeedsValues(p.Limit, p.Offset) => p with { Limit = p.Limit?.Initialize(evaluator), Offset = p.Offset?.Initialize(evaluator) },
            _ => plan,
        };

    private static bool NeedsValues(SqlRowCount? limit, SqlRowCount? offset)
        => limit?.Expression is not null || offset?.Expression is not null;
}
