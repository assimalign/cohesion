using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async IAsyncEnumerable<object?[]> ReadRecursiveJoinAsync(SqlJoinPlan plan, SqlSubqueryValues? frame,
        SqlStatementContext statement, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var table in plan.Bindings.Select(b => b.Source.Table).OfType<SqlCatalogTable>()
            .DistinctBy(t => t.ObjectId).OrderBy(t => t.ObjectId))
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.Transaction.Sequence,
                LockResource.Object(table.ObjectId), LockMode.IntentShared, cancellationToken).ConfigureAwait(false);
            if (_isDml || !_catalog.IsTruncatedTable(table)) { EnsureCurrentDefinition(table); }
        }
        if (plan.Bindings.Any(b => b.Source.View is not null)) { _ = statement.RequireCatalogSnapshot(); }
        var root = plan.Tree;
        if (root is null)
        {
            root = new SqlJoinTree(plan.FirstBinding);
            for (int index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                int inner = index == 0 && plan.FirstBinding == 1 ? 0 : step.Binding;
                root = new SqlJoinTree(-1, root, new SqlJoinTree(inner), step);
            }
        }
        statement.Metrics.JoinAccessPaths = plan.Steps.Select(step => step.Access is { } access
            ? "join-seek:" + access.Index.Name : "join-scan").ToArray();
        await foreach (var row in ReadNode(root).ConfigureAwait(false)) { yield return row; }

        async IAsyncEnumerable<object?[]> ReadNode(SqlJoinTree node, object?[]? outer = null, SqlJoinIndexPath? access = null)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Step is not { } step)
            {
                await foreach (var row in ReadInput(node.Binding, outer, access).ConfigureAwait(false)) { yield return row; }
                yield break;
            }
            // FULL needs the right side's match bitmap. Preserved-side LEFT/RIGHT
            // and INNER inputs retain their pull behavior, including shared CTEs.
            using var rightRows = step.Kind == SqlJoinType.FullOuter ? new RecursiveRows(RecursiveState!.Memory) : null;
            bool[]? matchedRight = null;
            long bitmapBytes = 0;
            if (rightRows is not null)
            {
                await foreach (var row in ReadNode(node.Right!).ConfigureAwait(false)) { rightRows.Add(row); }
                bitmapBytes = 24L + rightRows.Count;
                RecursiveState!.Memory.Reserve(bitmapBytes);
                matchedRight = new bool[rightRows.Count];
            }
            try
            {
                await foreach (var left in ReadNode(node.Left!).ConfigureAwait(false))
                {
                    RecursiveState!.Memory.Retain(left);
                    try
                    {
                        bool matched = false;
                        int rightIndex = 0;
                        await foreach (var right in Candidates(left).ConfigureAwait(false))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            statement.Metrics.RecordJoinCandidate();
                            if (statement.Metrics.Analysis(step) is { } work) { work.Candidates++; }
                            var joined = (object?[])left.Clone();
                            foreach (int binding in node.Right!.Leaves())
                            {
                                var input = plan.Bindings[binding];
                                Array.Copy(right, input.Offset, joined, input.Offset, input.Source.Columns.Count);
                            }
                            await PrepareRecursiveExpressionAsync(step.Condition, joined, frame, statement, cancellationToken).ConfigureAwait(false);
                            if (ExecutionEvaluator(frame, cancellationToken).Matches(step.Condition, joined))
                            {
                                matched = true;
                                if (matchedRight is not null) { matchedRight[rightIndex] = true; }
                                yield return joined;
                            }
                            rightIndex++;
                        }
                        if (!matched && step.Kind is SqlJoinType.LeftOuter or SqlJoinType.RightOuter or SqlJoinType.FullOuter)
                        { yield return left; }
                    }
                    finally { RecursiveState!.Memory.ReleaseRow(left); }
                }
                if (matchedRight is not null)
                {
                    for (int index = 0; index < matchedRight.Length; index++)
                    { cancellationToken.ThrowIfCancellationRequested(); if (!matchedRight[index]) { yield return rightRows![index]; } }
                }
            }
            finally { RecursiveState!.Memory.Release(bitmapBytes); }

            async IAsyncEnumerable<object?[]> Candidates(object?[] left)
            {
                if (rightRows is not null)
                { for (int index = 0; index < rightRows.Count; index++) { yield return rightRows[index]; } }
                else
                {
                    await foreach (var row in ReadNode(node.Right!, left, step.Access).ConfigureAwait(false)) { yield return row; }
                }
            }
        }

        async IAsyncEnumerable<object?[]> ReadInput(int binding, object?[]? outer, SqlJoinIndexPath? access)
        {
            var input = plan.Bindings[binding];
            var source = input.Source;
            var filter = plan.Inputs.Count == 0 ? null : plan.Inputs[binding].Filter;
            await foreach (var row in InputRows().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (statement.Metrics.Analysis(input) is { } work) { work.Candidates++; }
                var joined = new object?[plan.Columns.Count];
                row.CopyTo(joined, input.Offset);
                if (!ExecutionEvaluator(null, cancellationToken).Matches(filter, joined)) { continue; }
                yield return joined;
            }

            async IAsyncEnumerable<object?[]> InputRows()
            {
                if (source.Derived is { } derived)
                {
                    await foreach (var row in ReadRecursiveRelationAsync(derived, frame, statement, cancellationToken).ConfigureAwait(false))
                    { yield return row; }
                    yield break;
                }
                IEnumerable<object?[]> rows;
                if (source.Table is not { } table) { rows = ReadSystemRelation(source.View!, statement, cancellationToken); }
                else if (access is not null && outer is not null && TryResolveSeekTree(table, access.Index, out var index))
                {
                    var values = JoinProbeValues(access, outer);
                    rows = values is null ? [] : SeekRows(table, new SqlIndexSeekPath(access.Index, values, null, null),
                        index, statement, cancellationToken).Select(row => row.Values);
                }
                else
                {
                    rows = plan.Inputs.Count == 0 ? Scan(table, statement, cancellationToken).Select(row => row.Values)
                        : EnumerateRows(table, plan.Inputs[binding].Access, statement, cancellationToken).Select(row => row.Values);
                }
                foreach (var row in rows) { cancellationToken.ThrowIfCancellationRequested(); yield return row; }
            }
        }
    }
}
