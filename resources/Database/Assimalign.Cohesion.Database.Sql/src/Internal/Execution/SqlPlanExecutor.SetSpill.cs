using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private bool CanSpillSetOperation(SqlSetOperationPlan plan)
        => !_definitions.ForceSelectRetentionBasePath && _definitions.CanSpillValues
            && plan.Projections.All(projection => projection.Collation.IsFixed) && !HasOperatorSpillCorrelation(plan);

    private static bool HasOperatorSpillCorrelation(SqlPlan plan)
    {
        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            switch (plan)
            {
                case SqlCorrelatedSubqueryPlan: return true;
                case SqlSubqueryPlan subquery:
                    foreach (var query in subquery.Queries)
                    { if (query.IsCorrelated || query.IsDeferred || HasOperatorSpillCorrelation(query.Query)) { return true; } }
                    return HasOperatorSpillCorrelation(subquery.Input);
                case SqlJoinPlan join:
                    foreach (var binding in join.Bindings)
                    { if (binding.Source.Derived is { } derived && (derived.IsCorrelated || HasOperatorSpillCorrelation(derived.Query))) { return true; } }
                    return false;
                case SqlWithPlan with:
                    foreach (var relation in with.Relations)
                    { if (relation.IsCorrelated || HasOperatorSpillCorrelation(relation.Query)) { return true; } }
                    return HasOperatorSpillCorrelation(with.Input);
                case SqlSetOperationPlan set:
                    foreach (var operand in set.Operands)
                    { if (HasOperatorSpillCorrelation(operand.Query)) { return true; } }
                    return false;
                case SqlGroupPlan group: return HasOperatorSpillCorrelation(group.Input);
                case SqlWindowAggregatePlan window: return HasOperatorSpillCorrelation(window.Input);
                case SqlSequenceScopePlan sequence: return HasOperatorSpillCorrelation(sequence.Input);
                default: return false;
            }
        }
        catch (InsufficientExecutionStackException) { return true; } // Optional eligibility never adds a depth refusal.
    }

    private bool CanSpillSetOperation(SqlSetOperationPlan plan, SqlStatementContext statement, SqlExecutionException failure)
        => statement.Session?.Database.Engine.TempSpace is not null && CanSpillSetOperation(plan)
            && failure.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode;

    private void TryAdmitSetLeft(List<object?[]> left, ref long bytes, SqlSetOperationPlan plan,
        SqlStatementContext statement, CancellationToken token)
    {
        if (SetOperandPending.For(left) is not null) { return; }
        try { bytes += AdmitSelectRows(left, token, includeWindow: true); }
        catch (SqlExecutionException failure) when (CanSpillSetOperation(plan, statement, failure))
        { SetOperandPending.Attach(left, new SetOperandPending(failure), statement.Metrics, plan); }
    }

    // The ordinary helper remains the original async state machine. The permitted
    // helper returns its live cursor only on refusal; the caller then releases its
    // older operands before resuming. No input, projection or conversion is rerun.
    private async Task<List<object?[]>> MaterializePermittedSetOperandAsync(SqlPlan operand,
        IReadOnlyList<SqlBoundExpression> conversions, SqlSetOperationPlan plan,
        SqlStatementContext statement, CancellationToken token, List<object?[]>? destination,
        HashSet<object?[]>? seen)
    {
        long resultBytes = 0;
        SqlSelectMemory retainedBytes = default, seenBytes = default;
        var parent = _subqueryValues;
        var rows = destination ?? new List<object?[]>();
        DatabaseResultSet? result = null;
        IAsyncEnumerator<DatabaseRow>? reader = null;
        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            _subqueryValues = parent is null ? null : new SqlSubqueryValues(parent);
            statement.Metrics.BeginStageResult();
            try { result = (DatabaseResultSet)await ExecuteAsync(operand, statement, token).ConfigureAwait(false); }
            finally { statement.Metrics.EndStageResult(); }
            var evaluator = ExecutionEvaluator(_subqueryValues, token);
            try
            {
                resultBytes = result is SqlSpilledResultSet spilled
                    ? spilled.ReserveReader(statement.Metrics, _definitions.StatementMemoryBudget, converting: true)
                    : AdmitSelectResult(result, token);
            }
            catch (SqlExecutionException failure) when (CanSpillSetOperation(plan, statement, failure))
            {
                SetOperandPending.Attach(rows, new(failure, result, null, null, evaluator, conversions), statement.Metrics, plan);
                result = null; return rows;
            }
            reader = result.GetRowsAsync(token).GetAsyncEnumerator(token);
            while (await reader.MoveNextAsync().ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                var converted = ConvertSetRow(reader.Current, result.Columns, conversions, plan, evaluator);
                if (SetOperationSpill.For(rows) is { } stage) { stage.Append(converted, token); continue; }
                if (seen is not null && seen.Contains(converted)) { continue; }
                try
                {
                    if (seen is not null) { RetainSelectRow(ref seenBytes, converted, includeWindow: true, overhead: 64); seen.Add(converted); }
                    RetainSelectRow(ref retainedBytes, converted, includeWindow: true); rows.Add(converted);
                }
                catch (SqlExecutionException failure) when (CanSpillSetOperation(plan, statement, failure))
                {
                    SetOperandPending.Attach(rows, new(failure, result, reader, converted, evaluator, conversions, resultBytes), statement.Metrics, plan);
                    resultBytes = 0; result = null; reader = null; return rows;
                }
            }
            return rows;
        }
        finally
        {
            try { if (reader is not null) { await reader.DisposeAsync().ConfigureAwait(false); } }
            finally
            {
                try { if (result is not null) { await result.DisposeAsync().ConfigureAwait(false); } }
                finally
                {
                    ReleaseSelectMemory(ref retainedBytes); ReleaseSelectMemory(ref seenBytes); ReleaseSelectMemory(ref resultBytes);
                    _subqueryValues = parent;
                }
            }
        }
    }

    private static object?[] ConvertSetRow(DatabaseRow row, IReadOnlyList<DatabaseColumn> columns,
        IReadOnlyList<SqlBoundExpression> conversions, SqlSetOperationPlan plan, SqlExpressionEvaluator evaluator)
    {
        var values = new object?[row.FieldCount];
        for (int i = 0; i < values.Length; i++) { values[i] = SqlBinaryValue.CopyShared(SqlCharacter.Input(row.GetValue(i), columns[i].Type)); }
        var converted = new object?[values.Length];
        for (int i = 0; i < values.Length; i++)
        { converted[i] = SqlTemporalArithmetic.Promote(evaluator.Evaluate(conversions[i], values), plan.Projections[i].Type); }
        return converted;
    }

    private void StartSetUnionSpill(List<object?[]> rows, HashSet<object?[]>? seen, SqlSetOperationPlan plan,
        SqlStatementContext statement, CancellationToken token, SqlExecutionException? failure = null)
    {
        if (SetOperationSpill.For(rows) is not null) { return; }
        failure ??= SetOperandPending.For(rows)!.Refusal;
        var stage = new SetOperationSpill(this, plan, statement, failure, union: true);
        RegisterSetStage(statement.Metrics, plan, stage);
        SetOperationSpill.Attach(rows, stage);
        foreach (var row in rows) { stage.Append(row, token); }
        rows.Clear(); rows.Capacity = 0; seen?.Clear(); seen?.TrimExcess();
    }

    private async Task<List<object?[]>> ResumeSetOperandAsync(List<object?[]> rows, SqlSetOperationPlan plan,
        SqlStatementContext statement, CancellationToken token)
    {
        if (SetOperandPending.Take(rows) is not { } pending) { return rows; }
        try
        {
            var stage = SetOperationSpill.For(rows)!;
            if (pending.Current is { } current) { stage.Append(current, token); }
            if (pending.Result is not { } result) { return rows; }
            // The base source already completed. Its retained result is released only
            // after the remaining raw rows have been converted, in their original order.
            if (result is SqlSpilledResultSet spilled && pending.ResultBytes == 0)
            { pending.ResultBytes = spilled.ReserveReader(statement.Metrics, _definitions.StatementMemoryBudget, converting: true); }
            pending.Reader ??= result.GetRowsAsync(token).GetAsyncEnumerator(token);
            while (await pending.Reader.MoveNextAsync().ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                stage.Append(ConvertSetRow(pending.Reader.Current, result.Columns, pending.Conversions!, plan, pending.Evaluator!), token);
            }
            return rows;
        }
        catch (Exception failure) { pending.Failure = failure; throw; }
        finally { await pending.CloseAsync(statement.Metrics).ConfigureAwait(false); }
    }

    private async Task<List<object?[]>> SpillSetSidesAsync(List<object?[]> left, List<object?[]>? right,
        SqlSetOperationPlan plan, SqlStatementContext statement, CancellationToken token, SqlExecutionException? failure = null)
    {
        failure ??= (SetOperandPending.For(left) ?? SetOperandPending.For(right!))!.Refusal;
        var stage = new SetOperationSpill(this, plan, statement, failure, union: false);
        RegisterSetStage(statement.Metrics, plan, stage);
        SetOperationSpill.Attach(left, stage);
        foreach (var row in left) { stage.Append(row, token); }
        left.Clear(); left.Capacity = 0;
        await ResumeSetOperandAsync(left, plan, statement, token).ConfigureAwait(false);
        stage.NextSide(token);
        if (right is null)
        {
            await MaterializePermittedSetOperandAsync(plan.Operands[1].Query, plan.Operands[1].Values,
                plan, statement, token, left, null).ConfigureAwait(false);
            await ResumeSetOperandAsync(left, plan, statement, token).ConfigureAwait(false);
        }
        else
        {
            SetOperationSpill.Attach(right, stage);
            foreach (var row in right) { stage.Append(row, token); }
            right.Clear(); right.Capacity = 0;
            await ResumeSetOperandAsync(right, plan, statement, token).ConfigureAwait(false);
        }
        return left;
    }

    private static IEnumerable<object?[]> ReadSetRows(List<object?[]> rows, CancellationToken token)
    {
        if (SetOperationSpill.For(rows) is not { } stage) { return rows; }
        stage.Complete(token); return stage.Read(token);
    }

    private static ConditionalWeakTable<SqlStatementMetrics, Dictionary<SqlSetOperationPlan, List<IDisposable>>>? _setStages;
    private static void RegisterSetStage(SqlStatementMetrics metrics, SqlSetOperationPlan plan, IDisposable stage)
    {
        var owners = LazyInitializer.EnsureInitialized(ref _setStages).GetValue(metrics, _ => new(ReferenceEqualityComparer.Instance));
        if (!owners.TryGetValue(plan, out var stages)) { owners.Add(plan, stages = []); }
        stages.Add(stage);
    }
    private static void ReleaseSetStages(SqlStatementMetrics metrics, SqlSetOperationPlan plan)
    {
        if (_setStages?.TryGetValue(metrics, out var owners) != true || !owners!.Remove(plan, out var stages)) { return; }
        for (int i = stages.Count - 1; i >= 0; i--) { stages[i].Dispose(); }
        if (owners.Count == 0) { _setStages!.Remove(metrics); }
    }

    private static void RecordSetStageFailure(SqlStatementMetrics metrics, SqlSetOperationPlan plan, Exception failure)
    {
        if (_setStages?.TryGetValue(metrics, out var owners) != true || !owners!.TryGetValue(plan, out var stages)) { return; }
        foreach (var stage in stages)
        {
            if (stage is SetOperandPending pending) { pending.Failure = failure; }
            else if (stage is SetOperationSpill spilled) { spilled.Failure = failure; }
        }
    }

    [System.Diagnostics.DebuggerDisplay("SQL set operand continuation")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class SetOperandPending : IDisposable
    {
        private static ConditionalWeakTable<List<object?[]>, SetOperandPending>? _pending;
        private SqlStatementMetrics? _metrics;
        internal readonly SqlExecutionException Refusal;
        internal DatabaseResultSet? Result;
        internal IAsyncEnumerator<DatabaseRow>? Reader;
        internal object?[]? Current;
        internal readonly SqlExpressionEvaluator? Evaluator;
        internal readonly IReadOnlyList<SqlBoundExpression>? Conversions;
        internal long ResultBytes;
        internal Exception? Failure;
        internal SetOperandPending(SqlExecutionException refusal, DatabaseResultSet? result = null,
            IAsyncEnumerator<DatabaseRow>? reader = null, object?[]? current = null,
            SqlExpressionEvaluator? evaluator = null, IReadOnlyList<SqlBoundExpression>? conversions = null, long resultBytes = 0)
        { Refusal = refusal; Result = result; Reader = reader; Current = current; Evaluator = evaluator; Conversions = conversions; ResultBytes = resultBytes; }
        internal static SetOperandPending? For(List<object?[]>? rows)
            => rows is not null && _pending?.TryGetValue(rows, out var pending) == true ? pending : null;
        internal static void Attach(List<object?[]> rows, SetOperandPending pending, SqlStatementMetrics metrics, SqlSetOperationPlan plan)
        {
            pending._metrics = metrics; LazyInitializer.EnsureInitialized(ref _pending).Add(rows, pending);
            RegisterSetStage(metrics, plan, pending);
        }
        internal static SetOperandPending? Take(List<object?[]> rows)
        { var pending = For(rows); _pending?.Remove(rows); return pending; }
        internal async ValueTask CloseAsync(SqlStatementMetrics metrics)
        {
            Current = null;
            try
            {
                try { if (Reader is not null) { await Reader.DisposeAsync().ConfigureAwait(false); } }
                finally { if (Result is not null) { await Result.DisposeAsync().ConfigureAwait(false); } }
            }
            catch (Exception cleanup) when (cleanup is StorageTempException or SqlExecutionException or AggregateException)
            { (Failure ?? Refusal).Data["SetOperandCleanup"] = cleanup; }
            finally { Reader = null; Result = null; metrics.ReleasePlainSelectMemory(ResultBytes); ResultBytes = 0; }
        }
        public void Dispose()
        {
            try { CloseAsync(_metrics!).GetAwaiter().GetResult(); }
            catch (Exception cleanup) when (cleanup is StorageTempException or SqlExecutionException or AggregateException)
            { Refusal.Data["SetOperandCleanup"] = cleanup; }
        }
        private sealed class DebugView { public DebugView(SetOperandPending value) { } }
    }
}
