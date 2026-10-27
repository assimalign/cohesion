using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    /// <summary>
    /// Executes a correlated index join, bounded hash build or buffered nested loop. All inputs
    /// use the same captured statement snapshot, including every index cursor.
    /// Table intent locks protect definitions; MVCC readers need no FK row locks.
    /// </summary>
    private async Task<DatabaseResult> ExecuteJoinAsync(SqlJoinPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        Dictionary<SqlDerivedRelation, List<object?[]>>? derivedRows = null;
        foreach (var binding in plan.Bindings)
        {
            if (binding.Source.Derived is not { } derived) { continue; }
            (derivedRows ??= new(ReferenceEqualityComparer.Instance))[derived] =
                await MaterializeRelationAsync(derived, statement, cancellationToken).ConfigureAwait(false);
        }
        foreach (var table in plan.Bindings.Select(binding => binding.Source.Table).OfType<SqlCatalogTable>().DistinctBy(table => table.ObjectId).OrderBy(table => table.ObjectId))
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.Transaction.Sequence,
                LockResource.Object(table.ObjectId), LockMode.IntentShared, cancellationToken).ConfigureAwait(false);
            // Only read-only statements may use an immutable retired TRUNCATE owner.
            // INSERT sources and DML subqueries inherit the enclosing statement's check.
            if (_isDml || !_catalog.IsTruncatedTable(table)) { EnsureCurrentDefinition(table); }
        }
        // Require binding's image even when an empty outer input would skip virtual enumeration.
        if (plan.Bindings.Any(binding => binding.Source.View is not null))
        {
            _ = statement.RequireCatalogSnapshot();
        }

        var sharedColumns = new bool[plan.Columns.Count];
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken, sharedColumns);
        var matches = new List<object?[]>();
        await foreach (var row in EnumerateJoinRowsAsync(plan, statement, sharedColumns, cancellationToken, derivedRows).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PrepareSubqueriesAsync(plan.Where, row, statement, cancellationToken).ConfigureAwait(false);
            if (evaluator.Matches(plan.Where, row))
            {
                matches.Add(row);
            }
        }

        if (_subqueryValues is { HasCorrelatedQueries: true })
        {
            return await MaterializeCorrelatedSelectAsync(matches, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset,
                plan.IsDistinct, evaluator, statement, cancellationToken).ConfigureAwait(false);
        }
        return MaterializeSelect(matches, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset,
            plan.IsDistinct, evaluator, plan.OrderByProjections, cancellationToken);
    }

    /// <summary>
    /// Produces candidates in logical column order regardless of loop direction.
    /// An unindexed step without a safe hash key reads its inner once and uses a nested
    /// loop. Safe hash steps retain budgeted rows and probe only matching hash buckets.
    /// </summary>
    private async IAsyncEnumerable<object?[]> EnumerateJoinRowsAsync(SqlJoinPlan plan, SqlStatementContext statement,
        bool[] sharedColumns, [EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyDictionary<SqlDerivedRelation, List<object?[]>>? derivedRows,
        (int Binding, IReadOnlyList<object?[]> Rows)? dmlTarget = null)
    {
        // Every earlier step is retained by a later loop. Only the final index inner
        // is a fresh read for each delivered pair. Delivery detaches selected binary cells only.
        Array.Fill(sharedColumns, true);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var inputEvaluator = ExecutionEvaluator(null, cancellationToken);
        var inputIndexes = new BTreeIndex?[plan.Bindings.Count];
        for (int i = 0; i < plan.Inputs.Count; i++)
        {
            if (plan.Inputs[i].Access is SqlIndexSeekPath seek &&
                TryResolveSeekTree(plan.Bindings[i].Source.Table!, seek.Index, out var tree)) { inputIndexes[i] = tree; }
        }
        if (plan.Tree is not null)
        {
            // All tree inputs may be reused by a parent; delivery detaches selected cells.
            await foreach (var row in EnumerateNormalizedJoinRowsAsync(plan, statement, ReadInput, cancellationToken, dmlTarget?.Binding).ConfigureAwait(false))
            {
                yield return row;
            }
            yield break;
        }
        if (plan.Steps.Count == 0)
        {
            var work = statement.Metrics.Analysis(plan);
            foreach (var row in ReadInput(0))
            {
                if (work is not null) { work.Candidates++; }
                yield return row;
            }
            yield break;
        }
        var accesses = new SqlJoinIndexPath?[plan.Steps.Count];
        var indexes = new BTreeIndex?[plan.Steps.Count];
        var paths = new string[plan.Steps.Count];
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var access = plan.Steps[i].Access;
            if (access is not null && TryResolveSeekTree(plan.Bindings[access.InnerBinding].Source.Table!, access.Index, out var index))
            {
                accesses[i] = access;
                indexes[i] = index;
            }
            paths[i] = plan.Steps[i].Hash is not null ? "join-hash"
                : accesses[i] is { } chosen ? $"join-seek:{chosen.Index.Name}" : "join-scan";
        }
        statement.Metrics.JoinAccessPaths = paths;
        if (accesses[^1] is { } last)
        {
            var fresh = plan.Bindings[last.InnerBinding];
            Array.Fill(sharedColumns, false, fresh.Offset, fresh.Source.Columns.Count);
        }
        bool reversed = plan.FirstBinding == 1;
        var initial = plan.Bindings[reversed ? 1 : 0];
        var buffered = new List<object?[]>?[plan.Steps.Count];
        var hashes = new SqlHashJoinTable?[plan.Steps.Count];
        var cursors = new IEnumerator<object?[]>?[plan.Steps.Count];
        var matched = new bool[plan.Steps.Count];
        var extended = new bool[plan.Steps.Count];
        var rows = new object?[plan.Steps.Count + 1][];
        try
        {
            foreach (var first in ReadInput(reversed ? 1 : 0))
            {
                rows[0] = new object?[plan.Columns.Count + (dmlTarget is null ? 0 : 1)];
                first.CopyTo(rows[0], initial.Offset);
                if (dmlTarget?.Binding == (reversed ? 1 : 0)) { rows[0][^1] = first; }
                // An explicit cursor stack keeps N inputs from becoming N recursive
                // MoveNext calls. Query text controls N independently of expression depth.
                int level = 0;
                while (level >= 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (level == plan.Steps.Count)
                    {
                        yield return rows[level];
                        level--;
                        continue;
                    }
                    if (extended[level])
                    {
                        extended[level] = false;
                        level--;
                        continue;
                    }
                    var step = plan.Steps[level];
                    int innerBinding = level == 0 && reversed ? 0 : step.Binding;
                    var inner = plan.Bindings[innerBinding];
                    var outer = rows[level];
                    if (cursors[level] is null)
                    {
                        IEnumerable<object?[]> candidates;
                        if (step.Hash is { } hashKeys)
                        {
                            if (hashes[level] is null)
                            {
                                // Publish before reading: cancellation, decode and budget failures all
                                // release every reservation in the enclosing finally.
                                var table = new SqlHashJoinTable(hashKeys, statement.Metrics, _definitions.StatementMemoryBudget);
                                hashes[level] = table;
                                foreach (var item in ReadInput(innerBinding)) { table.Add(item, cancellationToken); }
                            }
                            candidates = hashes[level]!.Probe(outer, cancellationToken);
                        }
                        else if (accesses[level] is not { } access)
                        {
                            buffered[level] ??= ReadInput(innerBinding).ToList();
                            candidates = buffered[level]!;
                        }
                        else
                        {
                            var values = JoinProbeValues(access, outer);
                            candidates = values is null ? [] : FilterInput(SeekRows(inner.Source.Table!,
                                new SqlIndexSeekPath(access.Index, values, null, null), indexes[level]!, statement, cancellationToken)
                                .Select(row => row.Values), innerBinding);
                        }
                        cursors[level] = candidates.GetEnumerator();
                        matched[level] = false;
                    }
                    if (cursors[level]!.MoveNext())
                    {
                        statement.Metrics.RecordJoinCandidate();
                        if (statement.Metrics.Analysis(step) is { } work) { work.Candidates++; }
                        var joined = (object?[])outer.Clone();
                        cursors[level]!.Current.CopyTo(joined, inner.Offset);
                        if (dmlTarget?.Binding == innerBinding) { joined[^1] = cursors[level]!.Current; }
                        await PrepareSubqueriesAsync(step.Condition, joined, statement, cancellationToken).ConfigureAwait(false);
                        if (evaluator.Matches(step.Condition, joined))
                        {
                            matched[level] = true;
                            rows[++level] = joined;
                        }
                    }
                    else
                    {
                        cursors[level]!.Dispose();
                        cursors[level] = null;
                        if (!matched[level] && step.Kind == SqlJoinType.LeftOuter)
                        {
                            extended[level] = true;
                            rows[++level] = outer;
                        }
                        else { level--; }
                    }
                }
            }
        }
        finally
        {
            foreach (var cursor in cursors) { cursor?.Dispose(); }
            foreach (var hash in hashes) { hash?.Dispose(); }
        }

        IEnumerable<object?[]> ReadInput(int binding)
        {
            // Joined DML already applied this binding's pushed filter before retaining identities.
            if (dmlTarget?.Binding == binding) { return dmlTarget.Value.Rows; }
            var source = plan.Bindings[binding].Source;
            var rows = inputIndexes[binding] is { } tree && plan.Inputs[binding].Access is SqlIndexSeekPath seek
                ? SeekRows(source.Table!, seek, tree, statement, cancellationToken).Select(row => row.Values)
                : ReadRelation(source, statement, cancellationToken, derivedRows);
            return FilterInput(rows, binding);
        }

        IEnumerable<object?[]> FilterInput(IEnumerable<object?[]> candidates, int binding)
        {
            var filter = plan.Inputs.Count == 0 ? null : plan.Inputs[binding].Filter;
            // One scratch frame per enumeration, never one joined-row clone per rejected pair.
            // Filters retain the original bound ordinals, including merged USING expressions.
            var frame = filter is null ? null : new object?[plan.Columns.Count];
            var work = statement.Metrics.Analysis(plan.Bindings[binding]);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (work is not null) { work.Candidates++; }
                if (frame is not null)
                {
                    candidate.CopyTo(frame, plan.Bindings[binding].Offset);
                    if (!inputEvaluator.Matches(filter, frame)) { continue; }
                }
                yield return candidate;
            }
        }
    }

    /// <summary>Reads a bound source without giving virtual rows an object lock or index path.</summary>
    private IEnumerable<object?[]> ReadRelation(SqlRelationSource source, SqlStatementContext statement, CancellationToken cancellationToken,
        IReadOnlyDictionary<SqlDerivedRelation, List<object?[]>>? derivedRows)
        => source switch
        {
            { Table: { } table } => Scan(table, statement, cancellationToken).Select(row => row.Values),
            { View: { } view } => ReadSystemRelation(view, statement, cancellationToken),
            { Derived: { } derived } => derivedRows![derived],
            _ => throw new SqlExecutionException(SqlExecutionException.InternalErrorCode, ErrorMessages.RelationSourceUnknown),
        };

    /// <summary>
    /// Converts exact numeric keys to the indexed storage type. A NULL, rounded
    /// or out-of-range value cannot satisfy the mandatory equality and has no
    /// matches; all other comparisons still run against the original joined row.
    /// </summary>
    private static object?[]? JoinProbeValues(SqlJoinIndexPath access, object?[] outer)
    {
        var values = new object?[access.OuterOrdinals.Count];
        for (int i = 0; i < values.Length; i++)
        {
            object? original = outer[access.OuterOrdinals[i]];
            if (original is null)
            {
                return null;
            }

            var column = access.InnerColumns[i];
            // The planner only joins character keys to character keys with the index's
            // collation. A comparison is not an assignment: a longer string can compare equal
            // under that collation (for example an ignored combining accent), so probe it whole.
            if (original is string or SqlCharacter)
            {
                values[i] = original;
                continue;
            }
            try
            {
                values[i] = CoerceForColumn(original, column);
            }
            catch (DatabaseException)
            {
                return null;
            }

            if (SqlExpressionEvaluator.Compare(original, values[i]!) != 0)
            {
                return null;
            }
        }
        return values;
    }
}
