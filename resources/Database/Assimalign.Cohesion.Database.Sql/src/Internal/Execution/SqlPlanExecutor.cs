using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Executes bound plans against shared storage: table scans with predicate
/// filtering, projection/sort/limit for SELECT, typed writes for DML with
/// secondary-index maintenance, and catalog calls for DDL. Results are
/// sorted with a stable ORDER BY; without ORDER BY their order is unspecified
/// and may change with the chosen access path.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlPlanExecutor")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor.DebugView))]
internal sealed partial class SqlPlanExecutor
{
    private static readonly string _errorExecutorEnsureLatestVersionFailure = ErrorMessages.ExecutorEnsureLatestVersionFailure;

    // Test seam: after the final INSERT effect, while its statement apply is still admitted.
    internal static Action? AfterInsertApplied { get; set; }
    internal static Action? AfterSelfCommit { get; set; }

    private readonly SqlStorage _storage;
    private readonly SqlCatalog _catalog;
    private readonly BTreeIndexManager _indexManager;
    private readonly SqlBoundTableCache _definitions;

    /// <summary>
    /// The values this statement's subquery plans materialized, by slot: each slot is filled
    /// while the input plan its subquery belongs to runs. Null until the statement runs its first
    /// subquery plan.
    /// </summary>
    private SqlSubqueryValues? _subqueryValues;
    private bool _containsLike;
    private bool _isDml;
    private SqlStatementMetrics? _metrics;

    /// <summary>Initializes an executor for one statement.</summary>
    /// <param name="storage">The database's data storage.</param>
    /// <param name="catalog">The database's catalog.</param>
    /// <param name="indexManager">The database's index manager.</param>
    /// <param name="definitions">
    /// The database's bound table versions: the bound CHECK predicates and DEFAULT values every
    /// write and every read of a missing trailing field use.
    /// </param>
    /// <remarks>
    /// The executor takes no parameter values: the planner bound each parameter's supplied value
    /// into the plan's expressions (<see cref="SqlBoundParameter"/>), so nothing looks one up by
    /// name while the statement runs.
    /// </remarks>
    internal SqlPlanExecutor(SqlStorage storage, SqlCatalog catalog, BTreeIndexManager indexManager, SqlBoundTableCache definitions)
    {
        _storage = storage;
        _catalog = catalog;
        _indexManager = indexManager;
        _definitions = definitions;
    }

    /// <summary>
    /// The evaluator a plan's bound expressions run with: the statement's subquery values, and its
    /// cancellation token when a plan contains LIKE or the engine runs application code.
    /// Other plans still share the evaluator without subquery values or retained join columns,
    /// preserving their allocation budget even when their statement token is cancellable.
    /// </summary>
    private SqlExpressionEvaluator ExecutionEvaluator(SqlSubqueryValues? subqueryValues, CancellationToken cancellationToken,
        bool[]? sharedColumns = null, bool containsLike = false, DateTimeOffset? transactionTimestamp = null)
        => SqlExpressionEvaluator.ForExecution(subqueryValues,
            _containsLike || containsLike || _definitions.Functions.RunsApplicationCode ? cancellationToken : default, sharedColumns, transactionTimestamp ?? ViewTransactionStart);

    internal Task<DatabaseResult> ExecuteAsync(SqlPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // Set before WITH and subquery materialization, and retain it in every child read.
        _isDml |= StatementInput(plan) is SqlInsertPlan or SqlInsertSelectPlan or SqlUpdatePlan or SqlDeletePlan or SqlMergePlan;
        return JoinedDml(plan) && statement.Metrics.JoinedDmlStatement is null
            ? ExecuteJoinedStatementAsync(plan, statement, cancellationToken)
            : statement.Metrics.IsAnalyzing ? ExecuteAnalyzedAsync(plan, statement, cancellationToken)
            : ExecuteCoreAsync(plan, statement, cancellationToken);
    }

    private async Task<DatabaseResult> ExecuteAnalyzedAsync(SqlPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var metrics = statement.Metrics;
        var frame = metrics.BeginAnalysisFrame();
        try
        {
            var result = await ExecuteCoreAsync(plan, statement, cancellationToken).ConfigureAwait(false);
            if (plan is SqlSelectPlan)
            {
                metrics.Analysis(plan)!.Candidates += metrics.RecordsExamined - frame.StartRecords - frame.ChildRecords;
            }
            else if (plan is SqlJoinPlan { Steps.Count: > 0 })
            {
                metrics.Analysis(plan)!.Candidates += metrics.JoinCandidatePairs - frame.StartPairs - frame.ChildPairs;
            }
            return result;
        }
        finally { metrics.EndAnalysisFrame(frame); }
    }

