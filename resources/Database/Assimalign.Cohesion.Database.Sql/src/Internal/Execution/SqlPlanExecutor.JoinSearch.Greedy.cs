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
        // Normalized candidates fail the certificate unconditionally. Preserve the
        // written tree (including MERGE's source/target normalization) without search.
        if (baseline.Tree is not null)
        {
            return new SqlHeuristicJoinPlan(baseline, 0, 0, 0, 0, 0, 0, 0,
                Order(baseline), Order(baseline, true) != Order(plan, true), sourceOnly, false);
        }
        var oldSteps = new SqlJoinStep?[count];
        var boundOrder = new int[count];
        int position = 0;
        if (baseline.FirstBinding >= start) { boundOrder[position++] = baseline.FirstBinding - start; }
        for (int i = 0; i < baseline.Steps.Count; i++)
        {
            var step = baseline.Steps[i];
            int binding = i == 0 && baseline.FirstBinding == 1 ? 0 : step.Binding;
            if (binding < start) { continue; }
            oldSteps[binding - start] = step;
            boundOrder[position++] = binding - start;
        }
        // An ON conjunct takes effect after its written step. WHERE takes effect
        // only after the full product, even when all its references appeared earlier.
        var placement = new int[terms.Count];
        Array.Fill(placement, count);
        var termPositions = new Dictionary<SqlBoundExpression, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < terms.Count; i++) { termPositions.Add(terms[i].Expression, i); }
        for (int i = 1; i < count; i++)
        {
            foreach (var term in Conjuncts(oldSteps[boundOrder[i]]?.Condition))
            {
                if (termPositions.TryGetValue(term, out int index)) { placement[index] = Math.Min(placement[index], i); }
            }
        }
        var logSelectivity = new double[terms.Count];
        for (int i = 0; i < terms.Count; i++)
        {
            var term = terms[i];
            logSelectivity[i] = Math.Log(term.Expression is SqlBoundBinary { Operator: SqlBinaryOperator.Equal, Left: SqlBoundColumn a, Right: SqlBoundColumn b }
                ? 1 / Math.Max(1, Math.Max(baseRows[columnBindings[a.Ordinal]], baseRows[columnBindings[b.Ordinal]])) : 1d / 3);
        }

        var included = new bool[count];
        var bound = CostOrder(boundOrder, placement);
        // Identity pushdown retains order, paths, activation prefixes and every input
        // buffer. It is admissible even when it ties the real bound plan's work.
        var identity = CostOrder(boundOrder, null);
        var candidate = Candidate(boundOrder, identityOrder: true);
        bool accepted = Safe(candidate, identity.Cost, identity.Memory);
        var selected = accepted ? candidate : baseline;
        double chosenCost = accepted ? identity.Cost : bound.Cost;
        double chosenMemory = accepted ? identity.Memory : bound.Memory;
        // The certificate pins these flat orders. Pushdown can still improve them;
        // no other order can be admitted, so do not search it on every execution.
        (int[] Order, long Pairs, int States, double Cost, double Memory) result =
            sourceOnly || retainedFrames || oldSteps.Any(step => step?.Hash is not null)
            ? (boundOrder, 0, 2 * count - 1, identity.Cost, identity.Memory)
            : SqlGreedyJoinSearch.Find(baseRows, Evaluate, cancellationToken);
        int comparison = SqlJoinSearch.CompareCost(result.Cost, chosenCost);
        if (comparison < 0 || comparison == 0 && (result.Memory < chosenMemory ||
            result.Memory == chosenMemory && Earlier(result.Order, boundOrder)))
        {
            candidate = Candidate(result.Order);
            if (Safe(candidate, result.Cost, result.Memory))
            { selected = candidate; chosenCost = result.Cost; chosenMemory = result.Memory; accepted = true; }
        }
        // SQ-Z1 normalizes this historical reversed-first-pair binding on searched records.
        if (!accepted && selected.FirstBinding == 1)
        { selected = selected with { Steps = selected.Steps.Select((step, i) => i == 0 ? step with { Binding = 0 } : step).ToArray() }; }
        bool reordered = selected.FirstBinding != 0 ||
            !selected.Steps.Select(step => step.Binding).SequenceEqual(Enumerable.Range(1, selected.Bindings.Count - 1));
        return new SqlHeuristicJoinPlan(selected, result.States, result.Pairs, result.Pairs + 2L * (count - 1),
            bound.Cost, chosenCost, bound.Memory, chosenMemory, Order(selected), reordered, sourceOnly, accepted);

        bool Safe(SqlJoinPlan proposed, double cost, double memory)
            => SqlJoinSearch.CompareCost(cost, bound.Cost) <= 0 && memory <= bound.Memory &&
                PreservesRetention(baseline, proposed, sourceOnly || retainedFrames);

        static bool Earlier(int[] left, int[] right)
        {
            for (int i = 0; i < left.Length; i++) { if (left[i] != right[i]) { return left[i] < right[i]; } }
            return false;
        }

        (double Cost, double Memory) CostOrder(int[] order, int[]? at)
        {
            Array.Clear(included);
            included[order[0]] = true;
            double cost = baseRows[order[0]];
            double memory = sourceOnly ? Retained(order[0], oldSteps[order[0]]?.Access, oldSteps[order[0]]?.Hash) : 0;
            for (int i = 1; i < count; i++)
            {
                int inner = order[i];
                // Both candidates and the real bound order use exactly this physical
                // work function and the kernel's interleaved summation order.
                var step = oldSteps[inner];
                cost = SqlGreedyJoinSearch.AddCost(cost, baseRows[inner],
                    Work(Rows(included, at, i - 1), baseRows[inner], step?.Access, step?.Hash));
                memory += Retained(inner, step?.Access, step?.Hash);
                included[inner] = true;
            }
            return (cost, memory);
        }

        SqlJoinPlan Candidate(int[] order, bool identityOrder = false)
        {
            Array.Clear(included);
            var steps = new SqlJoinStep[count - 1];
            included[order[0]] = true;
            for (int i = 1; i < count; i++)
            {
                int inner = order[i];
                var crossing = new List<SqlBoundExpression>();
                foreach (var term in terms)
                { if (Crossing(term.Bindings, included, inner)) { crossing.Add(term.Expression); } }
                var condition = Combine(crossing);
                SqlJoinIndexPath? access;
                SqlHashJoinPath? hash;
                if (identityOrder) { access = oldSteps[inner]?.Access; hash = oldSteps[inner]?.Hash; }
                else { Paths(included, inner, out access, out hash); }
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
            var proposed = plan with { Inputs = filtered, InputEstimates = estimates, Tree = null, Steps = steps,
                FirstBinding = order[0] + start, Where = null };
            if (sourceOnly)
            {
                proposed = proposed with { FirstBinding = 0, Where = Combine(targetTerms), Steps =
                    new[] { new SqlJoinStep(order[0] + start, SqlJoinType.Cross, null, null) }.Concat(steps).ToArray() };
            }
            return proposed;
        }

        (double Work, double Memory, bool Connected) Evaluate(bool[] component, int inner)
        {
            Paths(component, inner, out var access, out var hash);
            double work = Work(Rows(component), baseRows[inner], access, hash);
            bool connected = false;
            foreach (var term in terms) { if (Crossing(term.Bindings, component, inner)) { connected = true; break; } }
            return (work, Retained(inner, access, hash), connected);
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
                for (int i = 0; i < ordinals.Count; i++)
                { int binding = columnBindings[ordinals[i]]; if (binding < 0 || !component[binding]) { return false; } }
                return true;
            }
        }

        double Rows(bool[] component, int[]? at = null, int depth = 0)
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
                if (terms[i].Bindings.Length <= 1 || at is not null && at[i] > depth) { continue; }
                bool contained = true;
                foreach (int binding in terms[i].Bindings) { if (!component[binding]) { contained = false; break; } }
                if (contained) { value += logSelectivity[i]; }
            }
            return value >= Math.Log(1e100) ? 1e100 : Math.Exp(value);
        }

        double Retained(int inner, SqlJoinIndexPath? access, SqlHashJoinPath? hash)
        {
            int width = 24 + 40 * plan.Bindings[inner + start].Source.Columns.Count;
            return hash is not null ? JoinBuildBytes(baseRows[inner], width) :
                access is null ? JoinBufferBytes(baseRows[inner], width) : 0;
        }

        static double Work(double left, double right, SqlJoinIndexPath? access, SqlHashJoinPath? hash)
            => hash is not null ? 4 * (left + right) : access is not null
                ? left * (1 + Math.Log2(right + 1)) : left * right;

        string Order(SqlJoinPlan chosen, bool identities = false)
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
            string Name(int binding) => identities ? binding.ToString(System.Globalization.CultureInfo.InvariantCulture) :
                chosen.Bindings[binding].Reference.Alias ?? chosen.Bindings[binding].Source.Name;
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
