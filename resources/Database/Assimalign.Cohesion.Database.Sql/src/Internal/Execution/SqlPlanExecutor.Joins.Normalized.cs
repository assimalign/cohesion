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
    /// <summary>Executes swapped RIGHT children with LEFT's ON and null-extension rules.</summary>
    private async IAsyncEnumerable<object?[]> EnumerateNormalizedJoinRowsAsync(SqlJoinPlan plan,
        SqlStatementContext statement, Func<int, IEnumerable<object?[]>> readInput,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var indexes = new Dictionary<SqlJoinStep, BTreeIndex>(ReferenceEqualityComparer.Instance);
        var paths = new string[plan.Steps.Count];
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step.Access is { } access && TryResolveSeekTree(plan.Bindings[access.InnerBinding].Source.Table!, access.Index, out var tree))
            {
                indexes.Add(step, tree);
            }
            paths[i] = step.Hash is not null ? "join-hash" : indexes.ContainsKey(step) ? $"join-seek:{step.Access!.Index.Name}" : "join-scan";
        }
        statement.Metrics.JoinAccessPaths = paths;
        // Postorder work avoids recursive MoveNext/async calls for arbitrarily long join chains.
        // Each child result is released after its parent consumes it.
        var pending = new Stack<(SqlJoinTree Node, int Phase)>();
        var results = new Dictionary<SqlJoinTree, (List<object?[]> Rows, int Start, int End)>(ReferenceEqualityComparer.Instance);
        pending.Push((plan.Tree!, 0));
        while (pending.TryPop(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = next.Node;
            if (node.Step is not { } step)
            {
                var binding = plan.Bindings[node.Binding];
                var rows = new List<object?[]>();
                foreach (var input in readInput(node.Binding))
                {
                    if (ReferenceEquals(node, plan.Tree) && statement.Metrics.Analysis(plan) is { } work) { work.Candidates++; }
                    var frame = new object?[plan.Columns.Count];
                    input.CopyTo(frame, binding.Offset);
                    rows.Add(frame);
                }
                results.Add(node, (rows, binding.Offset, binding.Offset + binding.Source.Columns.Count));
                continue;
            }
            bool seek = indexes.TryGetValue(step, out var index);
            if (next.Phase == 0)
            {
                pending.Push((node, 1));
                pending.Push((node.Left!, 0));
                continue;
            }
            var outer = results[node.Left!];
            if (next.Phase == 1)
            {
                if (outer.Rows.Count == 0)
                {
                    var leaves = node.Leaves().ToArray();
                    var last = plan.Bindings[leaves.Max()];
                    results.Remove(node.Left!);
                    results.Add(node, ([], plan.Bindings[leaves.Min()].Offset, last.Offset + last.Source.Columns.Count));
                    continue;
                }
                if (!seek)
                {
                    pending.Push((node, 2));
                    pending.Push((node.Right!, 0));
                    continue;
                }
            }
            (List<object?[]> Rows, int Start, int End) inner = seek ? (new List<object?[]>(), plan.Bindings[node.Right!.Binding].Offset,
                plan.Bindings[node.Right.Binding].Offset + plan.Bindings[node.Right.Binding].Source.Columns.Count) : results[node.Right!];
            var output = new List<object?[]>();
            SqlHashJoinTable? hash = null;
            try
            {
                if (step.Hash is { } keys)
                {
                    hash = new SqlHashJoinTable(keys, statement.Metrics, _definitions.StatementMemoryBudget);
                    foreach (var row in inner.Rows) { hash.Add(row, cancellationToken); }
                }
                foreach (var left in outer.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IEnumerable<object?[]> candidates = inner.Rows;
                    if (hash is not null) { candidates = hash.Probe(left, cancellationToken); }
                    else if (seek)
                    {
                        var access = step.Access!;
                        var values = JoinProbeValues(access, left);
                        candidates = values is null ? [] : SeekRows(plan.Bindings[access.InnerBinding].Source.Table!,
                            new SqlIndexSeekPath(access.Index, values, null, null), index!, statement, cancellationToken)
                            .Select(row => row.Values);
                    }
                    bool matched = false;
                    foreach (var right in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        statement.Metrics.RecordJoinCandidate();
                        if (statement.Metrics.Analysis(step) is { } work) { work.Candidates++; }
                        var joined = (object?[])left.Clone();
                        right.AsSpan(seek ? 0 : inner.Start, inner.End - inner.Start).CopyTo(joined.AsSpan(inner.Start));
                        await PrepareSubqueriesAsync(step.Condition, joined, statement, cancellationToken).ConfigureAwait(false);
                        if (!evaluator.Matches(step.Condition, joined)) { continue; }
                        matched = true;
                        output.Add(joined);
                    }
                    if (!matched && step.Kind is SqlJoinType.LeftOuter or SqlJoinType.RightOuter) { output.Add(left); }
                }
            }
            finally { hash?.Dispose(); }
            results.Remove(node.Left!);
            results.Remove(node.Right!);
            results.Add(node, (output, Math.Min(outer.Start, inner.Start), Math.Max(outer.End, inner.End)));
        }
        foreach (var row in results[plan.Tree!].Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }
}
