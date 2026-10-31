using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Called only after SearchJoinOrder's common legality proof. Component membership
    // uses arrays indexed by binding ordinal; no exact-search masks escape into here.
    private SqlJoinPlan SearchLargeJoinOrder(SqlJoinPlan plan, bool sourceOnly,
        List<(int[] Bindings, SqlBoundExpression Expression)> terms, List<SqlBoundExpression> targetTerms,
        Func<SqlCatalogTable, SqlAccessPath, SqlAccessPath> choose,
        Func<SqlCatalogTable, SqlIndexSeekPath?, BTreeRangeCount?> probe,
        Func<SqlJoinInput[], BTreeRangeCount?[], SqlJoinPlan> costBound, bool retainedFrames, CancellationToken cancellationToken)
    {
        int start = sourceOnly ? 1 : 0, count = plan.Bindings.Count - start;
        var inputs = plan.Inputs.Select((input, i) => input with { Access = choose(plan.Bindings[i].Source.Table!, input.Access) }).ToArray();
        var estimates = new BTreeRangeCount?[plan.Bindings.Count];
        var baseRows = new double[count];
        var logRows = new double[count];
        var columnBindings = new int[plan.Columns.Count];
        for (int binding = 0; binding < plan.Bindings.Count; binding++)
        {
            var relation = plan.Bindings[binding];
            for (int column = 0; column < relation.Source.Columns.Count; column++) { columnBindings[relation.Offset + column] = binding - start; }
            if (binding < start) { continue; }
            estimates[binding] = inputs[binding].Access switch
            {
                SqlIndexSeekPath { Cost.Estimate: { } observed } => observed,
                SqlScanPath { Cost.Estimate: { } observed } => observed,
                SqlIndexSeekPath seek when seek.Index.IsUnique && seek.EqualityValues.Count == seek.Index.ColumnNames.Count => null,
                _ => probe(relation.Source.Table!, inputs[binding].Access as SqlIndexSeekPath),
            };
            baseRows[binding - start] = inputs[binding].Access is SqlIndexSeekPath unique && unique.Index.IsUnique &&
                unique.EqualityValues.Count == unique.Index.ColumnNames.Count ? 1 : Estimate(estimates[binding]);
            logRows[binding - start] = Math.Log(baseRows[binding - start]);
        }
        var baseline = costBound(inputs, estimates);
        var oldSteps = new SqlJoinStep?[count];
        for (int i = 0; i < baseline.Steps.Count; i++)
        {
            var step = baseline.Steps[i];
            int binding = i == 0 && baseline.Tree is null && baseline.FirstBinding == 1 ? 0 : step.Binding;
            if (binding >= start) { oldSteps[binding - start] = step; }
        }
        var logSelectivity = new double[terms.Count];
        for (int i = 0; i < terms.Count; i++)
        {
            var term = terms[i];
            logSelectivity[i] = Math.Log(term.Expression is SqlBoundBinary { Operator: SqlBinaryOperator.Equal, Left: SqlBoundColumn a, Right: SqlBoundColumn b }
                ? 1 / Math.Max(1, Math.Max(baseRows[columnBindings[a.Ordinal]], baseRows[columnBindings[b.Ordinal]])) : 1d / 3);
        }

        var result = SqlGreedyJoinSearch.Find(baseRows, Evaluate, cancellationToken);
        var included = new bool[count];
        double boundCost = baseRows.Sum(), boundMemory = 0;
        if (baseline.Tree is { } normalized)
        {
            if (sourceOnly) { normalized = normalized.Left!.Binding == 0 ? normalized.Right! : normalized.Left!; }
            var bound = BoundTree(normalized);
            boundCost = bound.Cost; boundMemory = bound.Peak;
        }
        else
        {
            var boundOrder = new[] { baseline.FirstBinding }.Concat(baseline.Steps.Select((step, i) => i == 0 && baseline.FirstBinding == 1 ? 0 : step.Binding))
                .Where(i => i >= start).Select(i => i - start).ToArray();
            included[boundOrder[0]] = true;
            for (int i = 1; i < count; i++)
            {
                int inner = boundOrder[i];
                var step = oldSteps[inner];
                boundCost += BoundWork(step, Rows(included), baseRows[inner], baseRows[inner]);
                if (step?.Hash is not null) { boundMemory += BuildBytes(inner); }
                included[inner] = true;
            }
        }
        Array.Clear(included);
        var steps = new SqlJoinStep[count - 1];
        included[result.Order[0]] = true;
        for (int i = 1; i < count; i++)
        {
            int inner = result.Order[i];
            var condition = Combine(terms.Where(term => Crossing(term.Bindings, included, inner)).Select(term => term.Expression).ToArray());
            Paths(included, inner, out var access, out var hash);
            _ = SearchJoinWork(Rows(included), baseRows[inner], baseRows[inner], ref access, ref hash);
            steps[i - 1] = new(inner + start, condition is null ? SqlJoinType.Cross : SqlJoinType.Inner, condition, access) { Hash = hash };
            included[inner] = true;
        }
        var filtered = inputs.ToArray();
        for (int i = 0; i < count; i++)
        {
            var local = Conjuncts(inputs[i + start].Filter).Concat(terms.Where(term => term.Bindings.Length == 1 && term.Bindings[0] == i)
                .Select(term => term.Expression)).Distinct<SqlBoundExpression>(ReferenceEqualityComparer.Instance).ToArray();
            if (local.Length != 0) { filtered[i + start] = filtered[i + start] with { Filter = Combine(local) }; }
        }
        var candidate = plan with { Inputs = filtered, InputEstimates = estimates, Tree = null, Steps = steps,
            FirstBinding = result.Order[0] + start, Where = null };
        if (sourceOnly)
        {
            candidate = candidate with { FirstBinding = 0, Where = Combine(targetTerms), Steps =
                new[] { new SqlJoinStep(result.Order[0] + start, SqlJoinType.Cross, null, null) }.Concat(steps).ToArray() };
        }
        // The search prunes new physical builds/probes; the same SQ-Z1 certificate
        // still owns admission (including target buffers and hash activation prefixes).
        bool accepted = result.Cost < boundCost && result.Memory <= boundMemory &&
            PreservesRetention(baseline, candidate, sourceOnly || retainedFrames);
        var selected = accepted ? candidate : baseline;
        // SQ-Z1 normalizes this historical reversed-first-pair binding on searched records.
        if (!accepted && selected.Tree is null && selected.FirstBinding == 1)
        { selected = selected with { Steps = selected.Steps.Select((step, i) => i == 0 ? step with { Binding = 0 } : step).ToArray() }; }
        string order = Order(selected);
        bool reordered = selected.Tree is { } selectedTree ? !selectedTree.Leaves().SequenceEqual(Enumerable.Range(0, selected.Bindings.Count))
            : selected.FirstBinding != 0 || !selected.Steps.Select(step => step.Binding).SequenceEqual(Enumerable.Range(1, selected.Bindings.Count - 1));
        return new SqlHeuristicJoinPlan(selected, result.States, result.Pairs, result.Pairs + 2L * (count - 1),
            boundCost, result.Cost, boundMemory, result.Memory, order, reordered, sourceOnly, accepted);

        (double Work, double Memory, bool Connected) Evaluate(bool[] component, int inner)
        {
            Paths(component, inner, out var access, out var hash);
            double work = SearchJoinWork(Rows(component), baseRows[inner], baseRows[inner], ref access, ref hash);
            bool connected = false;
            foreach (var term in terms) { if (Crossing(term.Bindings, component, inner)) { connected = true; break; } }
            return (work, hash is null ? 0 : BuildBytes(inner), connected);
        }

        void Paths(bool[] component, int inner, out SqlJoinIndexPath? access, out SqlHashJoinPath? hash)
        {
            var old = oldSteps[inner];
            access = old?.Access;
            if (access is not null && !Available(access.OuterOrdinals)) { access = null; }
            hash = old?.Hash;
            if (hash is not null && (hash.OuterExpressions is not null || hash.InnerExpressions is not null || !Available(hash.OuterOrdinals))) { hash = null; }

            bool Available(IReadOnlyList<int> ordinals)
            {
                foreach (int ordinal in ordinals)
                { int binding = columnBindings[ordinal]; if (binding < 0 || !component[binding]) { return false; } }
                return true;
            }
        }

        double Rows(bool[] component)
        {
            // Log products implement the same set cardinality/selectivity model without
            // overflow/NaN for large cliques or an empty input following huge products.
            double value = 0;
            for (int i = 0; i < count; i++)
            {
                if (!component[i]) { continue; }
                if (baseRows[i] == 0) { return 0; }
                value += logRows[i];
            }
            for (int i = 0; i < terms.Count; i++)
            {
                if (terms[i].Bindings.Length <= 1) { continue; }
                bool contained = true;
                foreach (int binding in terms[i].Bindings) { if (!component[binding]) { contained = false; break; } }
                if (contained) { value += logSelectivity[i]; }
            }
            return value >= Math.Log(1e100) ? 1e100 : Math.Exp(value);
        }

        double BuildBytes(int inner)
            => Build(baseRows[inner], 24 + 40 * plan.Bindings[inner + start].Source.Columns.Count);

        static double Build(double rows, double width)
        {
            double capacity = rows <= 0 ? 0 : Math.Max(16, Math.Pow(2, Math.Ceiling(Math.Log2(rows))));
            return 104 + rows * width + (capacity == 0 ? 0 : 96 + 30 * capacity);
        }

        static double BoundWork(SqlJoinStep? step, double left, double right, double baseInner)
            => step?.Hash is not null ? 4 * (left + right) : step?.Access is not null
                ? left * (1 + Math.Log2(baseInner + 1)) : left * right;

        (double Cost, double Rows, double Peak, double Output, int Width) BoundTree(SqlJoinTree node)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node.Step is null)
            {
                double rows = baseRows[node.Binding - start];
                int width = plan.Bindings[node.Binding].Source.Columns.Count;
                double output = rows * (24 + 8 * plan.Columns.Count + 32 * width);
                return (rows, rows, output, output, width);
            }
            var left = BoundTree(node.Left!);
            var right = BoundTree(node.Right!);
            Array.Clear(included);
            Mark(node);
            double resultRows = Rows(included);
            int resultWidth = left.Width + right.Width;
            double resultOutput = resultRows * (24 + 8 * plan.Columns.Count + 32 * resultWidth);
            double build = node.Step.Hash is null ? right.Output : Build(right.Rows, 24 + 8 * plan.Columns.Count + 32 * right.Width);
            double peak = Math.Max(left.Peak, Math.Max(left.Output + right.Peak + build, left.Output + build + resultOutput));
            return (left.Cost + right.Cost + BoundWork(node.Step, left.Rows, right.Rows, right.Rows), resultRows, peak, resultOutput, resultWidth);

            void Mark(SqlJoinTree current)
            {
                System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
                if (current.Step is null) { included[current.Binding - start] = true; }
                else { Mark(current.Left!); Mark(current.Right!); }
            }
        }

        string Order(SqlJoinPlan chosen)
        {
            var text = new StringBuilder();
            if (chosen.Tree is { } tree) { Append(tree); }
            else
            {
                text.Append('(', chosen.Steps.Count);
                text.Append(Name(chosen.FirstBinding));
                foreach (var step in chosen.Steps) { text.Append(',').Append(Name(step.Binding)).Append(')'); }
            }
            return text.ToString();
            void Append(SqlJoinTree node)
            {
                System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
                if (node.Step is null) { text.Append(Name(node.Binding)); return; }
                text.Append('('); Append(node.Left!); text.Append(','); Append(node.Right!); text.Append(')');
            }
            string Name(int binding) => chosen.Bindings[binding].Reference.Alias ?? chosen.Bindings[binding].Source.Name;
        }
    }

    private static bool Crossing(int[] bindings, bool[] component, int inner)
    {
        if (bindings.Length <= 1) { return false; }
        bool hasInner = false, hasOuter = false;
        foreach (int binding in bindings)
        {
            if (binding == inner) { hasInner = true; }
            else if (component[binding]) { hasOuter = true; }
            else { return false; }
        }
        return hasInner && hasOuter;
    }
}
