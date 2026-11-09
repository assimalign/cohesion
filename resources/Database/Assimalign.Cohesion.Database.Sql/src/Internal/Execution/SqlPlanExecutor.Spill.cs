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
    // Root SELECT and the CTAS source root supply permission explicitly. Every other nested
    // ExecuteAsync call retains the original path, including DML, windows and compound operands.
    internal Task<DatabaseResult> ExecuteRootAsync(SqlPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken, SqlDatabaseSession? spillOwner, Location? location = null)
        => plan is SqlCreateTableAsPlan tableAs && spillOwner?.Database.Engine.TempSpace is not null
            && !plan.ContainsRecursion && !PlanUsesJson(plan, cancellationToken) && CanSpillTableAs(tableAs, cancellationToken)
            ? ExecuteSpillRootAsync(plan, statement, cancellationToken, spillOwner, location)
            : spillOwner?.Database.Engine.TempSpace is not null && plan is SqlSelectPlan or SqlJoinPlan or SqlGroupPlan or SqlSystemViewPlan or SqlConstantPlan or SqlWithPlan or SqlSubqueryPlan
            && StatementInput(plan) is SqlRelationPlan
            && plan is not SqlGroupingSetsPlan && !plan.ContainsRecursion && !PlanUsesJson(plan, cancellationToken)
            && (plan is not SqlSubqueryPlan subquery || !subquery.Queries.Any(query => query.IsCorrelated || query.IsDeferred))
                ? ExecuteSpillRootAsync(plan, statement, cancellationToken, spillOwner, location)
                : ExecuteAsync(plan, statement, cancellationToken);

    private bool CanSpillTableAs(SqlCreateTableAsPlan plan, CancellationToken token)
    {
        // Inferred domain checks run during assignment, although the source SELECT merely
        // reads their columns. Conservatively exclude these checks from spill; assignment
        // uses a private expression scope, not the source's SQL/JSON statement owner.
        for (int ordinal = 0; ordinal < plan.Columns.Count; ordinal++)
        {
            var column = plan.Columns[ordinal];
            if (column.DomainName is { } name && SqlJsonUsage.FunctionContains(
                _definitions.Functions.Types.Find(name)?.Check, _catalog.DefaultCollation, token)) { return false; }
        }
        return true;
    }

    private async Task<DatabaseResult> ExecuteSpillRootAsync(SqlPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken, SqlDatabaseSession spillOwner, Location? location, SqlGroupPlan? hashInput = null)
    {
        statement = statement.WithSession(spillOwner);
        bool statementRoot = _metrics is null;
        var metrics = statement.Metrics;
        var frame = metrics.IsAnalyzing ? metrics.BeginAnalysisFrame() : null;
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            _metrics = metrics; _containsLike |= plan.ContainsLike;
            var initialized = InitializeWindow(plan, cancellationToken);
            DatabaseResult result;
            if (initialized is SqlWithPlan with)
            {
                foreach (var relation in with.Relations) { await MaterializeRelationAsync(relation, statement, cancellationToken).ConfigureAwait(false); }
                result = await ExecuteRootAsync(with.Input, statement, cancellationToken, spillOwner, location).ConfigureAwait(false);
            }
            else
            {
                result = initialized switch
                {
                    SqlCreateTableAsPlan tableAs => await ExecuteCreateTableAsAsync(tableAs, statement, cancellationToken, spillOwner).ConfigureAwait(false),
                    SqlSelectPlan scan => ExecuteSelect(scan, statement, cancellationToken, null, plan, spillOwner, hashInput),
                    SqlSystemViewPlan view => ExecuteSystemView(view, statement, cancellationToken, plan, spillOwner, hashInput),
                    SqlConstantPlan constant => ExecuteRootConstant(constant, plan, cancellationToken, spillOwner),
                    SqlGroupPlan group => await ExecuteGroupAsync(group, statement, cancellationToken, (SqlGroupPlan)plan, spillOwner).ConfigureAwait(false),
                    SqlSubqueryPlan subquery => await ExecuteRootSubqueriesAsync(subquery.Input, subquery.Queries, statement, cancellationToken, spillOwner, location).ConfigureAwait(false),
                    SqlJoinPlan join => await (CanPipelineJoinOutput((SqlJoinPlan)plan)
                        ? ExecutePipelinedJoinAsync(join, statement, cancellationToken, spillOwner: spillOwner, hashInput: hashInput).AsTask()
                        : ExecuteJoinAsync(join, statement, cancellationToken, spillOwner, hashInput)).ConfigureAwait(false),
                    _ => await ExecuteAsync(plan, statement, cancellationToken).ConfigureAwait(false),
                };
            }
            if (frame is not null)
            {
                if (plan is SqlSelectPlan) { metrics.Analysis(plan)!.Candidates += metrics.RecordsExamined - frame.StartRecords - frame.ChildRecords; }
                else if (plan is SqlJoinPlan { Steps.Count: > 0 }) { metrics.Analysis(plan)!.Candidates += metrics.JoinCandidatePairs - frame.StartPairs - frame.ChildPairs; }
            }
            if (result is SqlSpilledResultSet spilled) { spilled.Location = location; }
            if (statementRoot) { CloseJoinTemp(metrics, result, location); }
            return result;
        }
        catch (SqlExecutionException exception) when (exception.Diagnostics[0].Code == StorageTempException.DamageCode)
        { throw CloseJoinFailure(metrics, exception.At(location), statementRoot, location); }
        catch (StorageTempException exception)
        { throw CloseJoinFailure(metrics, SqlSpilledResultSet.Translate(exception, location), statementRoot, location); }
        // Cleanup preserves the original evaluator/cancellation failure and releases the
        // shared owner even when no result was created.
        catch (System.Exception exception)
        {
            if (IsJoinTempCleanupFailure(exception))
            { throw CloseJoinFailure(metrics, SqlSpilledResultSet.TranslateCleanup((System.AggregateException)exception, location), statementRoot, location); }
            if (statementRoot) { CloseJoinTemp(metrics, exception, location); }
            throw;
        }
        finally
        {
            if (frame is not null) { metrics.EndAnalysisFrame(frame); }
            if (statementRoot && _materializedRelations is { } relations) { ReleaseSelectMemory(ref relations.Reserved); }
        }
    }

    private DatabaseResult ExecuteRootConstant(SqlConstantPlan constant, SqlPlan plan, CancellationToken token, SqlDatabaseSession owner)
    {
        var evaluator = ExecutionEvaluator(_subqueryValues, token);
        return MaterializeSelect(evaluator.Matches(constant.Where, []) ? [System.Array.Empty<object?>()] : [],
            constant.Projections, constant.OrderBy, constant.Limit, constant.Offset, constant.IsDistinct,
            evaluator, constant.OrderByProjections, token, incrementalDistinct: CanPipelinePlainDistinct(plan), spillOwner: owner);
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
            return (DatabaseResult)result;
        }
        catch (System.Exception exception) { result.DisposeAfterFailure(exception); throw; }
    }

    private bool RetainStagedSortSource(List<object?[]> rows, object?[] row, ref SqlSelectMemory memory,
        IReadOnlyList<SqlBoundOrdering> orderBy, SqlDatabaseSession? owner, CancellationToken token, bool includeWindow = true, SqlGroupPlan? hashInput = null)
    {
        if (owner is not null && (hashInput is null || rows.Count == 0) && SqlStagedSortSource.For(rows) is { } source) { source.Append(row, token); return false; }
        try { RetainSelectRow(ref memory, row, includeWindow: includeWindow); return true; }
        catch (SqlExecutionException exception) when (exception.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode)
        {
            if (hashInput is not null ? !CanSpillHashGroup(hashInput, owner)
                : owner is null || orderBy.Count == 0 || orderBy.Any(order => !order.Collation.IsFixed)) { throw; }
            SqlStagedSortSource.Add(rows, row, ref memory, _metrics!, _definitions.StatementMemoryBudget, owner!, token);
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
        long grant = System.Math.Min(int.MaxValue, budget - _metrics!.HashMemoryBytes - source.ReadReservation);
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
            return (DatabaseResult)result;
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
            return (DatabaseResult)result;
        }
        catch (System.Exception exception) { result.DisposeAfterFailure(exception); throw; }
    }
}
