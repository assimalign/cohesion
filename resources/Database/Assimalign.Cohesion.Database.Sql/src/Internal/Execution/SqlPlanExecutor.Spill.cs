using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // The one root entry supplies permission explicitly. Every nested ExecuteAsync call defaults
    // to the original, nonspilling executor, including CTAS, DML, windows and compound operands.
    internal Task<DatabaseResult> ExecuteRootAsync(SqlPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken, SqlDatabaseSession? spillOwner, Location? location = null)
        => spillOwner?.Database.Engine.TempSpace is not null && plan is SqlSelectPlan or SqlJoinPlan or SqlGroupPlan or SqlSystemViewPlan or SqlConstantPlan or SqlWithPlan or SqlSubqueryPlan
            && StatementInput(plan) is SqlRelationPlan
            && plan is not SqlGroupingSetsPlan && !plan.ContainsRecursion && !PlanUsesJson(plan, cancellationToken)
            && (plan is not SqlSubqueryPlan subquery || !subquery.Queries.Any(query => query.IsCorrelated || query.IsDeferred))
                ? ExecuteSpillRootAsync(plan, statement, cancellationToken, spillOwner, location)
                : ExecuteAsync(plan, statement, cancellationToken);

    private async Task<DatabaseResult> ExecuteSpillRootAsync(SqlPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken, SqlDatabaseSession spillOwner, Location? location)
    {
        var metrics = statement.Metrics;
        var frame = metrics.IsAnalyzing ? metrics.BeginAnalysisFrame() : null;
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            _metrics = metrics; _containsLike |= plan.ContainsLike;
            var initialized = InitializeWindow(plan, cancellationToken);
            var result = initialized switch
            {
                SqlSelectPlan scan => ExecuteSelect(scan, statement, cancellationToken, null, plan, spillOwner),
                SqlSystemViewPlan view => ExecuteSystemView(view, statement, cancellationToken, plan, spillOwner),
                SqlConstantPlan constant => ExecuteRootConstant(constant, plan, cancellationToken, spillOwner),
                SqlGroupPlan group => await ExecuteGroupAsync(group, statement, cancellationToken, (SqlGroupPlan)plan, spillOwner).ConfigureAwait(false),
                SqlWithPlan with => await ExecuteRootWithAsync(with, statement, cancellationToken, spillOwner, location).ConfigureAwait(false),
                SqlSubqueryPlan subquery => await ExecuteRootSubqueriesAsync(subquery.Input, subquery.Queries, statement, cancellationToken, spillOwner, location).ConfigureAwait(false),
                SqlJoinPlan join => await (CanPipelineJoinOutput((SqlJoinPlan)plan)
                    ? ExecutePipelinedJoinAsync(join, statement, cancellationToken, spillOwner: spillOwner).AsTask()
                    : ExecuteJoinAsync(join, statement, cancellationToken, spillOwner)).ConfigureAwait(false),
                _ => await ExecuteAsync(plan, statement, cancellationToken).ConfigureAwait(false),
            };
            if (frame is not null)
            {
                if (plan is SqlSelectPlan) { metrics.Analysis(plan)!.Candidates += metrics.RecordsExamined - frame.StartRecords - frame.ChildRecords; }
                else if (plan is SqlJoinPlan { Steps.Count: > 0 }) { metrics.Analysis(plan)!.Candidates += metrics.JoinCandidatePairs - frame.StartPairs - frame.ChildPairs; }
            }
            if (result is SqlSpilledResultSet spilled) { spilled.Location = location; }
            return result;
        }
        finally
        {
            if (frame is not null) { metrics.EndAnalysisFrame(frame); }
            if (_materializedRelations is { } relations) { ReleaseSelectMemory(ref relations.Reserved); }
        }
    }

    private DatabaseResult ExecuteRootConstant(SqlConstantPlan constant, SqlPlan plan, CancellationToken token, SqlDatabaseSession owner)
    {
        var evaluator = ExecutionEvaluator(_subqueryValues, token);
        return MaterializeSelect(evaluator.Matches(constant.Where, []) ? [System.Array.Empty<object?>()] : [],
            constant.Projections, constant.OrderBy, constant.Limit, constant.Offset, constant.IsDistinct,
            evaluator, constant.OrderByProjections, token, incrementalDistinct: CanPipelinePlainDistinct(plan), spillOwner: owner);
    }

    private async Task<DatabaseResult> ExecuteRootWithAsync(SqlWithPlan plan, SqlStatementContext statement,
        CancellationToken token, SqlDatabaseSession owner, Location? location)
    {
        foreach (var relation in plan.Relations) { await MaterializeRelationAsync(relation, statement, token).ConfigureAwait(false); }
        return await ExecuteRootAsync(plan.Input, statement, token, owner, location).ConfigureAwait(false);
    }

    private async Task<DatabaseResult> ExecuteRootSubqueriesAsync(SqlPlan input, IReadOnlyList<SqlSubqueryBinding> queries,
        SqlStatementContext statement, CancellationToken token, SqlDatabaseSession owner, Location? location)
    {
        // Only independent, eager frames enter here. Each child retains the ordinary ExecuteAsync
        // boundary; the explicit permission continues solely into this statement's outer body.
        var parent = _subqueryValues;
        var frame = SqlSubqueryValues.WithMemory(parent, queries[0].ScopeId, queries, false, statement.Metrics, _definitions.StatementMemoryBudget);
        _subqueryValues = frame;
        try
        {
            foreach (var query in queries)
            {
                if (!frame.StatementValues.TryGetValue(query.Slot, out var values))
                {
                    values = await MaterializeSubqueryAsync(query, statement, token).ConfigureAwait(false);
                    frame.Admit(values, correlated: false); frame.StatementValues.Add(query.Slot, values);
                }
                frame.Set(query.Slot, values);
            }
            return await ExecuteRootAsync(input, statement, token, owner, location).ConfigureAwait(false);
        }
        finally { frame.ClearCorrelated(RecursiveState?.Memory); frame.ReleaseMemory(); _subqueryValues = parent; }
    }

    private DatabaseResult FinishCollected(SqlRowCollector collector, IReadOnlyList<SqlProjection> projections,
        SqlRowCount? limit, SqlRowCount? offset, bool isDistinct, SqlExpressionEvaluator evaluator,
        CancellationToken cancellationToken, Collation[]? projectionCollations = null, bool resolveEmptyCollations = true)
    {
        if (!collector.HasSpilled)
        { return FinishSelect(collector.Finish(resolveEmptyCollations), projections, limit, offset, isDistinct, evaluator, cancellationToken, projectionCollations); }
        var columns = ((DatabaseResultSet)MaterializeProjected(projections, [])).Columns;
        var result = collector.TakeSpilled(columns, offset?.Count ?? 0, limit?.Count ?? -1);
        try
        {
            if (isDistinct)
            {
                // Keep the fitting return above free of a compiler-generated evaluator closure.
                // A captured lambda here allocates even when the spill branch is not taken.
                if (projectionCollations is null)
                {
                    projectionCollations = new Collation[projections.Count];
                    for (int i = 0; i < projectionCollations.Length; i++)
                    { projectionCollations[i] = projections[i].Collation.Resolve(evaluator.SubqueryValues); }
                }
                result.Deduplicate(new GroupKeyComparer(projectionCollations),
                    _metrics!, _definitions.StatementMemoryBudget, cancellationToken);
            }
            result.CaptureMetrics(_metrics!);
            return result;
        }
        catch (System.Exception exception) { result.DisposeAfterFailure(exception); throw; }
    }

    private bool RetainStagedSortSource(List<object?[]> rows, object?[] row, ref SqlSelectMemory memory,
        IReadOnlyList<SqlBoundOrdering> orderBy, SqlDatabaseSession? owner, CancellationToken token, bool includeWindow = true)
    {
        if (owner is not null && SqlStagedSortSource.For(rows) is { } source) { source.Append(row, token); return false; }
        try { RetainSelectRow(ref memory, row, includeWindow: includeWindow); return true; }
        catch (SqlExecutionException exception) when (exception.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode)
        {
            if (owner is null || orderBy.Count == 0 || orderBy.Any(order => !order.Collation.IsFixed)) { throw; }
            SqlStagedSortSource.Add(rows, row, ref memory, _metrics!, _definitions.StatementMemoryBudget, owner, token);
            return false;
        }
    }

    private DatabaseResult MaterializeStagedSort(SqlStagedSortSource source, IReadOnlyList<SqlProjection> projections,
        IReadOnlyList<SqlBoundOrdering> orderBy, SqlRowCount? limit, SqlRowCount? offset, bool distinct,
        SqlExpressionEvaluator evaluator, IReadOnlyDictionary<SqlExpression, int>? outputSlots,
        CancellationToken token, bool deferProjection)
    {
        if (!deferProjection && !distinct && limit?.Count == 0) { return MaterializeProjected(projections, []); }
        source.Seal(token);
        long budget = _definitions.StatementMemoryBudget;
        long grant = System.Math.Min(int.MaxValue, budget - _metrics!.HashMemoryBytes - source.ReadBufferBytes);
        int bound = (int)((grant - 32768) / 16 - 8192);
        if (grant < 65536 || bound < 16) { throw SqlExecutionException.StatementMemoryBudget(budget); }
        var columns = orderBy.Select(order => new SqlTempSortKeyCodec.Column(order.IsDescending, order.NullsFirst,
            order.Collation.Resolve(evaluator.SubqueryValues))).ToArray();
        var families = new byte[columns.Length];
        long maximumDecodedRowBytes = 0;
        StorageTempSortReader reader;
        StorageTempSorter? sorter = null;
        System.Exception? sortFailure = null;
        _metrics.ReservePlainSelectMemory(grant, budget);
        try
        {
            sorter = StorageTempSorter.Create(source.Statement, grant, bound, 64);
            SqlRowCollector.ObserveSpill?.Invoke("staged-sort");
            foreach (var row in source.Read(token))
            {
                var output = deferProjection ? row : ProjectRow(row, projections, evaluator, token);
                var ordered = row;
                if (outputSlots is { Count: > 0 })
                {
                    ordered = new object?[row.Length + output.Length]; row.CopyTo(ordered, 0); output.CopyTo(ordered, row.Length);
                }
                var keys = new object?[orderBy.Count];
                for (int i = 0; i < keys.Length; i++) { token.ThrowIfCancellationRequested(); keys[i] = evaluator.Evaluate(orderBy[i].Key, ordered); }
                if (!SqlTempSortKeyCodec.TryEncode(keys, columns, families, out var key)
                    || !SqlTempRowCodec.TryGetLength(output, out int size) || 16L + key.Length + size > bound
                    || !SqlTempRowCodec.TryEncode(output, out var value))
                { throw SqlExecutionException.StatementMemoryBudget(budget); }
                maximumDecodedRowBytes = System.Math.Max(maximumDecodedRowBytes, SqlTempRowCodec.DecodedBytes(output));
                sorter.Append(key, value, token);
            }
            reader = sorter.Complete(token);
            SqlSortSpillMetrics.Record(_metrics, source.Statement, sorter);
        }
        catch (StorageTempException exception) { sortFailure = SqlSpilledResultSet.Translate(exception); throw sortFailure; }
        catch (System.AggregateException exception) { sortFailure = SqlSpilledResultSet.TranslateCleanup(exception); throw sortFailure; }
        catch (System.Exception exception) { sortFailure = exception; throw; }
        finally
        {
            try
            {
                try { sorter?.Dispose(); }
                catch (System.AggregateException cleanup)
                {
                    var failure = SqlSpilledResultSet.TranslateCleanup(cleanup);
                    if (sortFailure is null) { throw failure; }
                    sortFailure.Data[nameof(StorageTempSorter)] = failure;
                }
            }
            finally { _metrics.ReleasePlainSelectMemory(grant); }
        }
        source.Transfer();
        SqlSpilledResultSet result;
        try
        { result = new SqlSpilledResultSet(((DatabaseResultSet)MaterializeProjected(projections, [])).Columns,
            source.Statement, reader, offset?.Count ?? 0, limit?.Count ?? -1, source.Owner, grant, maximumDecodedRowBytes); }
        catch (System.Exception exception) { SqlSpilledResultSet.ReleaseAfterFailure(source.Statement, exception); throw; }
        try
        {
            if (deferProjection) { result.Transform(row => ProjectRow(row, projections, evaluator, token), _metrics, budget, bound, token); }
            if (distinct) { result.Deduplicate(new GroupKeyComparer(projections.Select(p => p.Collation.Resolve(evaluator.SubqueryValues)).ToArray()), _metrics, budget, token); }
            result.CaptureMetrics(_metrics);
            return result;
        }
        catch (System.Exception exception) { result.DisposeAfterFailure(exception); throw; }
    }

    private DatabaseResult FinishStagedOutput(SqlStagedSortSource source, IReadOnlyList<SqlProjection> projections,
        SqlRowCount? limit, SqlRowCount? offset, bool distinct, SqlExpressionEvaluator evaluator, CancellationToken token)
    {
        source.Seal(token);
        SqlSortSpillMetrics.RecordOutput(_metrics!, source.Statement);
        SqlRowCollector.ObserveSpill?.Invoke("output-spool");
        SqlSpilledResultSet result;
        try
        { result = new SqlSpilledResultSet(((DatabaseResultSet)MaterializeProjected(projections, [])).Columns,
            source.Statement, source.TransferOutput(), offset?.Count ?? 0, limit?.Count ?? -1, source.Owner, _definitions.StatementMemoryBudget - _metrics!.HashMemoryBytes, source.MaximumDecodedRowBytes); }
        catch (System.Exception exception) { SqlSpilledResultSet.ReleaseAfterFailure(source.Statement, exception); throw; }
        try
        {
            if (distinct) { result.Deduplicate(new GroupKeyComparer(projections.Select(p => p.Collation.Resolve(evaluator.SubqueryValues)).ToArray()), _metrics!, _definitions.StatementMemoryBudget, token); }
            result.CaptureMetrics(_metrics!);
            return result;
        }
        catch (System.Exception exception) { result.DisposeAfterFailure(exception); throw; }
    }
}