    private async Task<DatabaseResult> ExecuteCoreAsync(SqlPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        _containsLike |= plan.ContainsLike;
        _metrics = statement.Metrics;
        plan = InitializeWindow(plan, cancellationToken);
        switch (plan)
        {
            case SqlWindowAggregatePlan window:
                return await ExecuteWindowAggregateAsync(window, statement, cancellationToken).ConfigureAwait(false);
            case SqlSetOperationPlan set:
                return await ExecuteSetOperationAsync(set, statement, cancellationToken).ConfigureAwait(false);
            case SqlConstantPlan constant:
                if (_subqueryValues is { HasCorrelatedQueries: true })
                {
                    return await ExecuteCorrelatedSelectAsync([Array.Empty<object?>()], constant.Projections,
                        constant.Where, constant.OrderBy, constant.Limit, constant.Offset, constant.IsDistinct,
                        statement, cancellationToken).ConfigureAwait(false);
                }
                var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
                return MaterializeSelect(evaluator.Matches(constant.Where, []) ? [Array.Empty<object?>()] : [],
                    constant.Projections, constant.OrderBy, constant.Limit, constant.Offset, constant.IsDistinct,
                    evaluator, constant.OrderByProjections, cancellationToken);
            case SqlWithPlan with:
                foreach (var relation in with.Relations)
                {
                    await MaterializeRelationAsync(relation, statement, cancellationToken).ConfigureAwait(false);
                }
                return await ExecuteAsync(with.Input, statement, cancellationToken).ConfigureAwait(false);
            case SqlDefinitionPlan definition:
                return await ExecuteDefinitionAsync(definition, statement, cancellationToken).ConfigureAwait(false);
            case SqlSubqueryPlan subquery:
                return await ExecuteSubqueriesAsync(subquery.Input, subquery.Queries, statement, cancellationToken).ConfigureAwait(false);
            case SqlCorrelatedSubqueryPlan correlated:
                return await ExecuteSubqueriesAsync(correlated.Input, correlated.Queries, statement, cancellationToken).ConfigureAwait(false);
            case SqlInsertSelectPlan insertSelect:
                return await ExecuteInsertSelectAsync(insertSelect, statement, cancellationToken).ConfigureAwait(false);
            case SqlGroupPlan group:
                return await ExecuteGroupAsync(group, statement, cancellationToken).ConfigureAwait(false);
            case SqlSystemViewPlan systemView:
                return _subqueryValues is { HasCorrelatedQueries: true }
                    ? await ExecuteCorrelatedSelectAsync(ReadCorrelatedSystemRelation(systemView.View, statement, cancellationToken),
                        systemView.Projections, systemView.Where, systemView.OrderBy, systemView.Limit, systemView.Offset,
                        systemView.IsDistinct, statement, cancellationToken).ConfigureAwait(false)
                    : ExecuteSystemView(systemView, statement, cancellationToken);
            case SqlSelectPlan select:
                return _subqueryValues is { HasCorrelatedQueries: true }
                    ? await ExecuteCorrelatedSelectAsync(EnumerateRows(select.Table, select.Access, statement, cancellationToken).Select(row => row.Values),
                        select.Projections, select.Where, select.OrderBy, select.Limit, select.Offset, select.IsDistinct,
                        statement, cancellationToken).ConfigureAwait(false)
                    : ExecuteSelect(select, statement, cancellationToken);
            case SqlJoinPlan join:
                return await ExecuteJoinAsync(join, statement, cancellationToken).ConfigureAwait(false);
            case SqlInsertPlan insert:
                return await ExecuteInsertAsync(insert, statement, cancellationToken).ConfigureAwait(false);
            case SqlUpdatePlan update:
                return await ExecuteUpdateAsync(update, statement, cancellationToken).ConfigureAwait(false);
            case SqlDeletePlan delete:
                return await ExecuteDeleteAsync(delete, statement, cancellationToken).ConfigureAwait(false);
            case SqlMergePlan merge:
                return await ExecuteMergeAsync(merge, statement, cancellationToken).ConfigureAwait(false);
            case SqlSequencePlan sequence:
                return await ExecuteSequenceDdlAsync(sequence, statement, cancellationToken).ConfigureAwait(false);
            case SqlCreateTablePlan create:
                return await ExecuteCreateTableAsync(create, statement, cancellationToken).ConfigureAwait(false);
            case SqlCreateTableAsPlan create:
                return await ExecuteCreateTableAsAsync(create, statement, cancellationToken).ConfigureAwait(false);
            case SqlCreateViewPlan createView:
                return await ExecuteCreateViewAsync(createView, statement, cancellationToken).ConfigureAwait(false);
            case SqlDropViewPlan dropView:
                return await ExecuteDropViewAsync(dropView, statement, cancellationToken).ConfigureAwait(false);
            case SqlCreateSchemaPlan createSchema:
                return await ExecuteCreateSchemaAsync(createSchema, statement, cancellationToken).ConfigureAwait(false);
            case SqlTruncatePlan truncate:
                return await ExecuteTruncateAsync(truncate, statement, cancellationToken).ConfigureAwait(false);
            case SqlDropTablePlan drop:
                return await ExecuteDropTableAsync(drop, statement, cancellationToken).ConfigureAwait(false);
            case SqlDropSchemaPlan dropSchema:
                return await ExecuteDropSchemaAsync(dropSchema, statement, cancellationToken).ConfigureAwait(false);
            case SqlCreateIndexPlan createIndex:
                return await ExecuteCreateIndexAsync(createIndex, statement, cancellationToken).ConfigureAwait(false);
            case SqlDropIndexPlan dropIndex:
                return await ExecuteDropIndexAsync(dropIndex, statement, cancellationToken).ConfigureAwait(false);
            case SqlAddColumnPlan addColumn:
                return await ExecuteAddColumnAsync(addColumn, statement, cancellationToken).ConfigureAwait(false);
            case SqlRenamePlan rename:
                return await ExecuteRenameAsync(rename, statement, cancellationToken).ConfigureAwait(false);
            case SqlAlterColumnPlan alterColumn:
                return await ExecuteAlterColumnAsync(alterColumn, statement, cancellationToken).ConfigureAwait(false);
            case SqlAddConstraintPlan addConstraint:
                return await ExecuteAddConstraintAsync(addConstraint, statement, cancellationToken).ConfigureAwait(false);
            case SqlDropConstraintPlan dropConstraint:
                return await ExecuteDropConstraintAsync(dropConstraint, statement, cancellationToken).ConfigureAwait(false);
            case SqlDropColumnPlan dropColumn:
                return await ExecuteDropColumnAsync(dropColumn, statement, cancellationToken).ConfigureAwait(false);
            default:
                throw new SqlExecutionException(SqlExecutionException.InternalErrorCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PlanNotExecutable, plan.GetType().Name));
        }
    }

    // ── SELECT ─────────────────────────────────────────────────────────

    private DatabaseResult ExecuteSelect(SqlSelectPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        // The access path narrows the candidate set; the full WHERE stays the
        // residual predicate for both paths, so seek results are equivalent to
        // scan results by construction.
        return MaterializeSelect(Matches(), plan.Projections, plan.OrderBy, plan.Limit, plan.Offset,
            plan.IsDistinct, evaluator, plan.OrderByProjections, cancellationToken);

        IEnumerable<object?[]> Matches()
        {
            foreach (var (_, values) in EnumerateRows(plan.Table, plan.Access, statement, cancellationToken))
            {
                if (evaluator.Matches(plan.Where, values)) { yield return values; }
            }
        }
    }

    /// <summary>Applies the common SELECT projection, ordering, distinctness and window.</summary>
    private DatabaseResult MaterializeSelect(IEnumerable<object?[]> matches, IReadOnlyList<SqlProjection> projections,
        IReadOnlyList<SqlBoundOrdering> orderBy, SqlRowCount? limit, SqlRowCount? offset,
        bool isDistinct, SqlExpressionEvaluator evaluator,
        IReadOnlyDictionary<SqlExpression, int>? orderByProjections, CancellationToken cancellationToken)
    {
        var projected = ProjectAndSortRows(matches, projections, orderBy, evaluator, orderByProjections, cancellationToken,
            isDistinct ? null : limit, isDistinct ? null : offset);

        return FinishSelect(projected, projections, limit, offset, isDistinct, evaluator, cancellationToken);
    }

    private static DatabaseResult FinishSelect(List<object?[]> projected, IReadOnlyList<SqlProjection> projections,
        SqlRowCount? limit, SqlRowCount? offset, bool isDistinct, SqlExpressionEvaluator evaluator, CancellationToken cancellationToken,
        Collation[]? projectionCollations = null)
    {
        if (isDistinct)
        {
            projected = Deduplicate(projected, projections, evaluator, cancellationToken, projectionCollations);
        }

        return MaterializeProjected(projections, isDistinct ? ApplyRowWindow(projected, limit, offset) : projected);
    }

    private static DatabaseResult MaterializeProjected(IReadOnlyList<SqlProjection> projections,
        List<object?[]> rows, long affectedCount = -1)
    {
        var columns = new DatabaseColumn[projections.Count];
        for (int i = 0; i < projections.Count; i++)
        {
            columns[i] = new DatabaseColumn
            {
                Name = projections[i].Name, Ordinal = i, Type = projections[i].Type,
                MaxLength = projections[i].MaxLength,
                IsNullable = projections[i].IsNullable,
            };
        }

        return new SqlMaterializedResultSet(columns, rows, affectedCount);
    }

    /// <summary>Bounds nonnegative Int64 counts by the materialized rows before narrowing.</summary>
    private static List<object?[]> ApplyRowWindow(List<object?[]> rows, SqlRowCount? limit, SqlRowCount? offset)
    {
        // Planning rejects negative counts. The collection bounds make both checked casts safe,
        // even at Int64.MaxValue, without adding the requested offset and limit.
        int start = checked((int)Math.Min(offset?.Count ?? 0, rows.Count));
        int count = checked((int)Math.Min(limit?.Count ?? rows.Count, rows.Count - start));
        if (start + count < rows.Count) { rows.RemoveRange(start + count, rows.Count - start - count); }
        if (start > 0) { rows.RemoveRange(0, start); }
        return rows;
    }

    /// <summary>
    /// Keeps source columns available for scalar keys and materializes output values
    /// once for aliases and ordinals, matching grouped-query ordering semantics.
    /// </summary>
    private List<object?[]> ProjectAndSortRows(IEnumerable<object?[]> matches,
        IReadOnlyList<SqlProjection> projections, IReadOnlyList<SqlBoundOrdering> orderBy,
        SqlExpressionEvaluator evaluator, IReadOnlyDictionary<SqlExpression, int>? outputSlots, CancellationToken cancellationToken,
        SqlRowCount? limit = null, SqlRowCount? offset = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (orderBy.Count == 0)
        {
            // The common scan needs only its output list. Avoid allocating sort state
            // for an unordered query, including the unlimited single-row lookup path.
            var projected = new List<object?[]>();
            long skip = offset?.Count ?? 0;
            long remaining = limit?.Count ?? long.MaxValue;
            if (remaining == 0) { return projected; }
            foreach (var source in matches)
            {
                var output = ProjectRow(source, projections, evaluator, cancellationToken);
                if (skip > 0) { skip--; continue; }
                SqlWindowMemory.For(_metrics)?.Retain(output);
                projected.Add(output);
                if (--remaining == 0) { break; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return projected;
        }
        if (HasSequenceProjection(projections) &&
            !OrderingUsesSequence(projections, outputSlots?.Values))
        {
            // PostgreSQL planner.c:6703-6763 postpones volatile outputs independent of
            // sorting. Alias/ordinal keys need only their referenced, non-sequence outputs.
            var sources = new SqlRowCollector(orderBy, evaluator.SubqueryValues, limit, offset, cancellationToken, _metrics);
            if (sources.IsComplete) { return []; }
            var completed = outputSlots?.Values.ToHashSet();
            foreach (var source in matches)
            {
                var orderedRow = OrderingRow(source, projections, completed, evaluator, cancellationToken);
                var keys = new object?[orderBy.Count];
                for (int index = 0; index < keys.Length; index++) { keys[index] = evaluator.Evaluate(orderBy[index].Key, orderedRow); }
                sources.Add(orderedRow, keys);
            }
            var selected = sources.Finish(resolveEmptyCollations: false, includeOffset: true);
            var projected = new List<object?[]>(selected.Count);
            foreach (var source in selected)
            {
                projected.Add(ProjectRow(source, projections, evaluator, cancellationToken, completed,
                    completed is { Count: > 0 } ? source.Length - projections.Count : 0));
            }
            return ApplyRowWindow(projected, limit, offset);
        }
        if (outputSlots is not { Count: > 0 } && projections.Any(projection => ContainsVolatileCall(projection.Value)))
        {
            // Preserve the old sort-then-project path, including its call count before
            // pagination. PostgreSQL planner.c:6649-6662 likewise postpones independent
            // volatile outputs. Aliases/ordinals still require projection before sorting.
            var sources = matches.ToList();
            if (_metrics is not null) { _metrics.PeakOrderedRows = Math.Max(_metrics.PeakOrderedRows, sources.Count); }
            if (sources.Count == 0) { return []; }
            var sorted = SortRows(sources, orderBy, evaluator, cancellationToken);
            var projected = new List<object?[]>(sorted.Count);
            foreach (var source in sorted) { projected.Add(ProjectRow(source, projections, evaluator, cancellationToken)); }
            cancellationToken.ThrowIfCancellationRequested();
            return ApplyRowWindow(projected, limit, offset);
        }
        var rows = new SqlRowCollector(orderBy, evaluator.SubqueryValues, limit, offset, cancellationToken, _metrics);
        if (rows.IsComplete) { return []; }
        foreach (var source in matches)
        {
            var output = ProjectRow(source, projections, evaluator, cancellationToken);
            var orderedRow = source;
            if (outputSlots is { Count: > 0 })
            {
                // Output aliases/ordinals follow the source slots. This combined row is
                // transient: only completed outputs and fixed keys survive this iteration.
                orderedRow = new object?[source.Length + output.Length];
                source.CopyTo(orderedRow, 0);
                output.CopyTo(orderedRow, source.Length);
            }
            var keys = orderBy.Count == 0 ? Array.Empty<object?>() : new object?[orderBy.Count];
            for (int i = 0; i < keys.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                keys[i] = evaluator.Evaluate(orderBy[i].Key, orderedRow);
            }
            rows.Add(output, keys);
            if (rows.IsComplete) { break; }
        }
        // The base ordinary SELECT skips sort/collation resolution over empty input.
        return rows.Finish(resolveEmptyCollations: false);
    }

    private static object?[] OrderingRow(object?[] source, IReadOnlyList<SqlProjection> projections,
        IReadOnlySet<int>? outputSlots, SqlExpressionEvaluator evaluator, CancellationToken cancellationToken)
    {
        if (outputSlots is not { Count: > 0 }) { return source; }
        var orderedRow = new object?[source.Length + projections.Count];
        source.CopyTo(orderedRow, 0);
        foreach (int index in outputSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projection = projections[index];
            orderedRow[source.Length + index] = projection.ColumnOrdinal is int ordinal ? evaluator.DeliverColumn(source, ordinal)
                : NormalizeGroupValue(evaluator.Evaluate(projection.Value!, source, copySharedBinary: true), projection.Type);
        }
        return orderedRow;
    }

    private static bool ContainsVolatileCall(SqlBoundExpression? expression)
    {
        if (expression is SqlBoundSequence) { return true; }
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is null) { return false; }
        if (expression is SqlBoundCall { Function.Volatility: SqlFunctionVolatility.Volatile }) { return true; }
        if (expression is SqlBoundCase choice)
        {
            if (ContainsVolatileCall(choice.Input) || ContainsVolatileCall(choice.ElseResult)) { return true; }
            foreach (var when in choice.Whens)
            {
                if (ContainsVolatileCall(when.Condition) || ContainsVolatileCall(when.Result)) { return true; }
            }
        }
        if (expression is SqlBoundLogical logical && logical.Operands.Any(ContainsVolatileCall)) { return true; }
        if (expression is SqlBoundCoalesce coalesce && coalesce.Arguments.Any(ContainsVolatileCall)) { return true; }
        return BoundChildren(expression).Any(ContainsVolatileCall);
    }

    private static bool ContainsSequenceCall(SqlBoundExpression? expression)
    {
        if (expression is null) { return false; }
        if (expression is SqlBoundSequence) { return true; }
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlBoundCase choice)
        {
            if (ContainsSequenceCall(choice.Input) || ContainsSequenceCall(choice.ElseResult)) { return true; }
            foreach (var when in choice.Whens)
            { if (ContainsSequenceCall(when.Condition) || ContainsSequenceCall(when.Result)) { return true; } }
        }
        if (expression is SqlBoundLogical logical && logical.Operands.Any(ContainsSequenceCall)) { return true; }
        if (expression is SqlBoundCoalesce coalesce && coalesce.Arguments.Any(ContainsSequenceCall)) { return true; }
        return BoundChildren(expression).Any(ContainsSequenceCall);
    }

    private static bool HasSequenceProjection(IReadOnlyList<SqlProjection>? projections)
    {
        if (projections is null || SqlSequenceExecution.Current is null) { return false; }
        foreach (var projection in projections) { if (ContainsSequenceCall(projection.Value)) { return true; } }
        return false;
    }

    private static bool OrderingUsesSequence(IReadOnlyList<SqlProjection> projections, IEnumerable<int>? slots)
    {
        if (slots is not null)
        {
            foreach (int index in slots) { if (ContainsSequenceCall(projections[index].Value)) { return true; } }
        }
        return false;
    }

    private static object?[] ProjectRow(object?[] row, IReadOnlyList<SqlProjection> projections,
        SqlExpressionEvaluator evaluator, CancellationToken cancellationToken,
        IReadOnlySet<int>? completed = null, int projectionStart = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var rowScope = SqlSequenceExecution.Current?.BeginRow();
        var output = new object?[projections.Count];
        for (int i = 0; i < output.Length; i++)
        {
            var projection = projections[i];
            output[i] = completed?.Contains(i) == true ? row[projectionStart + i]
                : projection.ColumnOrdinal is int ordinal ? evaluator.DeliverColumn(row, ordinal)
                : NormalizeGroupValue(evaluator.Evaluate(projection.Value!, row, copySharedBinary: true), projection.Type);
            if (output[i] is byte[])
            {
                // One decoded cell can be projected more than once, directly or through CASE,
                // COALESCE or an identity CAST. Transfer its first use; copy subsequent uses.
                for (int previous = 0; previous < i; previous++)
                {
                    if (ReferenceEquals(output[previous], output[i]))
                    {
                        output[i] = SqlBinaryValue.CopyShared(output[i]);
                        break;
                    }
                }
            }
        }
        return output;
    }

    private static int CompareOrderingValues(object? left, object? right, int direction, int nullComparison, Collation collation)
    {
        // NULL placement is independent of direction; the default was resolved during binding.
        if (left is null) { return right is null ? 0 : nullComparison; }
        if (right is null) { return -nullComparison; }
        return direction * Math.Sign(SqlValueComparer.Compare(left, right, collation));
    }

    private static List<object?[]> SortRows(List<object?[]> rows, IReadOnlyList<SqlBoundOrdering> orderBy,
        SqlExpressionEvaluator evaluator, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int operations = 0;
        // Precompute sort keys; OrderBy is a stable sort, satisfying determinism.
        var keyed = rows.Select(row => (Row: row, Keys: EvaluateKeys(row)));

        IOrderedEnumerable<(object?[] Row, object?[] Keys)>? ordered = null;

        for (int i = 0; i < orderBy.Count; i++)
        {
            int index = i;
            var ordering = orderBy[index];
            var collation = ordering.Collation.Resolve(evaluator.SubqueryValues);
            int direction = ordering.IsDescending ? -1 : 1;
            int nullComparison = ordering.NullsFirst ? -1 : 1;
            var comparer = Comparer<object?>.Create((left, right) =>
            {
                PollCancellation();
                return CompareOrderingValues(left, right, direction, nullComparison, collation);
            });

            ordered = ordered is null
                ? keyed.OrderBy(x => x.Keys[index], comparer)
                : ordered.ThenBy(x => x.Keys[index], comparer);
        }

        try
        {
            var sorted = ordered!.Select(x => x.Row).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            return sorted;
        }
        catch (InvalidOperationException exception) when (exception.InnerException is DatabaseException or OperationCanceledException)
        {
            // The runtime sort wraps a throwing comparer in InvalidOperationException.
            // Two keys the value order cannot compare are a statement error, so the
            // comparer's own DatabaseException is the failure, not a session-ending fault.
            ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }

        object?[] EvaluateKeys(object?[] row)
        {
            var keys = new object?[orderBy.Count];
            for (int i = 0; i < keys.Length; i++)
            {
                PollCancellation();
                keys[i] = evaluator.Evaluate(orderBy[i].Key, row);
            }
            return keys;
        }

        void PollCancellation()
        {
            // Bound cancellation latency during both key evaluation and the runtime's sort.
            if ((operations++ & 31) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
        }
    }

    /// <summary>Hashes each projected string with exactly the collation used for its equality.</summary>
    private static List<object?[]> Deduplicate(List<object?[]> rows, IReadOnlyList<SqlProjection> projections,
        SqlExpressionEvaluator evaluator, CancellationToken cancellationToken, Collation[]? collations = null)
    {
        var seen = new HashSet<object?[]>(new GroupKeyComparer(collations ?? projections
            .Select(projection => projection.Collation.Resolve(evaluator.SubqueryValues)).ToArray()));
        int count = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[i];
            if (seen.Add(row)) { rows[count++] = row; }
        }
        rows.RemoveRange(count, rows.Count - count);
        return rows;
    }

    private static Collation[] CaptureProjectionCollations(IReadOnlyList<SqlProjection> projections,
        SqlExpressionEvaluator evaluator, Collation[]? collations, CancellationToken cancellationToken)
    {
        bool first = collations is null;
        collations ??= new Collation[projections.Count];
        for (int i = 0; i < projections.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (first || !projections[i].Collation.IsFixed)
            {
                collations[i] = projections[i].Collation.Resolve(evaluator.SubqueryValues);
            }
        }
        return collations;
    }

    // ── DML ────────────────────────────────────────────────────────────
    //
    // Write statements execute in two phases (area DESIGN §3.8 step 3):
    //
    //   Phase 1 (no physical bracket): seek or scan through the statement snapshot,
    //   collect targets, and acquire locks through the lock manager — an
    //   IntentExclusive lock on the table, then an Exclusive lock per target
    //   row (keyed by the version's packed location, the same identity the
    //   version-store ledger uses). Lock waits are asynchronous, honor the
    //   session's cancellation token, and are where deadlocks are detected
    //   (the requester whose wait would close a cycle aborts).
    //
    //   Phase 2 (the coordinator's gated apply bracket): re-validate each
    //   target against its CURRENT stamps — the latest-state check under the
    //   exclusive lock, the B+Tree uniqueness-discipline precedent; a
    //   snapshot-only check here would admit write skew. A target tombstoned
    //   by a concurrently COMMITTED transaction fails the statement with a
    //   retryable write-write conflict (first-updater-wins); a tombstone from
    //   a transaction that rolled back was already cleared by its undo before
    //   its locks released, so the re-validation passes. Then apply: inserts
    //   and new update-versions stamp the writer, tombstones stamp the
    //   deleter, and every effect is recorded in the version-store ledger so
    //   rollback can undo it logically.

    private async Task<DatabaseResult> ExecuteInsertAsync(SqlInsertPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // A VALUES row has no columns in scope: the planner rejects column references (#1165) and
        // binds the values with no columns in scope, so no reference can resolve to an ordinal the
        // empty row they are evaluated against does not have.
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var values = new List<object?[]>(plan.Rows.Count);
        foreach (var valueRow in plan.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rowScope = SqlSequenceExecution.Current?.BeginRow();
            var row = new object?[valueRow.Length];
            for (int i = 0; i < row.Length; i++)
            {
                if (valueRow[i].Kind != SqlBoundExpressionKind.DefaultItem)
                {
                    row[i] = evaluator.Evaluate(valueRow[i], Array.Empty<object?>(), copySharedBinary: true);
                }
            }
            values.Add(row);
        }
        return await ExecuteInsertRowsAsync(plan.Table, plan.TargetOrdinals, values, statement, cancellationToken, plan.Returning, plan.Rows, plan.OnConflict).ConfigureAwait(false);
    }

    private Task<DatabaseResult> ExecuteUpdateAsync(SqlUpdatePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
        => plan.Source is null ? ExecutePlainUpdateAsync(plan, statement, cancellationToken)
            : ExecuteJoinedUpdateAsync(plan, statement, cancellationToken);

    private Task<DatabaseResult> ExecuteDeleteAsync(SqlDeletePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
        => plan.Source is null ? ExecutePlainDeleteAsync(plan, statement, cancellationToken)
            : ExecuteJoinedDeleteAsync(plan, statement, cancellationToken);

    private async Task<DatabaseResult> ExecutePlainUpdateAsync(SqlUpdatePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        await AcquireReferenceIntentLocksAsync(plan.Table, statement, cancellationToken).ConfigureAwait(false);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var indexes = GetLiveIndexes(plan.Table, statement);
        var targets = new List<(PageId PageId, int SlotIndex, object?[] Values)>();

        // Fully drain and dispose the candidate cursor before row-lock waits or writes.
        // This materialization also prevents Halloween revisits when SET moves an index key.
        foreach (var (location, values) in EnumerateRows(plan.Table, plan.Access, statement, cancellationToken))
        {
            await PrepareSubqueriesAsync(plan.Where, values, statement, cancellationToken).ConfigureAwait(false);
            if (evaluator.Matches(plan.Where, values))
            {
                targets.Add((location.PageId, location.SlotIndex, values));
            }
        }

        var returned = plan.Returning is null ? null : new List<object?[]>(targets.Count);
        var replacements = new List<(PageId PageId, int SlotIndex, object?[] OldValues, object?[] NewValues, byte[] NewVersion)>(targets.Count);
        var bound = _definitions.Get(plan.Table, cancellationToken);

        // Only the columns the statement sets are assigned their domain (phase E3): listed once, not per row.
        int[]? assigned = bound.Domains.Count > 0 ? plan.Assignments.Select(static assignment => assignment.Ordinal).ToArray() : null;
        foreach (var (pageId, slotIndex, values) in targets)
        {
            using var rowScope = SqlSequenceExecution.Current?.BeginRow();
            cancellationToken.ThrowIfCancellationRequested();
            var updated = (object?[])values.Clone();

            foreach (var (ordinal, expression) in plan.Assignments)
            {
                // Assignments evaluate against the ORIGINAL row (SQL semantics).
                updated[ordinal] = expression.Kind == SqlBoundExpressionKind.DefaultItem
                    ? ResolveColumnDefault(plan.Table, ordinal, bound.DefaultValues[ordinal], statement, cancellationToken)
                    : CoerceForColumn(evaluator.Evaluate(expression, values, copySharedBinary: true), plan.Table.Columns[ordinal]);
            }

            if (assigned is not null)
            {
                CheckDomains(bound, updated, evaluator, assigned);
            }

            replacements.Add((pageId, slotIndex, values, updated,
                SqlRowCodec.Encode(plan.Table, updated, statement.Transaction.Sequence)));
        }

        // Row locks come before the constraint reads, not after them: the
        // incoming-reference check below reads child tables in latest state, and
        // that is only sound while this statement holds the exclusive lock on
        // every row whose key it might be changing (the referential-locking note
        // in SqlPlanExecutor.Constraints.cs).
        await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, targets.ConvertAll(t => (t.PageId, t.SlotIndex)), cancellationToken).ConfigureAwait(false);

        var references = new List<SqlParentReference>();
        var pendingUpdates = BuildConstraintUpdates(plan.Table,
            replacements.Select(row => (SqlRecordLocation.Pack(row.PageId, row.SlotIndex), row.OldValues, row.NewValues)), plan.Assignments);
        ValidateRows(plan.Table, replacements.Select(row => row.NewValues).ToList(), statement, cancellationToken,
            references: references, updates: pendingUpdates);
        await AcquireParentRowLocksAsync(references, statement, cancellationToken).ConfigureAwait(false);

        // A reassigned foreign key releases the row's old parent as well as taking
        // on the new one, so the old parent row needs the same shared lock. Keys
        // the statement leaves alone are already covered by the locks above.
        var released = new List<SqlParentReference>();
        CollectReleasedReferences(plan.Table, replacements.ConvertAll(row => row.OldValues), released, statement, cancellationToken,
            changed: (reference, index) => reference.ChildKey.Ordinals.Any(ordinal =>
            {
                return !ValuesEqual(replacements[index].NewValues[ordinal], replacements[index].OldValues[ordinal]);
            }));
        await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false).ConfigureAwait(false);

        foreach (var replacement in replacements)
        {
            ValidateParentUpdate(plan.Table, replacement.OldValues, replacement.NewValues, statement, cancellationToken, updates: pendingUpdates);
        }

        // Unique key locks after the row locks (the lock-ordering rule): both the
        // old key (its entry is tombstoned) and the new key (its entry is
        // inserted) of every unique index.
        var uniqueKeyHashes = new List<ulong>();
        foreach (var (_, _, oldValues, newValues, _) in replacements)
        {
            CollectUniqueKeyHashes(indexes, plan.Table, oldValues, uniqueKeyHashes);
            ValidateIndexKeysAndCollectUniqueHashes(indexes, plan.Table, newValues, uniqueKeyHashes);
        }

        await AcquireUniqueKeyLocksAsync(statement, plan.Table.ObjectId, uniqueKeyHashes, cancellationToken).ConfigureAwait(false);

        if (HasSequenceProjection(plan.Returning))
        {
            foreach (var replacement in replacements)
            { returned!.Add(ProjectRow(replacement.NewValues, plan.Returning!, evaluator, cancellationToken)); }
        }

        try
        {
            return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                int applied = 0;
                foreach (var (pageId, slotIndex, oldValues, _, _) in replacements)
                {
                    if ((applied++ & 63) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                    EnsureLatestVersion(plan.Table, pageId, slotIndex, statement.Transaction.Sequence);

                    // MVCC update = version chain in the record space: tombstone the
                    // old version in place (same-length write — never relocates) and
                    // insert the new version, both stamped with this transaction's
                    // sequence. The index entries mirror the row versions exactly:
                    // the old location's entries get the same deleter, the new
                    // location gets fresh entries with the same writer.
                    TombstoneVersion(statement, bracket, pageId, slotIndex);
                    await TombstoneIndexEntriesAsync(
                        statement, indexes, plan.Table, oldValues, SqlRecordLocation.Pack(pageId, slotIndex), cancellationToken).ConfigureAwait(false);

                }
                // Release every replaced unique mapping before inserting replacements,
                // so statement-consistent key swaps do not fail on an old live entry.
                applied = 0;
                foreach (var (pageId, slotIndex, _, newValues, newVersion) in replacements)
                {
                    if ((applied++ & 63) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                    var location = _storage.InsertRow(bracket, plan.Table.ObjectId, newVersion);
                    _storage.RowVersionLinks.Add(SqlRecordLocation.Pack(pageId, slotIndex),
                        SqlRecordLocation.Pack(location.PageId, location.SlotIndex), statement.Transaction.Sequence);
                    statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, location.PageId, location.SlotIndex);
                    await InsertIndexEntriesAsync(
                        statement, indexes, plan.Table, newValues, SqlRecordLocation.Pack(location.PageId, location.SlotIndex), cancellationToken).ConfigureAwait(false);
                    if (returned is not null && !HasSequenceProjection(plan.Returning)) { returned.Add(ProjectRow(newValues, plan.Returning!, evaluator, cancellationToken)); }
                }

                return await statement.PrepareResultAsync(returned is null ? new SqlQueryResult(DatabaseResultStatus.Success, replacements.Count)
                    : MaterializeProjected(plan.Returning!, returned, replacements.Count), cancellationToken).ConfigureAwait(false);
            }, durable: false, cancellationToken).ConfigureAwait(false);
        }
        catch (IndexUniqueViolationException exception)
        {
            throw TranslateUniqueViolation(plan.Table, exception);
        }
    }

    private async Task<DatabaseResult> ExecutePlainDeleteAsync(SqlDeletePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var locks = statement.Coordinator.LockManager;
        var statementMark = locks.MarkStatement(statement.Transaction.Sequence);
        var retryMark = statementMark;
        const int retainedWaitThreshold = 3;
        int refusals = 0;
        bool applyStarted = false;
        try
        {
            while (true)
            {
                try
                {
                    await AcquireReferenceIntentLocksAsync(plan.Table, statement, cancellationToken, outgoing: false, cascade: true).ConfigureAwait(false);
                    var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
                    var targets = new List<((PageId PageId, int SlotIndex) Location, object?[] Values)>();
                    foreach (var row in EnumerateRows(plan.Table, plan.Access, statement, cancellationToken))
                    {
                        await PrepareSubqueriesAsync(plan.Where, row.Values, statement, cancellationToken).ConfigureAwait(false);
                        if (evaluator.Matches(plan.Where, row.Values)) { targets.Add(row); }
                    }

                    // Lock the directly targeted rows as one sorted batch before the cascade
                    // walk reads any child table: a parent row must be exclusively locked
                    // before its incoming references are read in latest state. The walk locks
                    // each row it discovers the same way, in discovery order — a transitive
                    // closure cannot be pre-sorted, so referential waits rely on the lock
                    // manager's deadlock detection rather than on a global ordering.
                    await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, targets.ConvertAll(t => t.Location), cancellationToken, cascade: true).ConfigureAwait(false);

                    var returned = plan.Returning is null ? null : new List<object?[]>(targets.Count);
                    var returningTargets = plan.Returning is null ? null : targets.Select(target =>
                        SqlRecordLocation.Pack(target.Location.PageId, target.Location.SlotIndex)).ToHashSet();
                    var deletions = new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete>();
                    var scannedTables = new HashSet<ulong>();
                    var released = new List<SqlParentReference>();
                    foreach (var target in targets)
                    {
                        await CollectCascadeDeletesAsync(plan.Table, target.Location, target.Values, deletions, scannedTables, released,
                            statement, cancellationToken).ConfigureAwait(false);
                    }

                    // Every deleted row releases the references it held; the shared locks are
                    // what make a concurrent parent delete wait for this transaction to decide
                    // instead of reading the tombstones as absence.
                    await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false, cascade: true).ConfigureAwait(false);

                    var updates = await CollectReferentialUpdatesAsync(deletions, scannedTables, statement, cancellationToken).ConfigureAwait(false);
                    ValidateRestrictedDeletes(deletions, updates, statement, cancellationToken);
                    await LockConstraintDeletesAsync(deletions.Values, statement, cancellationToken).ConfigureAwait(false);
                    if (HasSequenceProjection(plan.Returning))
                    {
                        foreach (var deletion in deletions.Values)
                        {
                            if (deletion.Table.ObjectId == plan.Table.ObjectId && returningTargets!.Contains(SqlRecordLocation.Pack(deletion.Location.PageId, deletion.Location.SlotIndex)))
                            { returned!.Add(ProjectRow(deletion.Values, plan.Returning!, evaluator, cancellationToken)); }
                        }
                    }
                    return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
                    {
                        applyStarted = true;
                        // Revalidate every target before applying anything. The bracket makes all
                        // tombstones, versions and index changes one rollback unit.
                        foreach (var update in updates.Values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            EnsureLatestVersion(update.Table, update.Location.PageId, update.Location.SlotIndex, statement.Transaction.Sequence);
                        }
                        int applied = 0;
                        foreach (var deletion in deletions.Values)
                        {
                            if ((applied++ & 63) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                            EnsureLatestVersion(deletion.Table, deletion.Location.PageId, deletion.Location.SlotIndex, statement.Transaction.Sequence);
                            TombstoneVersion(statement, bracket, deletion.Location.PageId, deletion.Location.SlotIndex);
                            await TombstoneIndexEntriesAsync(statement, GetLiveIndexes(deletion.Table, statement), deletion.Table, deletion.Values,
                                SqlRecordLocation.Pack(deletion.Location.PageId, deletion.Location.SlotIndex), cancellationToken).ConfigureAwait(false);
                            if (returned is not null && !HasSequenceProjection(plan.Returning) && deletion.Table.ObjectId == plan.Table.ObjectId &&
                                returningTargets!.Contains(SqlRecordLocation.Pack(deletion.Location.PageId, deletion.Location.SlotIndex)))
                            {
                                returned.Add(ProjectRow(deletion.Values, plan.Returning!, evaluator, cancellationToken));
                            }
                        }
                        foreach (var update in updates.Values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            TombstoneVersion(statement, bracket, update.Location.PageId, update.Location.SlotIndex);
                            await TombstoneIndexEntriesAsync(statement, GetLiveIndexes(update.Table, statement), update.Table, update.OldValues,
                                SqlRecordLocation.Pack(update.Location.PageId, update.Location.SlotIndex), cancellationToken).ConfigureAwait(false);
                        }
                        foreach (var update in updates.Values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var location = _storage.InsertRow(bracket, update.Table.ObjectId, update.NewVersion);
                            _storage.RowVersionLinks.Add(SqlRecordLocation.Pack(update.Location.PageId, update.Location.SlotIndex),
                                SqlRecordLocation.Pack(location.PageId, location.SlotIndex), statement.Transaction.Sequence);
                            statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, location.PageId, location.SlotIndex);
                            try
                            {
                                await InsertIndexEntriesAsync(statement, GetLiveIndexes(update.Table, statement), update.Table, update.NewValues,
                                    SqlRecordLocation.Pack(location.PageId, location.SlotIndex), cancellationToken).ConfigureAwait(false);
                            }
                            catch (IndexUniqueViolationException exception)
                            {
                                throw TranslateUniqueViolation(update.Table, exception);
                            }
                        }
                        return await statement.PrepareResultAsync(returned is null ? new SqlQueryResult(DatabaseResultStatus.Success, targets.Count)
                            : MaterializeProjected(plan.Returning!, returned, targets.Count), cancellationToken).ConfigureAwait(false);
                    }, durable: false, cancellationToken).ConfigureAwait(false);
                }
                catch (SqlCascadeLockRetry refused)
                {
                    // Bound release/restart starvation on a hot parent. From the third
                    // refusal onward, retain the closure and queue normally; hard cycles
                    // still fail through AcquireAsync's deadlock detector.
                    if (refusals < retainedWaitThreshold) { refusals++; }
                    if (refusals < retainedWaitThreshold) { locks.ReleaseSince(retryMark); }
                    await locks.AcquireAsync(statement.Transaction.Sequence, refused.Resource, refused.Mode, cancellationToken).ConfigureAwait(false);
                    // Keep every waited grant across future back-offs. Releasing it permits
                    // contenders to hand the same row back and forth forever (#1668).
                    retryMark = locks.MarkStatement(statement.Transaction.Sequence);
                    statement.References.ClearAfterCascadeRetry();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        catch when (!applyStarted && !_storage.IsOffline)
        {
            // Unwritten failures release even the retained wait grants. Previous
            // statements and their upgrades survive; an offline owner keeps all locks.
            locks.ReleaseSince(statementMark);
            throw;
        }
    }

    /// <summary>
    /// Phase-one lock acquisition for a write statement: IntentExclusive on the
    /// table, then Exclusive per target row in deterministic (location) order.
    /// Locks belong to the transaction and release as a set at commit/rollback
    /// (two-phase locking); deadlocks surface here as the lock manager's
    /// requester-closes-cycle abort.
    /// </summary>
    private static async ValueTask AcquireRowWriteLocksAsync(
        SqlStatementContext statement,
        ulong objectId,
        List<(PageId PageId, int SlotIndex)> targets,
        CancellationToken cancellationToken, bool cascade = false)
    {
        await AcquirePreparationLockAsync(statement, LockResource.Object(objectId), LockMode.IntentExclusive, cascade, cancellationToken).ConfigureAwait(false);

        var keys = targets.ConvertAll(target => SqlRecordLocation.Pack(target.PageId, target.SlotIndex));
        keys.Sort();

        foreach (ulong key in keys)
        {
            await AcquirePreparationLockAsync(statement, LockResource.Entry(objectId, key), LockMode.Exclusive, cascade, cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Secondary-index maintenance ────────────────────────────────────
    //
    // Lock-ordering rule (keep it consistent across INSERT/UPDATE/DELETE so
    // cycles stay detectable and rare): a write statement acquires, in phase
    // one and in deterministic order,
    //
    //   1. the table's IntentExclusive object lock,
    //   2. its target rows' Exclusive locks, sorted by packed location,
    //   3. its unique-index key locks, sorted by key hash (deduplicated).
    //
    // ON CONFLICT is the second deliberate exception (beside the cascade walk):
    // proposal unique keys precede live-row arbitration. Row locks use TryAcquire:
    // contention releases all grants since the latest statement mark, waits through AcquireAsync,
    // keeps the wait grant (a new mark follows it, so later back-offs never release it), then
    // restarts the whole unapplied arbitration (#1668). A failed upsert releases everything it
    // took after the statement's first mark before it rethrows; nothing has been applied yet.
    // Replacement keys follow successfully locked conflict rows. Pre-statement grants
    // (including upgrades) survive; reversed multi-row key acquisition can still deadlock.
    //
    // The B+Tree re-acquires the key lock inside the apply gate — a same-owner
    // re-grant that completes synchronously — so no lock wait can ever happen
    // while the apply gate is held (a wait there would be invisible to the lock
    // manager's deadlock detection). Non-unique indexes take no key locks at
    // all. Key locks and row locks share the Entry resource space (a hash could
    // collide with a packed location); a collision only over-locks, and the
    // class ordering above keeps acquisition order globally consistent.

    /// <summary>
    /// Resolves a column name to its ordinal (case-insensitive, SQL identifier
    /// rules). Index key columns are validated at plan/DDL time, so a miss here
    /// is a programming error surfaced loudly.
    /// </summary>
    internal static int FindColumnOrdinal(SqlCatalogTable table, string columnName)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new SqlExecutionException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnNameMissing, table.Schema, table.Name, columnName));
    }

    /// <summary>
    /// Resolves the table's registered indexes to their live trees and key
    /// ordinals for one statement.
    /// </summary>
    private List<SqlLiveIndex> GetLiveIndexes(SqlCatalogTable table, SqlStatementContext statement)
    {
        var cached = statement.References.LiveIndexes;
        if (cached.TryGetValue(table, out var found)) { return found; }
        statement.Metrics.LiveIndexBuilds++;
        var metadataList = _catalog.GetIndexes(table.ObjectId);

        var result = new List<SqlLiveIndex>(metadataList.Count);

        foreach (var metadata in metadataList)
        {
            if (!_indexManager.TryGetIndex(table.ObjectId, metadata.Name, out var index))
            {
                throw new SqlExecutionException(SqlExecutionException.InternalErrorCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.WriteIndexUnavailable, metadata.Name, table.Schema, table.Name));
            }

            var ordinals = _definitions.Get(table).GetKey(metadata.ColumnNames).Ordinals;

            result.Add(new SqlLiveIndex(metadata, index, ordinals));
        }

        cached.Add(table, result);
        return result;
    }

    /// <summary>
    /// Builds an index key from a row's values: one order-preserving component
    /// per key column, transformed under the column's effective collation and
    /// temporal identity (null components participate — nulls sort first and
    /// count as key values).
    /// </summary>
    private IndexKey BuildIndexKeyUnchecked(SqlCatalogTable table, int[] keyOrdinals, object?[] values)
    {
        var writer = new DatabaseKeyWriter();

        foreach (int ordinal in keyOrdinals)
        {
            SqlRowCodec.AppendKeyValue(writer, table.Columns[ordinal].Type.Type, values[ordinal],
                table.Columns[ordinal].Collation ?? _catalog.DefaultCollation);
        }

        var key = IndexKey.From(writer);
        IndexKeyBuilt?.Invoke(key);
        return key;
    }

    /// <summary>
    /// Builds and validates a key that will be written, including during index construction.
    /// Locking, lookups and tombstones use the unchecked builder so old values and
    /// discarded conflict proposals never fail a write-size check.
    /// </summary>
    private IndexKey BuildIndexKey(SqlCatalogTable table, int[] keyOrdinals, object?[] values)
    {
        var key = BuildIndexKeyUnchecked(table, keyOrdinals, values);
        if (key.Length > BTreeIndex.MaxKeyLength)
        {
            throw SqlEvaluationException.IndexKeyTooLarge($"{table.Schema}.{table.Name}", key.Length, BTreeIndex.MaxKeyLength);
        }
        return key;
    }

    /// <summary>
    /// Collects lock hashes for existing values without validating or building nonunique keys.
    /// </summary>
    private void CollectUniqueKeyHashes(List<SqlLiveIndex> indexes, SqlCatalogTable table, object?[] values, List<ulong> hashes)
    {
        foreach (var liveIndex in indexes)
        {
            if (liveIndex.Metadata.IsUnique)
            {
                hashes.Add(BuildIndexKeyUnchecked(table, liveIndex.KeyOrdinals, values).Hash());
            }
        }
    }

    /// <summary>
    /// Validates every index key before apply and collects the unique keys' lock hashes.
    /// </summary>
    private void ValidateIndexKeysAndCollectUniqueHashes(List<SqlLiveIndex> indexes, SqlCatalogTable table, object?[] values, List<ulong> hashes)
    {
        foreach (var liveIndex in indexes)
        {
            var key = BuildIndexKey(table, liveIndex.KeyOrdinals, values);
            if (liveIndex.Metadata.IsUnique) { hashes.Add(key.Hash()); }
        }
    }

    /// <summary>
    /// Phase-one acquisition of the statement's unique-index key locks, in
    /// deterministic (sorted, deduplicated) order — after the row locks, per the
    /// lock-ordering rule above.
    /// </summary>
    private static async ValueTask AcquireUniqueKeyLocksAsync(
        SqlStatementContext statement,
        ulong objectId,
        List<ulong> keyHashes,
        CancellationToken cancellationToken, bool cascade = false)
    {
        if (keyHashes.Count == 0)
        {
            return;
        }

        keyHashes.Sort();

        ulong? previous = null;
        foreach (ulong hash in keyHashes)
        {
            if (previous == hash)
            {
                continue;
            }

            previous = hash;
            await AcquirePreparationLockAsync(statement, LockResource.Entry(objectId, hash), LockMode.Exclusive, cascade, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inserts one row version's entries into every index, stamped with the
    /// writing transaction's sequence, and records them in the version-store
    /// ledger so a logical rollback erases them again.
    /// </summary>
    private async ValueTask InsertIndexEntriesAsync(
        SqlStatementContext statement,
        List<SqlLiveIndex> indexes,
        SqlCatalogTable table,
        object?[] values,
        ulong entryReference,
        CancellationToken cancellationToken)
    {
        foreach (var liveIndex in indexes)
        {
            var key = BuildIndexKey(table, liveIndex.KeyOrdinals, values);
            try
            {
                await liveIndex.Index.InsertAsync(statement.Transaction, key, entryReference, cancellationToken).ConfigureAwait(false);
            }
            catch (IndexUniqueViolationException)
            {
                throw new SqlConstraintViolationException(liveIndex.Metadata.Name, $"{table.Schema}.{table.Name}", "UNIQUE",
                    SafeValue(liveIndex.KeyOrdinals.Select(ordinal => values[ordinal]).ToArray()));
            }
            statement.Coordinator.VersionStore.RecordIndexEntryCreated(statement.Transaction.Sequence, liveIndex.Versions, key.Encoded, entryReference);
        }
    }

    /// <summary>
    /// Tombstones one row version's entries in every index with the writing
    /// transaction's sequence (mirroring the row version's deleter stamp), and
    /// records them in the ledger so a logical rollback restores them.
    /// </summary>
    private async ValueTask TombstoneIndexEntriesAsync(
        SqlStatementContext statement,
        List<SqlLiveIndex> indexes,
        SqlCatalogTable table,
        object?[] values,
        ulong entryReference,
        CancellationToken cancellationToken)
    {
        foreach (var liveIndex in indexes)
        {
            var key = BuildIndexKeyUnchecked(table, liveIndex.KeyOrdinals, values);
            await liveIndex.Index.DeleteAsync(statement.Transaction, key, entryReference, cancellationToken).ConfigureAwait(false);
            statement.Coordinator.VersionStore.RecordIndexEntryTombstoned(statement.Transaction.Sequence, liveIndex.Versions, key.Encoded, entryReference);
        }
    }

    /// <summary>
    /// Translates the index layer's unique violation into the area's error
    /// surface at the model boundary (the recorded child-root error policy).
    /// The statement's physical bracket has already rolled back — the session
    /// stays usable.
    /// </summary>
    private static DatabaseException TranslateUniqueViolation(SqlCatalogTable table, IndexUniqueViolationException exception)
        => new SqlConstraintViolationException(exception.IndexName, $"{table.Schema}.{table.Name}", "UNIQUE");

    /// <summary>
    /// The latest-state check under the exclusive row lock: a target tombstoned
    /// by a concurrently committed transaction fails the statement with a
    /// retryable write-write conflict — first-updater-wins. (A snapshot
    /// visibility check alone would admit write skew; this is the B+Tree
    /// uniqueness-discipline precedent applied to row updates.)
    /// </summary>
    /// <exception cref="TransactionAbortedException">The row was modified by a concurrently committed transaction.</exception>
    /// <exception cref="StorageCorruptionException">
    /// The row's slot holds a record too short for its version stamps. <c>ReadRow</c> fails on a
    /// deleted slot, so a short record is a live slot's, and it is damaged, not a reclaimed version
    /// to retry against (#1362).
    /// </exception>
    private void EnsureLatestVersion(SqlCatalogTable table, PageId pageId, int slotIndex, TransactionSequence self, bool codeSerialization = false)
    {
        var (_, deleter) = ReadVersionStamps(table, pageId, slotIndex);

        if (deleter != TransactionSequence.None && deleter != self)
        {
            var conflict = new TransactionAbortedException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorExecutorEnsureLatestVersionFailure, table.Schema, table.Name, deleter));
            if (codeSerialization)
            {
                throw new DatabaseTransactionAbortedException(conflict.Message,
                    [new Diagnostic { Code = SqlExecutionException.SerializationFailureCode, Severity = DiagnosticSeverity.Error, Message = conflict.Message }], conflict);
            }
            throw conflict;
        }
    }

    /// <summary>
    /// Reads the writer and deleter stamps of the row version in a slot. The caller holds the
    /// row's exclusive lock, so the slot holds a live version the purge cannot reclaim.
    /// </summary>
    /// <exception cref="StorageCorruptionException">
    /// The slot holds a record too short for its version stamps: damage, not a version to retry
    /// against (#1362).
    /// </exception>
    private (TransactionSequence Writer, TransactionSequence Deleter) ReadVersionStamps(SqlCatalogTable table, PageId pageId, int slotIndex)
    {
        _metrics?.RecordRowRead();
        var record = _storage.ReadRow(pageId, slotIndex);

        if (record.Length < SqlRowCodec.StampHeaderSize)
        {
            throw new StorageCorruptionException(
                pageId,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EnsureSnapshotSeesCascadeRowFailure2, slotIndex, (long)pageId, table.Schema, table.Name, record.Length, SqlRowCodec.StampHeaderSize));
        }

        return SqlRowCodec.ReadStamps(record.Span);
    }

    /// <summary>
    /// Stamps the record's deleter with the statement's transaction sequence —
    /// the same-length in-place tombstone write — and records it in the
    /// version-store ledger for logical undo and pruning.
    /// </summary>
    private void TombstoneVersion(SqlStatementContext statement, StorageTransaction bracket, PageId pageId, int slotIndex)
    {
        statement.Metrics.RecordRowRead();
        var current = _storage.ReadRow(pageId, slotIndex);
        byte[] tombstoned = SqlRowCodec.WithDeleter(current.Span, statement.Transaction.Sequence);
        _storage.UpdateRow(bracket, pageId, slotIndex, tombstoned);
        // A failed UPDATE followed by DELETE in the same transaction must not reuse
        // the failed replacement link (the transaction stamp alone cannot distinguish it).
        _storage.RowVersionLinks.Forget(SqlRecordLocation.Pack(pageId, slotIndex));
        statement.Coordinator.VersionStore.RecordTombstoned(statement.Transaction.Sequence, pageId, slotIndex);
    }

    // ── DDL ────────────────────────────────────────────────────────────

    /// <summary>
    /// Awaits one catalog self-commit of a DDL statement that changes what a reopened database
    /// shows (it publishes, alters or removes a definition) and records it on the statement once
    /// its commit returned. The session reads the record when the statement meets an offline
    /// storage: a statement that already committed such a change is unconfirmed, because the
    /// change survives the reopen; one that committed none is refused like any other statement
    /// (#1272). A commit whose own record was written before its flush failed is unconfirmed
    /// either way (<c>StorageOfflineException.CommitRecordWritten</c>).
    /// </summary>
    /// <remarks>
    /// A durable step nothing can reach before the catalog publishes it is not recorded: CREATE
    /// TABLE's identity reservation persists only the object-id counter, and the index trees CREATE
    /// TABLE, ADD CONSTRAINT and CREATE INDEX build before their catalog commit are orphaned pages
    /// until it. A statement that failed after them leaves a reopened database without the table or
    /// index, so it is refused, and a retry is safe.
    /// </remarks>
    /// <param name="statement">The DDL statement.</param>
    /// <param name="commit">The self-commit.</param>
    /// <returns>A task that completes once the bracket committed.</returns>
    private static async ValueTask SelfCommitAsync(SqlStatementContext statement, ValueTask commit)
    {
        await commit.ConfigureAwait(false);
        statement.Metrics.RecordSelfCommit();
        AfterSelfCommit?.Invoke();
    }

    /// <summary>
    /// Awaits one catalog self-commit of a DDL statement that changes what a reopened database shows
    /// and records it on the statement once its commit returned (see
    /// <see cref="SelfCommitAsync(SqlStatementContext, ValueTask)"/>).
    /// </summary>
    /// <typeparam name="T">The commit's result.</typeparam>
    /// <param name="statement">The DDL statement.</param>
    /// <param name="commit">The self-commit.</param>
    /// <returns>The commit's result.</returns>
    private static async ValueTask<T> SelfCommitAsync<T>(SqlStatementContext statement, ValueTask<T> commit)
    {
        T result = await commit.ConfigureAwait(false);
        statement.Metrics.RecordSelfCommit();
        AfterSelfCommit?.Invoke();
        return result;
    }

    private async Task<DatabaseResult> ExecuteCreateTableAsync(SqlCreateTablePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        SqlNamespaces.Require(_catalog, plan.Schema);
        if (plan.IfNotExists && _catalog.TryGetTable(plan.Schema, plan.Name, out _))
        {
            // A provisioner compensates every successful step. An object created
            // after it planned is not its work and must never enter that list.
            if (statement.ProvisioningSchema is not null)
            {
                throw new SqlExecutionException(SqlExecutionException.DuplicateTableCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableProvisioningCollision, plan.Schema, plan.Name));
            }

            return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
        }

        await CreateConstrainedTableAsync(plan, statement, cancellationToken).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    /// <summary>
    /// Drops a column as a catalog-only change (#1241): the catalog marks the column's
    /// physical ordinal dropped in one self-committed record and no stored version is
    /// read or written. Every version keeps the dropped component, which every decode
    /// skips (<see cref="SqlRowCodec.Decode"/>), and versions written afterwards store
    /// NULL there, or nothing when no live column follows. This is PostgreSQL's DROP COLUMN: <c>ATExecDropColumn</c>
    /// (<c>src/backend/commands/tablecmds.c:9355</c>) reaches <c>RemoveAttributeById</c>,
    /// which sets <c>attisdropped</c> and rewrites no tuple
    /// (<c>src/backend/catalog/heap.c:1692-1732</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The statement takes the table's Exclusive lock, so in-flight row writers finish
    /// first and later ones see the new definition (a writer bound to the old one fails
    /// with "changed while the statement was waiting"), and the constraint checks read
    /// a stable definition. It holds no apply gate and scans nothing: the work is O(1)
    /// in the table's size and no other table's writer waits on it.
    /// </para>
    /// <para>
    /// The one durable step is the catalog commit. A crash before it leaves the old
    /// definition, one after it the new one, and every stored version decodes correctly
    /// under either, because no physical ordinal is reused. For the same reason a SELECT
    /// that takes no table lock and overlaps the drop reads every value in its own
    /// column whichever definition it bound. One bound before the drop still sees the
    /// dropped column, with the values of the versions its snapshot sees; a version written
    /// after the drop is never visible to it, because the statement's snapshot is taken
    /// before it binds (<see cref="SqlStatementContext"/>) and every later writer waits for
    /// this statement's Exclusive lock.
    /// </para>
    /// </remarks>
    private async Task<DatabaseResult> ExecuteDropColumnAsync(SqlDropColumnPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!_catalog.TryGetTable(plan.Schema, plan.Name, out var before))
        {
            throw new SqlExecutionException(SqlExecutionException.UndefinedTableCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableMissing, plan.Schema, plan.Name));
        }

        await statement.Coordinator.LockManager.AcquireAsync(
            statement.DdlLockOwner, LockResource.Object(before.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        SqlCatalogTable current = ReadCurrentTable(before);
        EnsureCanChange(current.Owner, current.Name, current.OwningSchema, "ALTER TABLE DROP COLUMN", statement);
        EnsureSameIdentity(before, current);
        EnsureNoViewReads(current, plan.ColumnName);
        EnsureCanDropColumn(current, plan.ColumnName, cancellationToken);

        // Bind the projected version before publication, while folding still belongs to the
        // caller's cancellable validation. The catalog remains authoritative for invalid drops.
        int ordinal = Enumerable.Range(0, current.Columns.Count).FirstOrDefault(
            index => current.Columns[index].Name.Equals(plan.ColumnName, StringComparison.OrdinalIgnoreCase), -1);
        SqlCatalogTable? prepared = null;
        if (ordinal >= 0 && current.Columns.Count > 1 &&
            !current.PrimaryKeyColumns.Contains(plan.ColumnName, StringComparer.OrdinalIgnoreCase))
        {
            prepared = new SqlCatalogTable(current.ObjectId, current.Schema, current.Name,
                current.Columns.Where((_, index) => index != ordinal).ToArray(), current.PrimaryKeyColumns,
                current.Owner, current.OwningSchema, current.Constraints,
                [.. current.DroppedColumnOrdinals, current.GetPhysicalOrdinal(ordinal)]);
            _definitions.Get(prepared, cancellationToken);
        }

        // The authoritative step: the catalog checks the drop again under its own lock
        // and commits it. Bind the published version before the statement completes, as
        // every DDL does, so no later write parses its definitions.
        var updated = await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.DropColumnAsync(plan.Schema, plan.Name, plan.ColumnName, cancellationToken) : _catalog.DropColumnAsync(plan.Schema, plan.Name, plan.ColumnName, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false);
        if (prepared is not null) { _definitions.Adopt(updated, prepared, CancellationToken.None); }
        else { _definitions.Get(updated, CancellationToken.None); }
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private async Task<DatabaseResult> ExecuteDropTableAsync(SqlDropTablePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!plan.IfExists) { SqlNamespaces.Require(_catalog, plan.Schema); }
        if (SqlNamespaces.Find(_catalog, plan.Schema) is null || !_catalog.TryGetTable(plan.Schema, plan.Name, out var table))
        {
            if (plan.IfExists)
            {
                if (statement.ScriptReceipt is { } publication)
                {
                    await SelfCommitAsync(statement, _catalog.DropTableAsync(plan.Schema, plan.Name, publication, cancellationToken)).ConfigureAwait(false);
                }
                return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
            }

            throw new SqlCatalogException(SqlExecutionException.UndefinedTableCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableMissing, plan.Schema, plan.Name));
        }

        // The DDL-vs-writer interlock: an Exclusive table lock waits for every
        // in-flight row writer (IntentExclusive holders) to finish before the
        // table is dropped — and blocks new ones until this transaction ends.
        await statement.Coordinator.LockManager.AcquireAsync(
            statement.DdlLockOwner, LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        SqlCatalogTable current = ReadCurrentTable(table);
        EnsureCanChange(current.Owner, current.Name, current.OwningSchema, "DROP TABLE", statement);
        EnsureSameIdentity(table, current);
        table = current;
        EnsureNoViewReads(table);
        if (IncomingReferences(table).Any(reference => reference.Child.ObjectId != table.ObjectId))
        {
            throw new SqlExecutionException(SqlExecutionException.DependentObjectsCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableForeignKeyDependent, table.Name));
        }

        // The table's indexes fall with it. The catalog removes their metadata
        // and registrations atomically with the table record; the live trees
        // then leave the manager's directory (their owner-tagged pages await the
        // reclamation worker). Order matters: catalog first (durable), directory
        // second, all under the exclusive lock so no writer maintains a ghost.
        var droppedIndexes = _catalog.GetIndexes(table.ObjectId);

        await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.DropTableAsync(plan.Schema, plan.Name, cancellationToken) : _catalog.DropTableAsync(plan.Schema, plan.Name, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false);

        await RemoveDroppedIndexesAsync(statement, droppedIndexes, cancellationToken: CancellationToken.None).ConfigureAwait(false);

        // The catalog committed the retired identity with the unpublication. The
        // version-purge worker reclaims its rows and trees after existing readers finish.
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    // Catalog unpublication is irrevocable. Caller cancellation or a failed binding/directory
    // removal must not leave other retired handles live when reclamation frees their pages.
    private async Task RemoveDroppedIndexesAsync(SqlStatementContext statement,
        IReadOnlyList<SqlCatalogIndex> indexes, List<Exception>? failures = null, CancellationToken cancellationToken = default)
    {
        foreach (var index in indexes)
        {
            try
            {
                if (_indexManager.TryGetIndex(index.TableObjectId, index.Name, out _))
                {
                    await _indexManager.DropIndexAsync(statement.Transaction, index.TableObjectId, index.Name, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception failure)
            {
                // Complete the remaining mandatory removals before reporting any failures.
                (failures ??= []).Add(failure);
            }
        }
        if (failures is not null) { throw new AggregateException(failures); }
    }

    /// <summary>
    /// Acquires a table-grain lock for a DDL statement by schema-qualified name.
    /// </summary>
    private async ValueTask AcquireObjectLockAsync(
        SqlStatementContext statement,
        string schema,
        string name,
        string operation,
        CancellationToken cancellationToken)
    {
        if (_catalog.TryGetTable(schema, name, out var table))
        {
            await statement.Coordinator.LockManager.AcquireAsync(
                statement.DdlLockOwner, LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

            SqlCatalogTable current = ReadCurrentTable(table);
            EnsureCanChange(current.Owner, current.Name, current.OwningSchema, operation, statement);
            EnsureSameIdentity(table, current);
            return;
        }

        throw new SqlCatalogException(SqlExecutionException.UndefinedTableCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableMissing, schema, name));
    }

    /// <summary>
    /// Creates a secondary index: a DDL-blocking build under the table's
    /// Exclusive lock (in-flight writers finish first; new ones wait), inside a
    /// gated, <b>durably committed</b> bracket — the self-committing DDL posture,
    /// because the catalog's metadata+registration record commits independently
    /// and must never describe a tree whose pages a crash could revert. The
    /// build walks every stored version — visible or not — and inserts entries
    /// carrying the version's original stamps, so snapshots older than the index
    /// read exactly what the equivalent row scan shows them. Uniqueness is
    /// checked across the live versions during the build (the exclusive lock
    /// excludes concurrent writers, so live state cannot move underneath it).
    /// </summary>
    private async Task<DatabaseResult> ExecuteCreateIndexAsync(SqlCreateIndexPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (plan.Table.Constraints.Any(constraint => constraint.Name.Equals(plan.IndexName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SqlExecutionException(SqlExecutionException.DuplicateObjectCode, string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.ConstraintNameExists, plan.IndexName, plan.Table.Name));
        }
        if (_catalog.TryGetIndex(plan.Table.ObjectId, plan.IndexName, out _))
        {
            if (statement.ProvisioningSchema is not null)
            {
                throw new SqlExecutionException(SqlExecutionException.DuplicateTableCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IndexProvisioningCollision, plan.IndexName, plan.Table.Schema, plan.Table.Name));
            }

            if (plan.IfNotExists)
            {
                return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
            }

            throw new SqlExecutionException(SqlExecutionException.DuplicateTableCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExecutionIndexExists, plan.IndexName, plan.Table.Schema, plan.Table.Name));
        }

        await statement.Coordinator.LockManager.AcquireAsync(
            statement.DdlLockOwner, LockResource.Object(plan.Table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        var ordinals = new int[plan.ColumnNames.Count];
        for (int i = 0; i < ordinals.Length; i++)
        {
            ordinals[i] = FindColumnOrdinal(plan.Table, plan.ColumnNames[i]);
        }

        var definition = new IndexDefinition(plan.IndexName, IndexKind.BTree, plan.IsUnique);
        bool registered = false;
        int commitsBeforePublication = statement.Metrics.SelfCommits;

        try
        {
            if (statement.ProvisioningProgress is not null)
            {
                await BuildDeferredIndexAsync(plan.Table, definition, ordinals, statement,
                    () => registered = true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Durable, but not a self-commit the session counts (#1272): the tree is orphaned pages
                // until the catalog commit below describes it, so a failure here leaves no index.
                await statement.Coordinator.ApplyStatementAsync<bool>(statement.Transaction, async bracket =>
                {
                    var index = await _indexManager.CreateIndexAsync(statement.Transaction, plan.Table.ObjectId, definition, cancellationToken).ConfigureAwait(false);
                    registered = true;

                    foreach (var (location, values, writer, deleter) in ScanVersions(plan.Table, cancellationToken))
                    {
                        var key = BuildIndexKey(plan.Table, ordinals, values);

                        await index.InsertVersionAsync(
                            bracket, key, SqlRecordLocation.Pack(location.PageId, location.SlotIndex), writer, deleter, cancellationToken).ConfigureAwait(false);
                    }
                    await ValidateBuiltUniqueIndexAsync(plan.Table, index, statement, cancellationToken, ordinals).ConfigureAwait(false);
                    return true;
                }, durable: true, cancellationToken).ConfigureAwait(false);
            }
            // Metadata + registrations persist in ONE catalog self-commit: a crash
            // cannot leave a described index without a tree registration (or the
            // reverse). A crash before this write leaves only orphaned tree pages.
            var registrations = PublishedRegistrations([new SqlCatalogIndex(plan.Table.ObjectId, plan.IndexName, plan.ColumnNames, plan.IsUnique)]);
            await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.CreateIndexAsync(
                new SqlCatalogIndex(
                    plan.Table.ObjectId, plan.IndexName, plan.ColumnNames, plan.IsUnique,
                    statement.ProvisioningSchema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema,
                    statement.ProvisioningSchema),
                registrations, cancellationToken) : _catalog.CreateIndexAsync(
                new SqlCatalogIndex(
                    plan.Table.ObjectId, plan.IndexName, plan.ColumnNames, plan.IsUnique,
                    statement.ProvisioningSchema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema,
                    statement.ProvisioningSchema),
                registrations, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false);
        }
        catch
        {
            // A canceled build or publication must leave no unpublished directory entry.
            // Once publication committed, its enforcing tree must remain registered.
            if (registered && statement.Metrics.SelfCommits == commitsBeforePublication)
            {
                await _indexManager.DropIndexAsync(statement.Transaction, plan.Table.ObjectId, plan.IndexName, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    /// <summary>
    /// Drops a secondary index under the table's Exclusive lock: the catalog
    /// removes the metadata and registration first (one durable self-commit —
    /// the authoritative drop), then the live tree leaves the manager's
    /// directory; its pages await vacuum. The exclusive lock excludes writers
    /// for the whole statement, so no maintenance can race the two steps.
    /// </summary>
    private async Task<DatabaseResult> ExecuteDropIndexAsync(SqlDropIndexPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!_catalog.TryGetIndex(plan.Table.ObjectId, plan.IndexName, out var metadata))
        {
            if (plan.IfExists)
            {
                if (statement.ScriptReceipt is { } publication)
                {
                    await SelfCommitAsync(statement, _catalog.DropIndexAsync(plan.Table.ObjectId, plan.IndexName, [], publication, cancellationToken)).ConfigureAwait(false);
                }
                return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
            }

            throw new SqlExecutionException(SqlExecutionException.UndefinedObjectCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExecutionIndexMissing, plan.IndexName, plan.Table.Schema, plan.Table.Name));
        }

        await statement.Coordinator.LockManager.AcquireAsync(
            statement.DdlLockOwner, LockResource.Object(plan.Table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        SqlCatalogTable currentTable = ReadCurrentTable(plan.Table);
        if (!_catalog.TryGetIndex(currentTable.ObjectId, plan.IndexName, out metadata))
        {
            EnsureSameIdentity(plan.Table, currentTable);
            if (plan.IfExists)
            {
                if (statement.ScriptReceipt is { } publication)
                {
                    await SelfCommitAsync(statement, _catalog.DropIndexAsync(plan.Table.ObjectId, plan.IndexName, [], publication, cancellationToken)).ConfigureAwait(false);
                }
                return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
            }

            throw new SqlExecutionException(SqlExecutionException.UndefinedObjectCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExecutionIndexMissing, plan.IndexName, currentTable.Schema, currentTable.Name));
        }

        EnsureCanChange(metadata.Owner, metadata.Name, metadata.OwningSchema, "DROP INDEX", statement);
        EnsureCanDropIndex(currentTable, metadata);
        EnsureSameIdentity(plan.Table, currentTable);

        // The canonical (creation-time) name keyed by the catalog drives the
        // manager lookup: catalog names are case-insensitive, directory names
        // are exact.
        var remaining = new List<BTreeIndexRegistration>();
        foreach (var registration in PublishedRegistrations([]))
        {
            if (!(registration.ObjectId == plan.Table.ObjectId &&
                  string.Equals(registration.Definition.Name, metadata.Name, StringComparison.Ordinal)))
            {
                remaining.Add(registration);
            }
        }

        await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.DropIndexAsync(plan.Table.ObjectId, metadata.Name, remaining, cancellationToken) : _catalog.DropIndexAsync(plan.Table.ObjectId, metadata.Name, remaining, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false);

        await RemoveDroppedIndexesAsync(statement, [metadata], cancellationToken: CancellationToken.None).ConfigureAwait(false);

        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private static void EnsureCanChange(
        DatabaseObjectOwner owner,
        string objectName,
        string? owningSchema,
        string operation,
        SqlStatementContext statement)
    {
        if (owner == DatabaseObjectOwner.Schema &&
            !string.Equals(statement.ProvisioningSchema, owningSchema, StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseObjectLockedException(objectName, owningSchema!, operation, new Diagnostic { Code = SqlExecutionException.InsufficientPrivilegeCode });
        }
    }

    private SqlCatalogTable ReadCurrentTable(SqlCatalogTable expected)
    {
        if (!_catalog.TryGetTable(expected.Schema, expected.Name, out SqlCatalogTable current))
        {
            throw new SqlExecutionException(SqlExecutionException.UndefinedTableCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableMissing, expected.Schema, expected.Name));
        }

        return current;
    }

    private static void EnsureSameIdentity(SqlCatalogTable expected, SqlCatalogTable current)
    {
        // An object lock protects a catalog identity, not a reusable name. A
        // drop/recreate during the wait must never authorize changes to the new object.
        if (expected.ObjectId != current.ObjectId)
        {
            throw new SqlExecutionException(SqlExecutionException.SerializationFailureCode,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableIdentityReplaced, expected.Schema, expected.Name));
        }
    }

    // ── Scan / seek + coercion helpers ─────────────────────────────────

    /// <summary>
    /// Enumerates a statement's candidate rows through its access path: an index
    /// seek drives a B+Tree cursor with the statement snapshot; everything else
    /// is the per-object scan. A seek whose index is not attached (torn-state
    /// defense) falls back to the scan — the residual predicate makes the
    /// fallback invisible except in cost.
    /// </summary>
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> EnumerateRows(
        SqlCatalogTable table,
        SqlAccessPath access,
        SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        if (access is SqlIndexSeekPath seek && TryResolveSeekTree(table, seek.Index, out var index))
        {
            // DML's IX object lock stabilizes these lookups against DDL's X lock.
            // SELECT takes no object lock: DROP removes catalog metadata before detaching
            // the tree. A reused name must never apply old bounds to a replacement's keys.
            // The manager and catalog directory locks are taken and released separately.
            statement.Metrics.AccessPath = $"seek:{seek.Index.Name}";
            return SeekRows(table, seek, index, statement, cancellationToken);
        }

        statement.Metrics.AccessPath = "scan";
        return Scan(table, statement, cancellationToken);
    }

    /// <summary>Never applies a planned key shape to a detached or same-name replacement tree.</summary>
    private bool TryResolveSeekTree(SqlCatalogTable table, SqlCatalogIndex planned, out BTreeIndex index)
        => (_indexManager.TryGetIndex(table.ObjectId, planned.Name, out index!) &&
            _catalog.TryGetIndex(table.ObjectId, planned.Name, out var current) && ReferenceEquals(current, planned)) ||
            _catalog.TryGetTruncatedIndex(table, planned, out index!);

    /// <summary>
    /// Drives an index seek: incrementally reads visible entries in the seek's key
    /// range through the STATEMENT snapshot (the same snapshot the equivalent
    /// scan filters through — the equivalence anchor), fetches each entry's row
    /// version, and re-checks the row's stamps against the same snapshot
    /// (defense in depth: entries mirror row stamps by the maintenance
    /// discipline, so a divergence is a bug this filter contains rather than
    /// surfaces).
    /// </summary>
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> SeekRows(
        SqlCatalogTable table,
        SqlIndexSeekPath seek,
        BTreeIndex index,
        SqlStatementContext statement,
        CancellationToken cancellationToken,
        TransactionSnapshot? snapshotOverride = null, int[]? indexOrdinals = null)
    {
        var range = BuildSeekRange(table, seek, indexOrdinals ?? _definitions.Get(table, cancellationToken).GetKey(seek.Index.ColumnNames).Ordinals);
        var snapshot = snapshotOverride ?? statement.Snapshot;
        var defaults = _definitions.Get(table, cancellationToken).MissingValues;

        // Each cursor batch copies entries under the tree's read latch and releases it
        // before a move returns. No latch is held across residual/subquery evaluation or
        // a DML row-lock wait. DML drains this cursor before taking sorted row locks,
        // then sorted unique-key locks, then the apply gate and storage/index latches.
        // The finally below also disposes on predicate failure and cancellation.
        var cursor = index.OpenCursor(snapshot, range, cancellationToken: cancellationToken);

        try
        {
            while (cursor.MoveNextAsync(cancellationToken).GetAwaiter().GetResult())
            {
                statement.Metrics.RecordsExamined++;

                var (pageId, slotIndex) = SqlRecordLocation.Unpack(cursor.CurrentEntryReference);

                // Only a row reclaimed beneath the entry is skipped; a page that fails its
                // checksum or cannot be read (#1342), or a row that does not decode (#1362),
                // fails the statement.
                if (!_storage.TryReadRecord(pageId, slotIndex, table.ObjectId, out var record))
                {
                    continue;
                }

                var values = DecodeRow(record, pageId, slotIndex, table, defaults, out var writer, out var deleter);

                if (!snapshot.IsVisible(writer))
                {
                    continue;
                }

                if (deleter != TransactionSequence.None && snapshot.IsVisible(deleter))
                {
                    continue;
                }

                yield return ((pageId, slotIndex), values);
            }
        }
        finally
        {
            cursor.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Encodes the seek's key range: the equality prefix (encoded exactly like
    /// the maintenance path encodes keys, temporal identity included), extended
    /// by the optional range bounds on the next key column. Prefix semantics
    /// ride the codec's order-preservation: every composite key starting with
    /// prefix P sorts in [P, successor(P)), where successor increments the last
    /// non-0xFF byte.
    /// </summary>
    private IndexKeyRange BuildSeekRange(SqlCatalogTable table, SqlIndexSeekPath seek, int[] indexOrdinals)
    {
        var prefixWriter = new DatabaseKeyWriter();

        for (int i = 0; i < seek.EqualityValues.Count; i++)
        {
            int ordinal = indexOrdinals[i];
            SqlRowCodec.AppendKeyValue(prefixWriter, table.Columns[ordinal].Type.Type, seek.EqualityValues[i],
                table.Columns[ordinal].Collation ?? _catalog.DefaultCollation);
        }

        byte[] prefix = prefixWriter.ToArray();

        if (seek.Lower is null && seek.Upper is null)
        {
            return new IndexKeyRange(
                new IndexKey(prefix),
                PrefixSuccessor(prefix) is { } successor ? new IndexKey(successor) : null,
                IsStartInclusive: true,
                IsEndInclusive: false);
        }

        int rangeOrdinal = indexOrdinals[seek.EqualityValues.Count];
        var rangeType = table.Columns[rangeOrdinal].Type.Type;
        var rangeCollation = table.Columns[rangeOrdinal].Collation ?? _catalog.DefaultCollation;

        IndexKey? start = seek.EqualityValues.Count > 0 ? new IndexKey(prefix) : null;
        bool startInclusive = true;
        IndexKey? end = seek.EqualityValues.Count > 0 && PrefixSuccessor(prefix) is { } prefixEnd ? new IndexKey(prefixEnd) : null;
        bool endInclusive = false;

        if (seek.Lower is { } lower)
        {
            byte[] lowerKey = AppendComponent(prefix, rangeType, lower.Value, rangeCollation);

            // Exclusive lower: skip every composite key whose range component
            // equals the bound — start at the bound's prefix successor.
            start = lower.Inclusive
                ? new IndexKey(lowerKey)
                : PrefixSuccessor(lowerKey) is { } lowerSuccessor ? new IndexKey(lowerSuccessor) : null;
            startInclusive = true;
        }

        if (seek.Upper is { } upper)
        {
            byte[] upperKey = AppendComponent(prefix, rangeType, upper.Value, rangeCollation);

            // Inclusive upper: admit every composite key whose range component
            // equals the bound — end (exclusively) at the bound's successor.
            end = upper.Inclusive
                ? PrefixSuccessor(upperKey) is { } upperSuccessor ? new IndexKey(upperSuccessor) : null
                : new IndexKey(upperKey);
            endInclusive = false;
        }

        return new IndexKeyRange(start, end, startInclusive, endInclusive);
    }

    private static byte[] AppendComponent(byte[] prefix, DatabaseType type, object? value, Collation collation)
    {
        var writer = new DatabaseKeyWriter();
        SqlRowCodec.AppendKeyValue(writer, type, value, collation);
        byte[] component = writer.ToArray();

        var combined = new byte[prefix.Length + component.Length];
        prefix.CopyTo(combined, 0);
        component.CopyTo(combined, prefix.Length);
        return combined;
    }

    /// <summary>
    /// Computes the smallest byte string greater than every string with the
    /// given prefix, or null when none exists (an all-0xFF prefix): increment
    /// the last non-0xFF byte and truncate behind it.
    /// </summary>
    private static byte[]? PrefixSuccessor(byte[] prefix)
    {
        for (int i = prefix.Length - 1; i >= 0; i--)
        {
            if (prefix[i] != 0xFF)
            {
                var successor = new byte[i + 1];
                prefix.AsSpan(0, i + 1).CopyTo(successor);
                successor[i]++;
                return successor;
            }
        }

        return null;
    }

    /// <summary>
    /// Scans the table's visible row versions through the statement's snapshot:
    /// a version is visible when its writer is admitted and its deleter (when
    /// stamped) is not — a visible tombstone reads as absence. Exactly one
    /// version of a logical row is visible per snapshot by construction: an
    /// update stamps the old version's deleter with the same sequence that wrote
    /// the new version.
    /// </summary>
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> Scan(
        SqlCatalogTable table,
        SqlStatementContext statement,
        CancellationToken cancellationToken,
        TransactionSnapshot? snapshotOverride = null)
    {
        var snapshot = snapshotOverride ?? statement.Snapshot;

        foreach (var version in ScanVersions(table, cancellationToken, statement.Metrics))
        {
            if (!snapshot.IsVisible(version.Writer))
            {
                continue;
            }

            if (version.Deleter != TransactionSequence.None && snapshot.IsVisible(version.Deleter))
            {
                continue; // a visible tombstone reads as absence
            }

            yield return (version.Location, version.Values);
        }
    }

    /// <summary>
    /// Scans every stored version of the table's rows — visible or not — with
    /// its stamps, decoded through <paramref name="table"/>. Index builds use
    /// this: an index must carry an entry for every version an older snapshot
    /// can still read, with the version's own stamps. The scan is scoped to the
    /// table's record chain (per-object pages), so its cost is O(table), not
    /// O(database). Every record the scan meets is the table's: one whose object-id
    /// prefix names another object, or that does not decode, fails the scan with
    /// <see cref="StorageCorruptionException"/> rather than being skipped (#1362).
    /// </summary>
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values, TransactionSequence Writer, TransactionSequence Deleter)> ScanVersions(
        SqlCatalogTable table,
        CancellationToken cancellationToken,
        SqlStatementMetrics? metrics = null)
    {
        var defaults = _definitions.Get(table, cancellationToken).MissingValues;
        using var iterator = _storage.GetUnitIterator(table.ObjectId);

        while (iterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var unit = iterator.Current;
            var values = DecodeRow(unit.Data, unit.PageId, unit.SlotIndex, table, defaults, out var writer, out var deleter);

            if (metrics is not null)
            {
                metrics.RecordsExamined++;
            }

            yield return ((unit.PageId, unit.SlotIndex), values, writer, deleter);
        }
    }

    /// <summary>
    /// Coerces an evaluated value to a column's storage type, enforcing nullability and declared bounds.
    /// </summary>
    internal static object? CoerceForColumn(object? value, SqlCatalogColumn column)
    {
        if (value is null)
        {
            if (!column.IsNullable)
            {
                throw SqlEvaluationException.NotNullViolation(column.Name);
            }

            return null;
        }

        try
        {
            if (value is SqlCharacter character)
            {
                value = column.Type.Type == DatabaseType.Character ? character.Value : character.Text;
            }
            object converted = column.Type.Type switch
            {
                DatabaseType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                DatabaseType.Int8 => Convert.ToSByte(value, CultureInfo.InvariantCulture),
                DatabaseType.Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
                DatabaseType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                DatabaseType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                DatabaseType.Float32 => value is string realText ? SqlStandardCasts.ParseReal(realText) : Convert.ToSingle(value, CultureInfo.InvariantCulture),
                DatabaseType.Float64 => value is string floatText ? SqlStandardCasts.ParseFloat(floatText) : Convert.ToDouble(value, CultureInfo.InvariantCulture),
                DatabaseType.Decimal => value is string numericText
                    ? SqlCastConverter.ParseNumericText(numericText, column.Type)
                    : Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                DatabaseType.String or DatabaseType.Character or DatabaseType.Json => value as string
                    ?? Convert.ToString(value, CultureInfo.InvariantCulture)!,
                DatabaseType.Binary or DatabaseType.JsonBinary => value is byte[] bytes
                    ? bytes
                    : throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnBinaryRequired, column.Name)),
                DatabaseType.Date => value switch
                {
                    DateOnly date => date,
                    string text => SqlTemporalText.ParseAssignmentDate(text),
                    DateTime dateTime => DateOnly.FromDateTime(dateTime),
                    DateTimeOffset instant => DateOnly.FromDateTime(instant.UtcDateTime),
                    _ => throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DateConversionUnsupported, value.GetType().Name)),
                },
                DatabaseType.Time => value switch
                {
                    TimeOnly time => time,
                    DateTimeOffset instant => TimeOnly.FromDateTime(instant.UtcDateTime),
                    string text => SqlTemporalText.ParseTime(text),
                    _ => throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TimeConversionUnsupported, value.GetType().Name)),
                },
                DatabaseType.DateTime => value switch
                {
                    DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified),
                    DateTimeOffset instant => DateTime.SpecifyKind(instant.UtcDateTime, DateTimeKind.Unspecified),
                    string text => SqlTemporalText.ParseAssignmentTimestamp(text),
                    _ => throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TimestampConversionUnsupported, value.GetType().Name)),
                },
                DatabaseType.DateTimeOffset => value switch
                {
                    DateTimeOffset dateTimeOffset => dateTimeOffset,
                    DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified), TimeSpan.Zero),
                    string text => SqlTemporalText.ParseAssignmentTimestampTz(text),
                    _ => throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TimestampTzConversionUnsupported, value.GetType().Name)),
                },
                DatabaseType.TimeSpan => value switch
                {
                    TimeSpan timeSpan => timeSpan,
                    string text => SqlTemporalText.ParseInterval(text),
                    _ => throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IntervalConversionUnsupported, value.GetType().Name)),
                },
                DatabaseType.Guid => value switch
                {
                    Guid guid => guid,
                    string text => Guid.Parse(text),
                    _ => throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.UuidConversionUnsupported, value.GetType().Name)),
                },
                _ => throw new SqlExecutionException(SqlExecutionException.UnsupportedCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnStorageUnsupported, column.Type.Type)),
            };
            return SqlCastConverter.ApplyModifiers(converted, column.Type);
        }
        catch (SqlEvaluationException exception)
        {
            throw exception.WithContext($"Column '{column.Name}'{(column.DomainName is null ? string.Empty : $" (domain '{column.DomainName}')")}");
        }
        catch (SqlExecutionException exception)
        {
            throw exception.WithContext(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.ColumnConversionFailed, value, column.Name, column.Type.Type));
        }
        catch (Exception exception) when (value is string
            && column.Type.Type is DatabaseType.Float32 or DatabaseType.Float64
            && exception is FormatException or OverflowException or InvalidCastException)
        {
            throw SqlEvaluationException.CastValueFailed(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.ColumnTextConversionFailed, column.Name, column.Type.Type, exception.Message), exception);
        }
        catch (OverflowException exception) when (column.Type.Type is DatabaseType.Int8 or DatabaseType.Int16
            or DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Decimal)
        {
            // ISO store assignment: a value the numeric column cannot hold is a numeric
            // value out of range (SQLSTATE 22003), the same fault as an overflowing result.
            throw SqlEvaluationException.NumericValueOutOfRange(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExecutorCoerceForColumnSqlEvaluation, Convert.ToString(value, CultureInfo.InvariantCulture), column.Name, column.Type.Type), exception);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException or ArgumentOutOfRangeException)
        {
            throw new SqlExecutionException(SqlExecutionException.ConversionCode(exception),
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnConversionFailed, value, column.Name, column.Type.Type), exception);
        }
    }


    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor value)
        {
            _ = value;
        }
    }
}
