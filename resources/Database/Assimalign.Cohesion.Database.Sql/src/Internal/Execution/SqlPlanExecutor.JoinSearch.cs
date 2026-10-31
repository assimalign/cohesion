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
        Func<SqlCatalogTable, SqlIndexSeekPath?, BTreeRangeCount?> probe, CancellationToken cancellationToken)
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
            if (boundary.Left is { Binding: 0, Step: null }) { sourceTree = boundary.Right; }
            else if (boundary.Right is { Binding: 0, Step: null }) { sourceTree = boundary.Left; }
            else { return null; }
        }
        var sourceSteps = sourceTree is null ? plan.Steps : TreeSteps(sourceTree).ToArray();
        if (sourceSteps.Any(step => step.Kind is not (SqlJoinType.Inner or SqlJoinType.Cross))) { return null; }
        var terms = new List<(int Mask, SqlBoundExpression Expression)>();
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
                bool target = false;
                foreach (int ordinal in columns)
                {
                    int binding = BindingOf(ordinal);
                    if (binding < start) { target = true; }
                    else { mask |= 1 << (binding - start); }
                }
                if (target) { targetTerms.Add(term); }
                else { terms.Add((mask == 0 ? 1 : mask, term)); }
            }
        }
        var inputs = plan.Inputs.Select((input, i) => i < start ? input
            : input with { Access = choose(plan.Bindings[i].Source.Table!, input.Access) }).ToArray();
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
        var result = SqlJoinSearch.Find(count, mask => rows[mask], MakeJoin, cancellationToken);
        var tree = Remap(result.Tree);
        if (sourceOnly)
        {
            if (boundary is not null)
            { tree = ReferenceEquals(sourceTree, boundary.Left) ? boundary with { Left = tree } : boundary with { Right = tree }; }
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
        // A left-deep SELECT uses the streaming flat executor and local hash-key ordinals.
        // DML keeps its explicit target/source boundary even when both sides are INNER.
        if (!sourceOnly && IsLeftDeep(tree))
        {
            var flat = steps.Select(step => step with { Hash = step.Hash is null ? null :
                SqlPlanner.HashKeys(step.Condition, plan.Bindings, plan.Columns, step.Binding) }).ToArray();
            return searched with { Tree = null, Steps = flat, FirstBinding = tree.Leaves().First() };
        }
        return searched;

        (SqlJoinStep Step, double Work) MakeJoin(int left, int right)
        {
            var crossing = terms.Where(term => (term.Mask & (left | right)) == term.Mask &&
                (term.Mask & left) != 0 && (term.Mask & right) != 0).Select(term => term.Expression).ToArray();
            var condition = Combine(crossing);
            int inner = start + BitOperations.TrailingZeroCount(right);
            var keys = SqlPlanner.HashKeys(condition, plan.Bindings, plan.Columns, inner,
                fullFrame: true, innerMask: right << start);
            SqlJoinIndexPath? access = BitOperations.IsPow2(right) ? IndexPath(inner, left << start, crossing) : null;
            double work = rows[left] * rows[right];
            if (access is not null)
            {
                double seek = rows[left] * (1 + Math.Log2(baseRows[inner - start] + 1));
                // Unique probes bound candidate work even with duplicate outer keys.
                if (seek <= work || access.Index.IsUnique && access.OuterOrdinals.Count == access.Index.ColumnNames.Count) { work = seek; }
                else { access = null; }
            }
            double hash = 4 * (rows[left] + rows[right]);
            if (keys is null || hash >= work) { keys = null; }
            else { access = null; work = hash; }
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

    private static bool IsLeftDeep(SqlJoinTree tree) => tree.Step is null || tree.Right!.Step is null && IsLeftDeep(tree.Left!);

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
