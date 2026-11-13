using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Optional deterministic test observation; no counters or callback state per statement.
    internal static Action<string, SqlStatementMetrics, CancellationToken>? FullJoinProgress { get; set; }

    /// <summary>Executes normalized joins, including FULL's independently tracked build rows.</summary>
    private async IAsyncEnumerable<object?[]> EnumerateNormalizedJoinRowsAsync(SqlJoinPlan plan,
        SqlStatementContext statement, Func<int, IEnumerable<object?[]>> readInput,
        [EnumeratorCancellation] CancellationToken cancellationToken, int? dmlTarget = null, SqlJoinRowMode mode = SqlJoinRowMode.None)
    {
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var indexes = new Dictionary<SqlJoinStep, BTreeIndex>(ReferenceEqualityComparer.Instance);
        var paths = new string[plan.Steps.Count];
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step.Access is { } access && access.InnerBinding != dmlTarget && TryResolveSeekTree(plan.Bindings[access.InnerBinding].Source.Table!, access.Index, out var tree))
            {
                indexes.Add(step, tree);
            }
            paths[i] = step.Hash is not null ? "join-hash" : indexes.ContainsKey(step) ? $"join-seek:{step.Access!.Index.Name}" : "join-scan";
        }
        statement.Metrics.JoinAccessPaths = paths;
        // Postorder work avoids recursive MoveNext/async calls for arbitrarily long join chains.
        // Each child result is released after its parent consumes it.
        var pending = new Stack<(SqlJoinTree Node, int Phase)>();
        var results = new Dictionary<SqlJoinTree, (SelectJoinBuffer Rows, int Start, int End)>(ReferenceEqualityComparer.Instance);
        var builds = new Dictionary<SqlJoinTree, SqlHashJoinTable>(ReferenceEqualityComparer.Instance);
        var sinks = new Dictionary<SqlJoinTree, SqlHashJoinTable>(ReferenceEqualityComparer.Instance);
        try
        {
            pending.Push((plan.Tree!, 0));
            while (pending.TryPop(out var next))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = next.Node;
                if (node.Step is not { } step)
                {
                    var binding = plan.Bindings[node.Binding];
                    var rows = new SelectJoinBuffer();
                    results.Add(node, (rows, binding.Offset, binding.Offset + binding.Source.Columns.Count));
                    foreach (var input in readInput(node.Binding))
                    {
                        if (ReferenceEquals(node, plan.Tree) && statement.Metrics.Analysis(plan) is { } work) { work.Candidates++; }
                        var frame = new object?[plan.Columns.Count + (dmlTarget is null ? 0 : 2)];
                        input.CopyTo(frame, binding.Offset);
                        if (node.Binding == dmlTarget) { frame[^2] = input; }
                        if ((mode & SqlJoinRowMode.StreamRoot) != 0 && ReferenceEquals(node, plan.Tree)) { yield return frame; }
                        else { Retain(node, frame, rows, dmlTarget); }
                    }
                    continue;
                }
                bool seek = indexes.TryGetValue(step, out var index);
                if (next.Phase == 0)
                {
                    if (statement.Metrics.IsAnalyzing && step.Hash is not null)
                    { statement.Metrics.RecordHashSpillPolicy(step, _definitions.StatementMemoryBudget >= 262144 && (mode & SqlJoinRowMode.AllowSpill) != 0 && CanSpillJoinRows(plan, statement.Session)); }
                    pending.Push((node, 1));
                    pending.Push((node.Left!, 0));
                    continue;
                }
                var outer = results[node.Left!];
                if (next.Phase == 1)
                {
                    if (outer.Rows.Count == 0 && SqlStagedSortSource.For(outer.Rows) is null && step.Kind != SqlJoinType.FullOuter)
                    {
                        var leaves = node.Leaves().ToArray();
                        var last = plan.Bindings[leaves.Max()];
                        results.Remove(node.Left!);
                        results.Add(node, ([], plan.Bindings[leaves.Min()].Offset, last.Offset + last.Source.Columns.Count));
                        continue;
                    }
                    if (step.Hash is { } keys)
                    {
                        // Reserve while producing the inner subtree, before retaining its output.
                        // Nested builds share the same live grant and unwind together on failure.
                        var build = new SqlHashJoinTable(keys, statement.Metrics, _definitions.StatementMemoryBudget,
                            trackUnmatched: step.Kind == SqlJoinType.FullOuter, evaluator: evaluator);
                        builds.Add(node, build);
                        sinks.Add(node.Right!, build);
                    }
                    if (!seek)
                    {
                        pending.Push((node, 2));
                        pending.Push((node.Right!, 0));
                        continue;
                    }
                }
                (SelectJoinBuffer Rows, int Start, int End) inner = seek ? (new SelectJoinBuffer(), plan.Bindings[node.Right!.Binding].Offset,
                    plan.Bindings[node.Right.Binding].Offset + plan.Bindings[node.Right.Binding].Source.Columns.Count) : results[node.Right!];
                var output = new SelectJoinBuffer();
                results.Add(node, (output, Math.Min(outer.Start, inner.Start), Math.Max(outer.End, inner.End)));
                SqlHashJoinTable? hash = null;
                try
                {
                    builds.Remove(node, out hash);
                    sinks.Remove(node.Right!);
                    if (statement.Metrics.IsAnalyzing && hash is not null)
                    { statement.Metrics.RecordHashSpillStatus(step, hash.IsSpilled); }
                    foreach (var left in ReadRows(outer.Rows))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        bool matched = false;
                        foreach (var candidate in Candidates(left))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            statement.Metrics.RecordJoinCandidate();
                            if (statement.Metrics.Analysis(step) is { } work) { work.Candidates++; }
                            var joined = (object?[])left.Clone();
                            var right = candidate.Row;
                            if (!seek && plan is SqlSearchedJoinPlan)
                            {
                                // DP subtrees need not occupy a contiguous range of written slots.
                                foreach (int leaf in node.Right!.Leaves())
                                {
                                    var binding = plan.Bindings[leaf];
                                    right.AsSpan(binding.Offset, binding.Source.Columns.Count).CopyTo(joined.AsSpan(binding.Offset));
                                }
                            }
                            else { right.AsSpan(seek ? 0 : inner.Start, inner.End - inner.Start).CopyTo(joined.AsSpan(inner.Start)); }
                            // A seek bypasses ReadInput. Apply its local restriction once in
                            // the full ordinal frame, just as the flat seek reader does.
                            if (seek && plan is SqlSearchedJoinPlan &&
                                !evaluator.Matches(plan.Inputs[step.Access!.InnerBinding].Filter, joined)) { continue; }
                            if (dmlTarget is not null && !seek)
                            {
                                joined[^2] ??= right[^2];
                                joined[^1] ??= right[^1];
                            }
                            if (dmlTarget is not null && seek && ReferenceEquals(node, plan.Tree)) { joined[^1] = true; }
                            await PrepareSubqueriesAsync(step.Condition, joined, statement, cancellationToken).ConfigureAwait(false);
                            if (!evaluator.Matches(step.Condition, joined)) { continue; }
                            matched = true;
                            if (step.Kind == SqlJoinType.FullOuter)
                            {
                                hash!.MarkMatched(candidate.Entry);
                                FullJoinProgress?.Invoke("match", statement.Metrics, cancellationToken);
                            }
                            if ((mode & SqlJoinRowMode.StreamRoot) != 0 && ReferenceEquals(node, plan.Tree)) { yield return joined; }
                            else { Retain(node, joined, output, dmlTarget); }
                        }
                        if (!matched && step.Kind is SqlJoinType.LeftOuter or SqlJoinType.RightOuter or SqlJoinType.FullOuter)
                        {
                            if ((mode & SqlJoinRowMode.StreamRoot) != 0 && ReferenceEquals(node, plan.Tree)) { yield return left; }
                            else { Retain(node, left, output, dmlTarget); }
                        }
                    }
                    if (step.Kind == SqlJoinType.FullOuter)
                    {
                        // Probe input is exhausted. Release its rows/readers before the
                        // unmatched ordinal merge claims the remaining shared grant/quota.
                        if (SqlHashSpillMetrics.For(statement.Metrics)?.JoinStatement is not null)
                        {
                            ReleaseSelectRows(ref outer.Rows.Memory, outer.Rows);
                            ReleaseSpool(outer.Rows); outer.Rows.Clear();
                        }
                        foreach (var row in hash!.Unmatched(cancellationToken))
                        {
                            FullJoinProgress?.Invoke("unmatched", statement.Metrics, cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if ((mode & SqlJoinRowMode.StreamRoot) != 0 && ReferenceEquals(node, plan.Tree)) { yield return row; }
                            else { Retain(node, row, output, dmlTarget); }
                        }
                    }

                    IEnumerable<(object?[] Row, int Entry)> Candidates(object?[] left)
                    {
                        if (hash is not null)
                        {
                            foreach (int entry in hash.ProbeEntries(left, cancellationToken)) { yield return (hash.RowAt(entry), entry); }
                            yield break;
                        }
                        IEnumerable<object?[]> candidates = inner.Rows;
                        if (seek)
                        {
                            var access = step.Access!;
                            var values = JoinProbeValues(access, left);
                            candidates = values is null ? [] : SeekRows(plan.Bindings[access.InnerBinding].Source.Table!,
                                new SqlIndexSeekPath(access.Index, values, null, null), index!, statement, cancellationToken)
                                .Select(row => row.Values);
                        }
                        foreach (var row in candidates) { yield return (row, -1); }
                    }
                }
                finally
                {
                    hash?.Dispose();
                    ReleaseSelectRows(ref outer.Rows.Memory, outer.Rows);
                    // The iterator can hoist these lists beyond their dictionary entries.
                    // Drop consumed cells as soon as their charge is released.
                    ReleaseSpool(outer.Rows);
                    outer.Rows.Clear();
                    if (!seek) { ReleaseSelectRows(ref inner.Rows.Memory, inner.Rows); }
                    ReleaseSpool(inner.Rows);
                    inner.Rows.Clear();
                }
                results.Remove(node.Left!);
                results.Remove(node.Right!);
            }
            foreach (var row in ReadRows(results[plan.Tree!].Rows))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
        }
        finally
        {
            foreach (var build in builds.Values) { build.Dispose(); }
            foreach (var result in results.Values) { ReleaseSpool(result.Rows); ReleaseSelectMemory(ref result.Rows.Memory); }
        }

        void ReleaseSpool(SelectJoinBuffer buffer)
        {
            try { SqlStagedSortSource.Release(buffer); }
            catch (SqlExecutionException cleanup) when (cleanup.Diagnostics[0].Code == "CK05005")
            { SqlHashSpillMetrics.JoinCleanup(statement.Metrics, cleanup); }
        }
        SelectJoinRows ReadRows(SelectJoinBuffer buffer)
        {
            if (SqlStagedSortSource.For(buffer) is not { } source) { return new(buffer); }
            source.Seal(cancellationToken); return new(source.Read(cancellationToken).GetEnumerator());
        }

        void Retain(SqlJoinTree node, object?[] row, SelectJoinBuffer buffer, int? target)
        {
            // Presence is independent of user NULLs, including an all-NULL derived source.
            // The target's original array survives both FULL build output and join clones.
            if (target is not null && ReferenceEquals(node,
                plan.Tree!.Left!.Binding == target ? plan.Tree.Right : plan.Tree.Left)) { row[^1] = true; }
            if (sinks.TryGetValue(node, out var sink))
            {
                try { sink.Add(row, cancellationToken); }
                catch (SqlExecutionException error) when ((mode & SqlJoinRowMode.AllowSpill) != 0 && CanSpillJoinRows(plan, statement.Session, error))
                { sink.Spill(statement.Session!, row, cancellationToken, error); }
                FullJoinProgress?.Invoke("build", statement.Metrics, cancellationToken);
            }
            else
            {
                if (statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget) is { } memory)
                {
                    memory.Retain(row);
                    memory.Reserve(32); // list storage, including growth
                }
                if (buffer.Count == 0 && SqlStagedSortSource.For(buffer) is { } source) { source.Append(row, cancellationToken); return; }
                try { RetainSelectRow(ref buffer.Memory, row, includeWindow: true); buffer.Add(row); }
                catch (SqlExecutionException error) when ((mode & SqlJoinRowMode.AllowSpill) != 0 && CanSpillJoinRows(plan, statement.Session, error))
                {
                    _ = SqlHashSpillMetrics.JoinTemp(statement.Metrics, statement.Session!);
                    SqlStagedSortSource.Add(buffer, row, ref buffer.Memory, statement.Metrics, _definitions.StatementMemoryBudget, statement.Session!, cancellationToken);
                }
            }
        }
    }

    [Flags]
    private enum SqlJoinRowMode : byte { None = 0, StreamRoot = 1, AllowSpill = 2 }

    private sealed class SelectJoinBuffer : List<object?[]>
    {
        internal SqlSelectMemory Memory;
    }

    // The ordinary foreach had a 24-byte List.Enumerator. Keep two reference slots
    // and two scalar slots, with no boxed IEnumerable/Enumerator on fitting buffers.
    // Buffers are private and never mutated until their consumer has finished.
    private readonly struct SelectJoinRows
    {
        private readonly object _source;
        internal SelectJoinRows(object source) { _source = source; }
        public Enumerator GetEnumerator() => new(_source);
        internal struct Enumerator : IDisposable
        {
            private readonly object _source;
            private int _index;
            private readonly bool _spilled;
            private object?[] _current;
            internal Enumerator(object source)
            { _source = source; _index = 0; _spilled = source is not SelectJoinBuffer; _current = null!; }
            public object?[] Current => _current;
            public bool MoveNext()
            {
                if (_spilled)
                {
                    var reader = (IEnumerator<object?[]>)_source;
                    if (!reader.MoveNext()) { _current = null!; return false; }
                    _current = reader.Current; return true;
                }
                var rows = (SelectJoinBuffer)_source;
                if (_index >= rows.Count) { _current = null!; return false; }
                _current = rows[_index++]; return true;
            }
            public void Dispose() { if (_spilled) { ((IDisposable)_source).Dispose(); } }
        }
    }
}
