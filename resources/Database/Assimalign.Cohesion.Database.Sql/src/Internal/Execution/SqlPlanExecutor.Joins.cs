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
    private async Task<DatabaseResult> ExecuteJoinAsync(SqlJoinPlan plan, SqlStatementContext statement, CancellationToken cancellationToken, SqlDatabaseSession? spillOwner = null, SqlGroupPlan? hashInput = null)
    {
        SqlSelectMemory matchBytes = default;
        long derivedBytes = 0;
        List<object?[]>? matches = null;
        try
        {
            Dictionary<SqlDerivedRelation, List<object?[]>>? derivedRows = null;
            foreach (var binding in plan.Bindings)
            {
                if (binding.Source.Derived is not { } derived) { continue; }
                var materialized = await MaterializeRelationAsync(derived, statement, cancellationToken).ConfigureAwait(false);
                if (derived.IsCorrelated) { derivedBytes += AdmitSelectRows(materialized, cancellationToken, includeWindow: true); }
                (derivedRows ??= new(ReferenceEqualityComparer.Instance))[derived] = materialized;
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
            var sourceOwner = spillOwner is not null && HasSequenceProjection(plan.Projections) && !OrderingUsesSequence(plan.Projections, plan.OrderByProjections?.Values)
                ? null : spillOwner;
            matches = new List<object?[]>();
            await foreach (var row in EnumerateJoinRowsAsync(plan, statement, sharedColumns, cancellationToken, derivedRows, spillOwner: spillOwner).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PrepareSubqueriesAsync(plan.Where, row, statement, cancellationToken).ConfigureAwait(false);
                if (evaluator.Matches(plan.Where, row))
                {
                    if (statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget) is { } memory)
                    {
                        memory.Retain(row);
                        memory.Reserve(32);
                    }
                    if (RetainStagedSortSource(matches, row, ref matchBytes, plan.OrderBy,
                        sourceOwner, cancellationToken, hashInput: hashInput)) { matches.Add(row); }
                }
            }

            _definitions.ObserveSelectRetention?.Invoke("join-matches", false, matches.Count);
            if (SqlStagedSortSource.For(matches) is { } spooled)
            {
                if (hashInput is not null)
                {
                    spooled.Seal(cancellationToken);
                    var input = new HashGroupInput(((DatabaseResultSet)MaterializeProjected(plan.Projections, [])).Columns,
                        spooled, plan.Projections, evaluator);
                    SqlStagedSortSource.Detach(matches); return input;
                }
                return MaterializeStagedSort(spooled, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset, plan.IsDistinct,
                    evaluator, plan.OrderByProjections, cancellationToken,
                    plan.OrderByProjections is not { Count: > 0 } && plan.Projections.Any(p => ContainsVolatileCall(p.Value)));
            }
            if (_subqueryValues is { HasCorrelatedQueries: true })
            {
                return await MaterializeCorrelatedSelectAsync(matches, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset,
                    plan.IsDistinct, evaluator, statement, cancellationToken).ConfigureAwait(false);
            }
            return MaterializeSelect(matches, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset,
                plan.IsDistinct, evaluator, plan.OrderByProjections, cancellationToken, spillOwner: spillOwner, hashInput: hashInput);
        }
        catch (Exception exception) { if (matches is not null) { SqlStagedSortSource.ReleaseAfterFailure(matches, exception); } throw; }
        finally { if (matches is not null) { SqlStagedSortSource.Release(matches); } ReleaseSelectMemory(ref matchBytes); ReleaseSelectMemory(ref derivedBytes); }
    }

    private async ValueTask<DatabaseResult> ExecutePipelinedJoinAsync(SqlJoinPlan plan,
        SqlStatementContext statement, CancellationToken cancellationToken, int? retainRows = null, SqlDatabaseSession? spillOwner = null, SqlGroupPlan? hashInput = null)
    {
        var (derivedRows, derivedBytes) = await PrepareJoinInputsAsync(plan, statement, cancellationToken).ConfigureAwait(false);
        try
        {
            var sharedColumns = new bool[plan.Columns.Count];
            var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken, sharedColumns);
            return await ProjectJoinRowsAsync(plan, statement, sharedColumns, derivedRows, evaluator, cancellationToken, retainRows, spillOwner, hashInput).ConfigureAwait(false);
        }
        finally { ReleaseSelectMemory(ref derivedBytes); }
    }

    // Shared with group input: retain derived materialization, lock, definition and snapshot order.
    private async ValueTask<(Dictionary<SqlDerivedRelation, List<object?[]>>? Rows, long Bytes)> PrepareJoinInputsAsync(SqlJoinPlan plan,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        long derivedBytes = 0;
        try
        {
            Dictionary<SqlDerivedRelation, List<object?[]>>? derivedRows = null;
            foreach (var binding in plan.Bindings)
            {
                if (binding.Source.Derived is not { } derived) { continue; }
                var materialized = await MaterializeRelationAsync(derived, statement, cancellationToken).ConfigureAwait(false);
                if (derived.IsCorrelated) { derivedBytes += AdmitSelectRows(materialized, cancellationToken, includeWindow: true); }
                (derivedRows ??= new(ReferenceEqualityComparer.Instance))[derived] = materialized;
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

            return (derivedRows, derivedBytes);
        }
        catch { ReleaseSelectMemory(ref derivedBytes); throw; }
    }

    /// <summary>
    /// Produces candidates in logical column order regardless of loop direction.
    /// An unindexed step without a safe hash key reads its inner once and uses a nested
    /// loop. Safe hash steps retain budgeted rows and probe only matching hash buckets.
    /// </summary>
    private async IAsyncEnumerable<object?[]> EnumerateJoinRowsAsync(SqlJoinPlan plan, SqlStatementContext statement,
        bool[] sharedColumns, [EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyDictionary<SqlDerivedRelation, List<object?[]>>? derivedRows,
        (int Binding, IReadOnlyList<object?[]> Rows)? dmlTarget = null, bool streamRoot = false, SqlDatabaseSession? spillOwner = null)
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
            await foreach (var row in EnumerateNormalizedJoinRowsAsync(plan, statement, ReadInput, cancellationToken, dmlTarget?.Binding,
                (streamRoot ? SqlJoinRowMode.StreamRoot : SqlJoinRowMode.None) | (spillOwner is not null ? SqlJoinRowMode.AllowSpill : SqlJoinRowMode.None)).ConfigureAwait(false))
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
        bool reversed = plan is not SqlSearchedJoinPlan && plan.FirstBinding == 1;
        var buffered = new (List<object?[]>? Rows, SqlSelectMemory Memory)[plan.Steps.Count];
        var hashes = new SqlHashJoinTable?[plan.Steps.Count];
        var cursors = new IEnumerator<object?[]>?[plan.Steps.Count];
        var matched = new bool[plan.Steps.Count];
        var extended = new bool[plan.Steps.Count];
        var rows = new object?[plan.Steps.Count + 1][];
        try
        {
            foreach (var first in ReadInput(plan.FirstBinding))
            {
                rows[0] = new object?[plan.Columns.Count + (dmlTarget is null ? 0 : 1)];
                first.CopyTo(rows[0], plan.Bindings[plan.FirstBinding].Offset);
                if (dmlTarget?.Binding == plan.FirstBinding) { rows[0][^1] = first; }
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
                    int innerBinding = level == 0 && reversed ? 0 : plan.Steps[level].Binding;
                    var inner = plan.Bindings[innerBinding];
                    var outer = rows[level];
                    if (cursors[level] is null)
                    {
                        IEnumerable<object?[]> candidates;
                        if (plan.Steps[level].Hash is { } hashKeys)
                        {
                            if (hashes[level] is null)
                            {
                                if (statement.Metrics.IsAnalyzing)
                                { statement.Metrics.RecordHashSpillPolicy(plan.Steps[level], _definitions.StatementMemoryBudget >= 262144 && CanSpillJoinRows(plan, spillOwner)); }
                                // Publish before reading: cancellation, decode and budget failures all
                                // release every reservation in the enclosing finally.
                                var table = new SqlHashJoinTable(hashKeys, statement.Metrics, _definitions.StatementMemoryBudget);
                                hashes[level] = table;
                                foreach (var item in ReadInput(innerBinding))
                                {
                                    try { table.Add(item, cancellationToken); }
                                    catch (SqlExecutionException error) when (CanSpillJoinBuild(plan, hashKeys, spillOwner, error))
                                    { table.Spill(spillOwner!, item, cancellationToken, error); }
                                }
                                if (statement.Metrics.IsAnalyzing)
                                { statement.Metrics.RecordHashSpillStatus(plan.Steps[level], table.IsSpilled); }
                            }
                            candidates = hashes[level]!.Probe(outer, cancellationToken);
                        }
                        else if (accesses[level] is not { } access)
                        {
                            if (buffered[level].Rows is null)
                            {
                                var buffer = new List<object?[]>();
                                foreach (var item in ReadInput(innerBinding))
                                {
                                    if (statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget) is { } memory)
                                    {
                                        memory.Retain(item);
                                        memory.Reserve(32); // list storage, including growth
                                    }
                                    RetainSelectRow(ref buffered[level].Memory, item, includeWindow: true);
                                    buffer.Add(item);
                                }
                                buffered[level].Rows = buffer;
                                SqlRowCollector.ObserveBuffer?.Invoke("join-inner", buffer);
                            }
                            candidates = buffered[level].Rows!;
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
                        if (statement.Metrics.Analysis(plan.Steps[level]) is { } work) { work.Candidates++; }
                        var joined = (object?[])outer.Clone();
                        cursors[level]!.Current.CopyTo(joined, inner.Offset);
                        if (dmlTarget?.Binding == innerBinding) { joined[^1] = cursors[level]!.Current; }
                        await PrepareSubqueriesAsync(plan.Steps[level].Condition, joined, statement, cancellationToken).ConfigureAwait(false);
                        if (evaluator.Matches(plan.Steps[level].Condition, joined))
                        {
                            matched[level] = true;
                            rows[++level] = joined;
                        }
                    }
                    else
                    {
                        cursors[level]!.Dispose();
                        cursors[level] = null;
                        if (!matched[level] && plan.Steps[level].Kind == SqlJoinType.LeftOuter)
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
            for (int index = 0; index < buffered.Length; index++) { ReleaseSelectMemory(ref buffered[index].Memory); }
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
