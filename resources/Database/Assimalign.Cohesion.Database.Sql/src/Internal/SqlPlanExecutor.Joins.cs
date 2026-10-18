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
    /// Executes a correlated index join or a buffered nested loop. Both inputs
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
            EnsureCurrentDefinition(table);
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
            plan.IsDistinct, evaluator, plan.OrderByProjections);
    }

    /// <summary>
    /// Produces candidates in logical column order regardless of loop direction.
    /// At each step without a usable index, the inner input is read once, costing O(L + R)
    /// stored reads and O(L * R) predicate evaluations, with O(R) buffered rows.
    /// </summary>
    private async IAsyncEnumerable<object?[]> EnumerateJoinRowsAsync(SqlJoinPlan plan, SqlStatementContext statement,
        bool[] sharedColumns, [EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyDictionary<SqlDerivedRelation, List<object?[]>>? derivedRows)
    {
        // Every earlier step is retained by a later loop. Only the final index inner
        // is a fresh read for each delivered pair. Delivery detaches selected binary cells only.
        Array.Fill(sharedColumns, true);
        if (plan.Steps.Count == 0)
        {
            foreach (var row in ReadRelation(plan.Bindings[0].Source, statement, cancellationToken, derivedRows)) { yield return row; }
            yield break;
        }
        var accesses = new SqlJoinIndexPath?[plan.Steps.Count];
        var indexes = new BTreeIndex?[plan.Steps.Count];
        var paths = new string[plan.Steps.Count];
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var access = plan.Steps[i].Access;
            if (access is not null && _indexManager.TryGetIndex(plan.Bindings[access.InnerBinding].Source.Table!.ObjectId,
                access.Index.Name, out var index))
            {
                accesses[i] = access;
                indexes[i] = index;
            }
            paths[i] = accesses[i] is { } chosen ? $"join-seek:{chosen.Index.Name}" : "join-scan";
        }
        statement.Metrics.JoinAccessPaths = paths;
        if (accesses[^1] is { } last)
        {
            var fresh = plan.Bindings[last.InnerBinding];
            Array.Fill(sharedColumns, false, fresh.Offset, fresh.Source.Columns.Count);
        }
        bool reversed = accesses[0]?.InnerBinding == 0;
        var initial = plan.Bindings[reversed ? 1 : 0];
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var buffered = new List<object?[]>?[plan.Steps.Count];
        var cursors = new IEnumerator<object?[]>?[plan.Steps.Count];
        var matched = new bool[plan.Steps.Count];
        var extended = new bool[plan.Steps.Count];
        var rows = new object?[plan.Steps.Count + 1][];
        try
        {
            foreach (var first in ReadRelation(initial.Source, statement, cancellationToken, derivedRows))
            {
                rows[0] = new object?[plan.Columns.Count];
                first.CopyTo(rows[0], initial.Offset);
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
                    var inner = plan.Bindings[level == 0 && reversed ? 0 : step.Binding];
                    var outer = rows[level];
                    if (cursors[level] is null)
                    {
                        IEnumerable<object?[]> candidates;
                        if (accesses[level] is not { } access)
                        {
                            buffered[level] ??= ReadRelation(inner.Source, statement, cancellationToken, derivedRows).ToList();
                            candidates = buffered[level]!;
                        }
                        else
                        {
                            var values = JoinProbeValues(access, outer);
                            candidates = values is null ? [] : SeekRows(inner.Source.Table!,
                                new SqlIndexSeekPath(access.Index, values, null, null), indexes[level]!, statement, cancellationToken)
                                .Select(row => row.Values);
                        }
                        cursors[level] = candidates.GetEnumerator();
                        matched[level] = false;
                    }
                    if (cursors[level]!.MoveNext())
                    {
                        var joined = (object?[])outer.Clone();
                        cursors[level]!.Current.CopyTo(joined, inner.Offset);
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
            _ => throw new DatabaseException("Internal invariant: unknown SQL relation source."),
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
