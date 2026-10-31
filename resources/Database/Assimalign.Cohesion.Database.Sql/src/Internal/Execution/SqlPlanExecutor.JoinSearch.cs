using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private SqlJoinPlan? SearchJoinOrder(SqlJoinPlan plan, bool sourceOnly,
        Func<SqlCatalogTable, SqlAccessPath, SqlAccessPath> choose,
        Func<SqlCatalogTable, SqlIndexSeekPath?, BTreeRangeCount?> probe,
        Func<SqlJoinInput[], BTreeRangeCount?[], SqlJoinPlan> costBound, bool retainedFrames, CancellationToken cancellationToken)
    {
        int start = sourceOnly ? 1 : 0;
        int count = plan.Bindings.Count - start;
        if (count < (sourceOnly ? 2 : 3) || plan.Bindings.Any(binding => binding.Source.Table is null)) { return null; }
        // Conservative subset of PostgreSQL optimizer/README:199-285 and
        // joinrels.c:355-676. No reassociation across ANY outer/semi/anti boundary.
        // Derived sources (including LATERAL), subquery slots and unsafe expressions
        // are barriers as well: volatility is insufficient to prove error/count safety.
        SqlJoinTree? boundary = sourceOnly ? plan.Tree : null;
        SqlJoinTree? sourceTree = null;
        if (boundary is not null)
        {
            // MERGE associations have no WHERE (BindDmlSources/PlanMerge). Moving it
            // below the preserved boundary, or dropping target terms, is not legal.
            if (plan.Where is not null) { throw new System.Diagnostics.UnreachableException(); }
            if (boundary.Left is { Binding: 0, Step: null }) { sourceTree = boundary.Right; }
            else if (boundary.Right is { Binding: 0, Step: null }) { sourceTree = boundary.Left; }
            else { return null; }
        }
        var sourceSteps = sourceTree is null ? plan.Steps : TreeSteps(sourceTree).ToArray();
        if (sourceSteps.Any(step => step.Kind is not (SqlJoinType.Inner or SqlJoinType.Cross))) { return null; }
        var terms = new List<(int Mask, SqlBoundExpression Expression)>();
        var largeTerms = count > SqlJoinSearch.ExactRelationLimit ? new List<(int[] Bindings, SqlBoundExpression Expression)>() : null;
        var targetTerms = new List<SqlBoundExpression>();
        var seen = new HashSet<SqlBoundExpression>(ReferenceEqualityComparer.Instance);
        foreach (var expression in sourceSteps.Select(step => step.Condition).Append(plan.Where))
        {
            foreach (var term in Conjuncts(expression))
            {
                if (!seen.Add(term)) { continue; }
                var columns = new HashSet<int>();
                if (!SqlPlanner.JoinRestrictionColumns(term, columns, plan.Columns)) { return null; }
                int mask = 0;
                List<int>? members = largeTerms is null ? null : new();
                bool target = false;
                bool source = false;
                foreach (int ordinal in columns)
                {
                    int binding = BindingOf(ordinal);
                    if (binding < start) { target = true; }
                    else
                    {
                        source = true;
                        if (members is null) { mask |= 1 << (binding - start); }
                        else if (!members.Contains(binding - start)) { members.Add(binding - start); }
                    }
                }
                // A flat UPDATE/DELETE source can be parameterized by the target's row.
                // Independent source materialization would discard those indexed probes.
                if (target && source && boundary is null) { return null; }
                if (target) { targetTerms.Add(term); }
                else if (members is not null)
                {
                    if (members.Count == 0) { members.Add(0); }
                    members.Sort();
                    largeTerms!.Add((members.ToArray(), term));
                }
                else { terms.Add((mask == 0 ? 1 : mask, term)); }
            }
        }
        if (largeTerms is not null)
        {
            if (boundary is not null && targetTerms.Count != 0) { throw new System.Diagnostics.UnreachableException(); }
            return SearchLargeJoinOrder(plan, sourceOnly, largeTerms, targetTerms, choose, probe, costBound, retainedFrames, cancellationToken);
        }
        var inputs = plan.Inputs.Select((input, i) =>
            input with { Access = choose(plan.Bindings[i].Source.Table!, input.Access) }).ToArray();
        var boundInputs = inputs.ToArray();
        var estimates = new BTreeRangeCount?[plan.Bindings.Count];
        var baseRows = new double[count];
        for (int i = 0; i < count; i++)
        {
            int binding = start + i;
            estimates[binding] = inputs[binding].Access switch
            {
                SqlIndexSeekPath { Cost.Estimate: { } observed } => observed,
                SqlScanPath { Cost.Estimate: { } observed } => observed,
                SqlIndexSeekPath seek when seek.Index.IsUnique && seek.EqualityValues.Count == seek.Index.ColumnNames.Count => null,
                _ => probe(plan.Bindings[binding].Source.Table!, inputs[binding].Access as SqlIndexSeekPath),
            };
            // An unprobed unique equality has an upper bound of one, not an exact observation.
            baseRows[i] = inputs[binding].Access is SqlIndexSeekPath unique && unique.Index.IsUnique &&
                unique.EqualityValues.Count == unique.Index.ColumnNames.Count ? 1 : Estimate(estimates[binding]);
            var local = Conjuncts(inputs[binding].Filter)
                .Concat(terms.Where(term => term.Mask == (1 << i)).Select(term => term.Expression))
                .Distinct<SqlBoundExpression>(ReferenceEqualityComparer.Instance).ToArray();
            if (local.Length != 0)
            {
                inputs[binding] = inputs[binding] with { Filter = Combine(local) };
            }
        }
        // Reuse each input count and each subset cardinality throughout all partitions.
        var rows = new double[1 << count];
        for (int mask = 1; mask < rows.Length; mask++)
        {
            double value = 1;
            for (int i = 0; i < count; i++) { if ((mask & (1 << i)) != 0) { value *= baseRows[i]; } }
            foreach (var term in terms)
            {
                if ((term.Mask & mask) != term.Mask || BitOperations.IsPow2(term.Mask)) { continue; }
                double selectivity = 1d / 3;
                if (term.Expression is SqlBoundBinary { Operator: SqlBinaryOperator.Equal, Left: SqlBoundColumn a, Right: SqlBoundColumn b })
                { selectivity = 1 / Math.Max(1, Math.Max(baseRows[BindingOf(a.Ordinal) - start], baseRows[BindingOf(b.Ordinal) - start])); }
                value *= selectivity;
            }
            rows[mask] = Math.Min(1e100, value);
        }
        var indexes = Enumerable.Range(start, count).Select(i => _catalog.GetIndexes(plan.Bindings[i].Source.Table!.ObjectId)
            .OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase).ThenBy(index => index.Name, StringComparer.Ordinal).ToArray()).ToArray();
        var provenPaths = plan.Steps.SelectMany(step => step.Access is { } access
            ? access.Alternatives ?? new[] { access } : Array.Empty<SqlJoinIndexPath>()).OfType<SqlPartialJoinIndexPath>().ToArray();
        var baseline = costBound(boundInputs, estimates);
        var result = SqlJoinSearch.Find(count, mask => rows[mask], MakeJoin, cancellationToken, RetainedBytes);
        var tree = Remap(result.Tree);
        if (sourceOnly)
        {
            if (boundary is not null)
            {
                if (targetTerms.Count != 0) { throw new System.Diagnostics.UnreachableException(); }
                tree = ReferenceEquals(sourceTree, boundary.Left) ? boundary with { Left = tree } : boundary with { Right = tree };
            }
            else
            {
                var condition = Combine(targetTerms);
                tree = new(-1, new(0), tree, new(start, condition is null ? SqlJoinType.Cross : SqlJoinType.Inner, condition, null));
            }
            // Preserve normalized costing at the fixed target/source boundary too.
            // Source reordering must not turn a formerly hashed association into a product.
            if (tree.Step is { Access: null, Hash: null } rootStep)
            {
                var inner = tree.Right!.Leaves().ToArray();
                tree = tree with { Step = rootStep with { Hash = SqlPlanner.HashKeys(rootStep.Condition,
                    plan.Bindings, plan.Columns, inner.Min(), inner.Max(), fullFrame: true) } };
            }
        }
        var steps = TreeSteps(tree).ToArray();
        var selected = plan with { Inputs = inputs, InputEstimates = estimates, Tree = tree, Steps = steps,
            Where = sourceOnly && boundary is not null ? plan.Where : null };
        string order = Order(tree);
        string written = plan.Tree is { } original ? Order(original, identities: true) : FlatOrder(plan);
        var searched = new SqlSearchedJoinPlan(selected, result.States, result.Partitions, result.Cost, order,
            Order(tree, identities: true) != written, sourceOnly);
        // Expression vectors evaluate full ordinal frames, so retain the normalized executor.
        bool expressions = steps.Any(step => step.Hash is { } hash &&
            (hash.OuterExpressions is not null || hash.InnerExpressions is not null));
        if (sourceOnly && boundary is null && IsLeftDeep(tree.Right!) && !expressions)
        {
            // Flatten the independent source tree after the pinned target. Yield directly
            // to association deduplication; never retain the target x source output.
            var first = tree.Right!.Leaves().First();
            var flat = new[] { new SqlJoinStep(first, SqlJoinType.Cross, null, null) }
                .Concat(TreeSteps(tree.Right!)).Select(LocalKeys).ToArray();
            selected = selected with { Tree = null, Steps = flat, FirstBinding = 0, Where = Combine(targetTerms) };
        }
        else if (!sourceOnly && IsLeftDeep(tree) && !expressions)
        {
            selected = selected with { Tree = null, Steps = steps.Select(LocalKeys).ToArray(), FirstBinding = tree.Leaves().First() };
        }
        // Estimates rank work; they cannot prove a byte bound for arbitrary stored values,
        // duplicate fanout or incomplete probes. Certify the physical retention separately.
        // Normalized/full-width candidates currently have no such certificate: use the old
        // executor and paths, including expression hashes. The permitted byte increase is zero.
        if (!PreservesRetention(baseline, selected, sourceOnly || retainedFrames))
        {
            var oldTree = PhysicalTree(baseline);
            if (baseline.Tree is null && baseline.FirstBinding == 1)
            { baseline = baseline with { Steps = baseline.Steps.Select((step, i) => i == 0 ? step with { Binding = 0 } : step).ToArray() }; }
            return new SqlSearchedJoinPlan(baseline, result.States, result.Partitions, result.Cost,
                Order(oldTree), Order(oldTree, identities: true) != written, sourceOnly);
        }
        return searched with { Tree = selected.Tree, Steps = selected.Steps, FirstBinding = selected.FirstBinding, Where = selected.Where };

        SqlJoinStep LocalKeys(SqlJoinStep step) => step with { Hash = step.Hash is null ? null :
            SqlPlanner.HashKeys(step.Condition, plan.Bindings, plan.Columns, step.Binding) };

        SqlJoinTree PhysicalTree(SqlJoinPlan original)
        {
            if (original.Tree is { } normalized) { return normalized; }
            var node = new SqlJoinTree(original.FirstBinding);
            for (int i = 0; i < original.Steps.Count; i++)
            {
                var step = original.Steps[i];
                int inner = i == 0 && original.FirstBinding == 1 ? 0 : step.Binding;
                node = new(-1, node, new(inner), step);
            }
            return node;
        }

        double RetainedBytes(SqlJoinTree node)
        {
            // Cost the actual executor shape, including padded ordinal arrays. Scalar
            // payloads use the hash accountant's 32-byte allowance. Variable payloads
            // remain estimates, never the retention certificate below.
            bool flat = IsLeftDeep(node) && !TreeSteps(node).Any(step => step.Hash is { } hash &&
                (hash.OuterExpressions is not null || hash.InnerExpressions is not null));
            if (flat)
            {
                double retained = 0;
                foreach (var step in TreeSteps(node))
                {
                    if (step.Hash is not null)
                    { retained += Build(baseRows[step.Binding - start], 24 + 40 * plan.Bindings[step.Binding].Source.Columns.Count); }
                }
                return retained;
            }
            return Materialized(node).Peak;

            (double Peak, double Output) Materialized(SqlJoinTree current)
            {
                int mask = 0, width = 0;
                foreach (int leaf in current.Leaves())
                { mask |= 1 << leaf; width += plan.Bindings[leaf + start].Source.Columns.Count; }
                double output = rows[mask] * (24 + 8 * plan.Columns.Count + 32 * width);
                if (current.Step is null) { return (output, output); }
                var left = Materialized(current.Left!);
                var right = Materialized(current.Right!);
                double build = current.Step.Hash is null ? right.Output : Build(rows[current.Right!.Leaves().Aggregate(0, (m, leaf) => m | (1 << leaf))],
                    24 + 8 * plan.Columns.Count + 32 * current.Right.Leaves().Sum(leaf => plan.Bindings[leaf + start].Source.Columns.Count));
                return (Math.Max(left.Peak, Math.Max(left.Output + right.Peak + build, left.Output + build + output)), output);
            }
            static double Build(double count, double width)
            {
                double capacity = count <= 0 ? 0 : Math.Max(16, Math.Pow(2, Math.Ceiling(Math.Log2(count))));
                // Include old and new bucket/entry arrays at the largest growth.
                return 104 + count * width + (capacity == 0 ? 0 : 96 + 30 * capacity);
            }
        }

        (SqlJoinStep Step, double Work) MakeJoin(int left, int right)
        {
            var crossing = terms.Where(term => (term.Mask & (left | right)) == term.Mask &&
                (term.Mask & left) != 0 && (term.Mask & right) != 0).Select(term => term.Expression).ToArray();
            var condition = Combine(crossing);
            int inner = start + BitOperations.TrailingZeroCount(right);
            var keys = SqlPlanner.HashKeys(condition, plan.Bindings, plan.Columns, inner,
                fullFrame: true, innerMask: right << start);
            SqlJoinIndexPath? access = BitOperations.IsPow2(right) ? IndexPath(inner, left << start, crossing) : null;
            double work = SearchJoinWork(rows[left], rows[right], baseRows[inner - start], ref access, ref keys);
            return (new(inner, condition is null ? SqlJoinType.Cross : SqlJoinType.Inner, condition, access) { Hash = keys }, work);
        }

        SqlJoinIndexPath? IndexPath(int inner, int outer, IReadOnlyList<SqlBoundExpression> conditions)
        {
            SqlJoinIndexPath? best = null;
            var binding = plan.Bindings[inner];
            foreach (var index in indexes[inner - start])
            {
                var proven = provenPaths
                    .FirstOrDefault(path => path.InnerBinding == inner && ReferenceEquals(path.Index, index));
                string? proof = proven?.Proof ?? (inputs[inner].Access is SqlPartialIndexSeekPath input && ReferenceEquals(input.Index, index) ? input.Proof : null);
                if (index.Predicate is not null && proof is null) { continue; }
                var ordinals = new List<int>();
                var columns = new List<SqlCatalogColumn>();
                foreach (string name in index.ColumnNames)
                {
                    int local = -1;
                    for (int i = 0; i < binding.Source.Columns.Count; i++)
                    { if (string.Equals(binding.Source.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) { local = i; break; } }
                    if (local < 0) { break; }
                    int ordinal = binding.Offset + local;
                    int comparand = -1;
                    foreach (var condition in conditions)
                    {
                        if (condition is not SqlBoundBinary { Operator: SqlBinaryOperator.Equal, Left: SqlBoundColumn a, Right: SqlBoundColumn b } equality) { continue; }
                        int other = a.Ordinal == ordinal ? b.Ordinal : b.Ordinal == ordinal ? a.Ordinal : -1;
                        if (other < 0 || (outer & (1 << BindingOf(other))) == 0 ||
                            !SqlPlanner.CanSeekJoinEquality(plan.Columns[ordinal].Type.Type, plan.Columns[other].Type.Type)) { continue; }
                        var column = plan.Columns[ordinal];
                        var collation = equality.Collation.Resolve(null);
                        if (column.Type.Type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json &&
                            (!collation.IsIndexBacked || collation != (column.Collation ?? _catalog.DefaultCollation))) { continue; }
                        comparand = other;
                        break;
                    }
                    if (comparand < 0) { break; }
                    ordinals.Add(comparand);
                    columns.Add(plan.Columns[ordinal]);
                }
                if (ordinals.Count == 0 || best is not null && (ordinals.Count < best.OuterOrdinals.Count ||
                    ordinals.Count == best.OuterOrdinals.Count && (!index.IsUnique || best.Index.IsUnique))) { continue; }
                best = proof is null ? new SqlJoinIndexPath(inner, index, ordinals, columns) : new SqlPartialJoinIndexPath(inner, index, ordinals, columns, proof);
            }
            return best;
        }

        int BindingOf(int ordinal)
        {
            for (int i = plan.Bindings.Count - 1; i >= 0; i--) { if (ordinal >= plan.Bindings[i].Offset) { return i; } }
            throw new System.Diagnostics.UnreachableException();
        }

        SqlJoinTree Remap(SqlJoinTree node) => node.Step is null ? new(node.Binding + start)
            : node with { Left = Remap(node.Left!), Right = Remap(node.Right!) };

        string Order(SqlJoinTree node, bool identities = false) => node.Step is null
            ? identities ? node.Binding.ToString(System.Globalization.CultureInfo.InvariantCulture) : Name(node.Binding)
            : "(" + Order(node.Left!, identities) + "," + Order(node.Right!, identities) + ")";
        string Name(int binding) => plan.Bindings[binding].Reference.Alias ?? plan.Bindings[binding].Source.Name;
        string FlatOrder(SqlJoinPlan original)
        {
            string order = "0";
            for (int i = 1; i < original.Bindings.Count; i++) { order = "(" + order + "," + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"; }
            return order;
        }
    }

    private static double SearchJoinWork(double outerRows, double innerRows, double baseInnerRows,
        ref SqlJoinIndexPath? access, ref SqlHashJoinPath? keys)
    {
        double work = outerRows * innerRows;
        if (access is not null)
        {
            double seek = outerRows * (1 + Math.Log2(baseInnerRows + 1));
            if (seek <= work || access.Index.IsUnique && access.OuterOrdinals.Count == access.Index.ColumnNames.Count) { work = seek; }
            else { access = null; }
        }
        double hash = 4 * (outerRows + innerRows);
        if (keys is null || hash >= work) { keys = null; }
        else { access = null; work = hash; }
        return work;
    }

    private static bool IsLeftDeep(SqlJoinTree tree) => tree.Step is null || tree.Right!.Step is null && IsLeftDeep(tree.Left!);

    private static bool PreservesRetention(SqlJoinPlan baseline, SqlJoinPlan candidate, bool dml)
    {
        if (baseline.Tree is not null || candidate.Tree is not null) { return false; }
        var old = baseline.Steps.Select((step, i) => i == 0 && baseline.FirstBinding == 1
            ? step with { Binding = 0 } : step).ToArray();
        // Joined DML also charges buffered inputs and selected frames through its ledger.
        // Until it has a width/fanout proof, retain the same streaming order and buffers.
        // Hash activation must also retain its prefix: a zero-result prefix in the old
        // plan can avoid a later build whose complete input would exceed the grant.
        if ((dml || old.Any(step => step.Hash is not null)) && (candidate.FirstBinding != baseline.FirstBinding ||
            !candidate.Steps.Select(step => step.Binding).SequenceEqual(old.Select(step => step.Binding)))) { return false; }
        for (int i = 0; i < baseline.Inputs.Count; i++)
        { if (!ReferenceEquals(baseline.Inputs[i].Access, candidate.Inputs[i].Access)) { return false; } }
        foreach (var step in candidate.Steps)
        {
            var previous = old.FirstOrDefault(item => item.Binding == step.Binding);
            // Replacing a shared input buffer with repeated seeks can retain fresh
            // boxed/text cells per association instead of aliasing one decoded row.
            if (dml && previous?.Access is null && step.Access is not null) { return false; }
            // All flat hash tables coexist. Preserve each build input and its NULL-key
            // population, with local-width rows and unchanged bucket growth. Added safe
            // input restrictions can only remove rows. No new budgeted build is allowed.
            if (step.Hash is { } hash && (previous?.Hash is not { } prior ||
                !hash.InnerOrdinals.SequenceEqual(prior.InnerOrdinals) ||
                !hash.Collations.SequenceEqual(prior.Collations))) { return false; }
        }
        foreach (var step in old)
        {
            var next = candidate.Steps.FirstOrDefault(item => item.Binding == step.Binding);
            if (step.Hash is not null && next?.Hash is null) { return false; }
            if (step.Access is { } access && (next?.Access is not { } chosen ||
                !ReferenceEquals(access.Index, chosen.Index) || !access.OuterOrdinals.SequenceEqual(chosen.OuterOrdinals))) { return false; }
        }
        return true;
    }

    private static IEnumerable<SqlJoinStep> TreeSteps(SqlJoinTree node)
    {
        if (node.Step is null) { yield break; }
        foreach (var step in TreeSteps(node.Left!)) { yield return step; }
        foreach (var step in TreeSteps(node.Right!)) { yield return step; }
        yield return node.Step;
    }

    private static IEnumerable<SqlBoundExpression> Conjuncts(SqlBoundExpression? expression)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlBoundLogical { IsOr: false } logical)
        { foreach (var operand in logical.Operands) { foreach (var term in Conjuncts(operand)) { yield return term; } } }
        else if (expression is not null) { yield return expression; }
    }

    private static SqlBoundExpression? Combine(IReadOnlyList<SqlBoundExpression> expressions)
        => expressions.Count == 0 ? null : expressions.Count == 1 ? expressions[0] : new SqlBoundLogical(false, expressions.ToArray());
}
