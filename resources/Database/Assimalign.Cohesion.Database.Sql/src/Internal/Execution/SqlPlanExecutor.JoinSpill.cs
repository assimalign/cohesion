using System;
using System.Linq;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private bool CanSpillJoinRows(SqlJoinPlan plan, SqlDatabaseSession? owner, SqlExecutionException error)
        => error.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode
            && CanSpillJoinRows(plan, owner);

    private bool CanSpillJoinRows(SqlJoinPlan plan, SqlDatabaseSession? owner)
        => owner?.Database.Engine.TempSpace is not null && CanPipelineSelectRetention()
            && !plan.IsDistinct && plan.Bindings.All(b => b.Source.Derived is not { IsCorrelated: true })
            && plan.Steps.All(s => s.Hash is { InnerExpressions: null, OuterExpressions: null });

    private bool CanSpillJoinBuild(SqlJoinPlan plan, SqlHashJoinPath? keys,
        SqlDatabaseSession? owner, SqlExecutionException error)
        => keys is { InnerExpressions: null, OuterExpressions: null } && CanSpillJoinRows(plan, owner, error);

    private static bool IsJoinTempCleanupFailure(Exception failure)
        => failure is AggregateException aggregate && aggregate.Flatten().InnerExceptions.All(error => error is StorageTempException);

    private static SqlExecutionException CloseJoinFailure(SqlStatementMetrics metrics, SqlExecutionException failure,
        bool statementRoot, Location? location)
    {
        if (statementRoot) { CloseJoinTemp(metrics, failure, location); }
        return failure;
    }

    private static void CloseJoinTemp(SqlStatementMetrics metrics, object? outcome, Location? location)
    {
        if (SqlHashSpillMetrics.DetachJoinTemp(metrics) is not { } statement) { return; }
        SqlHashSpillMetrics.RefreshJoin(metrics, statement);
        var recorded = SqlHashSpillMetrics.For(metrics)!.JoinCleanupFailure;
        if (outcome is Exception initiating)
        {
            _ = DisposeHashTemp(statement, initiating);
            if (recorded is not null) { initiating.Data[nameof(SqlHashJoinSpill)] = recorded; }
            return;
        }
        if (recorded is not null)
        {
            var failure = recorded is SqlExecutionException sql ? sql
                : recorded is AggregateException aggregate ? SqlSpilledResultSet.TranslateCleanup(aggregate, location)
                : SqlSpilledResultSet.Translate((StorageTempException)recorded, location);
            if (outcome is SqlSpilledResultSet undelivered) { undelivered.DisposeAfterFailure(failure); }
            _ = DisposeHashTemp(statement, failure);
            throw failure.At(location);
        }
        if (outcome is SqlSpilledResultSet delivered && delivered.AdoptStatement(statement)) { return; }
        var cleanup = DisposeHashTemp(statement, null);
        if (cleanup is SqlExecutionException located) { throw located.At(location); }
        if (cleanup is not null) { throw cleanup; }
    }
}
