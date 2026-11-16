using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private bool CanSpillNestedLoopRows(SqlJoinPlan plan, SqlDatabaseSession? owner)
    {
        if (owner?.Database.Engine.TempSpace is null || !CanPipelineSelectRetention() || !CanSpillValues
            || HasOperatorSpillCorrelation(plan)) { return false; }
        foreach (var binding in plan.Bindings)
        { if (binding.Source.Derived is { IsCorrelated: true }) { return false; } }
        foreach (var step in plan.Steps)
        { if (step.Hash is null && step.Access is null) { return true; } }
        return false;
    }

    // A normalized FULL/hash parent may consume a nested-loop subtree. The hash
    // build still needs plain keys; a non-hash sibling does not invalidate them.
    private bool CanSpillMixedJoinBuild(SqlJoinPlan plan, SqlDatabaseSession? owner)
    {
        if (CanSpillJoinRows(plan, owner)) { return true; }
        if (plan.IsDistinct || !CanSpillNestedLoopRows(plan, owner)) { return false; }
        bool nested = false;
        foreach (var step in plan.Steps)
        {
            if (step.Hash is null) { nested = true; }
            else if (step.Hash is { InnerExpressions: not null } or { OuterExpressions: not null }) { return false; }
        }
        return nested;
    }

    private void RetainNestedLoopInner(List<object?[]> rows, object?[] row, ref SqlSelectMemory memory,
        SqlJoinPlan plan, SqlDatabaseSession? owner, CancellationToken token)
    {
        if (SqlJoinInnerSpool.For(rows) is { } spool) { spool.Append(row, token); return; }
        try { RetainSelectRow(ref memory, row, includeWindow: true); rows.Add(row); }
        catch (SqlExecutionException failure) when (failure.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode
            && CanSpillNestedLoopRows(plan, owner))
        { SqlJoinInnerSpool.Add(rows, row, ref memory, _metrics!, _definitions.StatementMemoryBudget, owner!, failure, token); }
    }

    private static IEnumerable<object?[]> ReadNestedLoopInner(List<object?[]> rows, CancellationToken token)
        => SqlJoinInnerSpool.For(rows) is { } spool ? spool.Read(token) : rows;
}
