using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Optional test observation; ordinary statements retain no additional counters.
    internal static Action<BTreeRangeCount>? CostProbeCompleted { get; set; }

    // Costing follows binding, so ordinary unique lookups allocate neither a cost context nor
    // a probe. All counts are statement-local observations, never catalog statistics.
    internal SqlPlan Cost(SqlPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
        => plan.ContainsRecursion || plan is SqlSelectPlan { Access: SqlScanPath } ||
           plan is SqlSelectPlan { Access: SqlIndexSeekPath { Cost: null, Index.IsUnique: true } unique } &&
               unique.EqualityValues.Count == unique.Index.ColumnNames.Count
            ? plan : CostCore(plan, statement, cancellationToken, new(ReferenceEqualityComparer.Instance));

    private SqlPlan CostCore(SqlPlan plan, SqlStatementContext statement, CancellationToken cancellationToken,
        Dictionary<SqlDerivedRelation, SqlDerivedRelation> relations)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        cancellationToken.ThrowIfCancellationRequested();
        bool definitionChanged = false;
        var costed = plan switch
        {
            SqlSelectPlan p => CostSelect(p),
            SqlUpdatePlan p => p with { Access = Choose(p.Table, p.Access), Source = RewriteDmlSource(p.Source) },
            SqlDeletePlan p => p with { Access = Choose(p.Table, p.Access), Source = RewriteDmlSource(p.Source) },
            SqlMergePlan p => p with { Source = RewriteDmlSource(p.Source)! },
            SqlJoinPlan p => CostJoin(p),
            SqlWindowAggregatePlan p => p with { Input = Rewrite(p.Input) },
            SqlGroupPlan p => p with { Input = Rewrite(p.Input) },
            SqlSubqueryPlan p => p with { Input = Rewrite(p.Input), Queries = p.Queries.Select(q => q with { Query = Rewrite(q.Query) }).ToArray() },
            SqlCorrelatedSubqueryPlan p => p with { Input = Rewrite(p.Input), Queries = p.Queries.Select(q => q with { Query = Rewrite(q.Query) }).ToArray() },
            SqlSetOperationPlan p => p with { Operands = p.Operands.Select(q => q with { Query = Rewrite(q.Query) }).ToList() },
            SqlRecursiveUnionPlan p => p with { Working = RewriteRelation(p.Working),
                Anchor = p.Anchor with { Query = Rewrite(p.Anchor.Query) }, Recursive = p.Recursive with { Query = Rewrite(p.Recursive.Query) } },
            SqlWithPlan p => p with { Input = Rewrite(p.Input), Relations = p.Relations.Select(RewriteRelation).ToArray() },
            SqlInsertSelectPlan p => p with { Source = Rewrite(p.Source) },
            _ => plan,
        };
        // Every rewrite here is optional. Discard earlier successful observations too:
        // join direction and hash selection must retain binding's path after invalidation.
        return definitionChanged ? plan : costed;

        SqlPlan Rewrite(SqlPlan child) => child is SqlSelectPlan { Access: SqlScanPath } ||
            child is SqlSelectPlan { Access: SqlIndexSeekPath { Cost: null, Index.IsUnique: true } seek } &&
                seek.EqualityValues.Count == seek.Index.ColumnNames.Count
                ? child : CostCore(child, statement, cancellationToken, relations);

        SqlDerivedRelation RewriteRelation(SqlDerivedRelation relation)
        {
            if (!relations.TryGetValue(relation, out var rewritten))
            {
                rewritten = relation with { Query = Rewrite(relation.Query) };
                relations.Add(relation, rewritten);
            }
            return rewritten;
        }

        SqlDmlSource? RewriteDmlSource(SqlDmlSource? source)
            => source is null ? null : source with { Associations = CostJoin(source.Associations, pinFirst: true) };

        SqlPlan CostSelect(SqlSelectPlan select)
        {
            var chosen = select.Access is SqlOrderedIndexPath ordered ? ChooseOrdered(select, ordered) : Choose(select.Table, select.Access);
            return ReferenceEquals(chosen, select.Access) ? select : select with { Access = chosen };
        }

        SqlAccessPath ChooseOrdered(SqlSelectPlan select, SqlOrderedIndexPath original)
        {
            SqlIndexSeekPath best = original;
            BTreeRangeCount? bestCount = null;
            double bestWork = double.PositiveInfinity;
            double window = select.IsDistinct ? double.PositiveInfinity : CostCount(select.Limit, double.PositiveInfinity) + CostCount(select.Offset, 0);
            var alternatives = original.Cost!.Alternatives;
            var counts = new BTreeRangeCount?[alternatives.Count];
            for (int i = 0; i < alternatives.Count; i++) { counts[i] = Probe(select.Table, alternatives[i]); }
            BTreeRangeCount? total = null;
            bool totalProbed = false;
            for (int i = 0; i < alternatives.Count; i++)
            {
                var candidate = alternatives[i];
                var count = counts[i];
                if (count is null) { continue; }
                double rows = Estimate(count);
                // Retain the established four sequential-row units per random heap fetch.
                // A comparison costs one eighth of a sequential row. A bounded sorter
                // compares against its retained top-N window, but still reads all candidates.
                double work = candidate is SqlOrderedIndexPath
                    ? 4 * Math.Min(rows, window / ResidualSelectivity(candidate)) : 4 * rows + SortWork(rows);
                if (work < bestWork) { best = candidate; bestCount = count; bestWork = work; }
            }
            if (bestCount is null) { return original; }
            if (bestCount.Value.Count > 16)
            {
                total = Total();
                // An unbounded unconditional index proves the same candidate population
                // as the table, so its estimate also permits comparing full-scan work.
                bool wholeTable = best.Index.Predicate is null && best.EqualityValues.Count == 0 && best.Lower is null && best.Upper is null;
                if (total is { } observed && (observed.IsComplete || wholeTable) && Estimate(total) + SortWork(Estimate(total)) < bestWork)
                { return new SqlScanPath { Cost = new([]) { Estimate = total } }; }
            }
            return best with { Cost = new([]) { Estimate = bestCount } };

            double SortWork(double rows) => rows * Math.Log2(Math.Min(rows, window) + 1) / 8;

            BTreeRangeCount? Total()
            {
                if (!totalProbed) { total = Probe(select.Table, null); totalProbed = true; }
                return total;
            }

            double ResidualSelectivity(SqlIndexSeekPath candidate)
            {
                if (select.Where is null || double.IsPositiveInfinity(window)) { return 1; }
                SqlIndexSeekPath? known = null;
                double knownRows = double.PositiveInfinity;
                for (int j = 0; j < alternatives.Count; j++)
                {
                    if (alternatives[j] is SqlOrderedIndexPath || counts[j] is not { IsComplete: true } exact) { continue; }
                    bool residualEvidence = alternatives[j].Index.Predicate is not null && candidate.Index.Predicate is null;
                    foreach (var term in SqlPredicateImplication.Conjuncts(select.Where))
                    { residualEvidence |= Covered(term, alternatives[j]) && !Covered(term, candidate); }
                    if (residualEvidence && exact.Count < knownRows) { known = alternatives[j]; knownRows = exact.Count; }
                }
                // An incomplete base probe proves at least 1,024 rows. Use that lower
                // bound rather than pretending a sibling's exact count is the table size.
                double fraction = known is null ? 1 : Math.Min(1, knownRows / Math.Max(1, Estimate(Total())));
                var enforced = Predicate(candidate);
                var counted = known is null ? null : Predicate(known);
                Span<byte> ranges = select.Table.Columns.Count <= 64 ? stackalloc byte[select.Table.Columns.Count] : new byte[select.Table.Columns.Count];
                ranges.Clear();
                foreach (var term in SqlPredicateImplication.Conjuncts(select.Where))
                {
                    if (Covered(term, candidate) || known is not null && Covered(term, known) ||
                        ImpliedBy(term, enforced) || ImpliedBy(term, counted)) { continue; }
                    // PostgreSQL src/include/utils/selfuncs.h:34-55. These are fallback
                    // estimates for residual restrictions, never stored statistics.
                    if (term is SqlBoundBinary binary)
                    {
                        if (binary.Operator == SqlBinaryOperator.Equal) { fraction *= 0.005; }
                        else if (binary.Operator is SqlBinaryOperator.LessThan or SqlBinaryOperator.LessOrEqual or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterOrEqual)
                        {
                            if (ComparisonColumn(binary) is int ordinal)
                            {
                                bool lower = binary.Operator is SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterOrEqual;
                                if (binary.Right is SqlBoundColumn) { lower = !lower; }
                                ranges[ordinal] |= lower ? (byte)2 : (byte)1;
                            }
                            else { fraction *= 1d / 3; }
                        }
                    }
                    else if (term is SqlBoundBetween { IsNegated: false }) { fraction *= 0.005; }
                    else if (term is SqlBoundLike { IsNegated: false }) { fraction *= 0.005; }
                }
                foreach (byte range in ranges)
                { fraction *= range == 3 ? 0.005 : range == 0 ? 1 : 1d / 3; }
                return Math.Max(double.Epsilon, fraction);
            }

            SqlBoundExpression? Predicate(SqlIndexSeekPath path) => path.Index.Predicate is null ? null
                : _definitions.GetIndexPredicate(select.Table, path.Index, cancellationToken);

            bool ImpliedBy(SqlBoundExpression term, SqlBoundExpression? predicate) => predicate is not null &&
                SqlPredicateImplication.Prove(term, [predicate], cancellationToken: cancellationToken) is not null;

            bool Covered(SqlBoundExpression term, SqlIndexSeekPath path)
            {
                int? ordinal = term is SqlBoundBinary binary ? ComparisonColumn(binary)
                    : term is SqlBoundBetween { IsNegated: false, LowOperand: SqlBoundColumn low, HighOperand: SqlBoundColumn high } && low.Ordinal == high.Ordinal ? low.Ordinal : null;
                if (ordinal is null) { return false; }
                string name = select.Table.Columns[ordinal.Value].Name;
                for (int key = 0; key < path.EqualityValues.Count; key++)
                { if (string.Equals(path.Index.ColumnNames[key], name, StringComparison.OrdinalIgnoreCase)) { return true; } }
                return (path.Lower is not null || path.Upper is not null) &&
                    string.Equals(path.Index.ColumnNames[path.EqualityValues.Count], name, StringComparison.OrdinalIgnoreCase);
            }

            static int? ComparisonColumn(SqlBoundBinary binary)
                => binary.Left is SqlBoundColumn left && binary.Right is SqlBoundConstant or SqlBoundParameter ? left.Ordinal
                    : binary.Right is SqlBoundColumn right && binary.Left is SqlBoundConstant or SqlBoundParameter ? right.Ordinal : null;

            // Never execute count expressions during costing: volatile/deferred values
            // initialize once at execution. Fixed integers and supplied parameters are safe.
            static double CostCount(SqlRowCount? count, double absent)
            {
                if (count is null) { return absent; }
                if (count.Expression is null) { return count.Count; }
                object? value = count.Expression switch { SqlBoundConstant constant => constant.Value, SqlBoundParameter parameter => parameter.Value, _ => null };
                return value switch { sbyte n when n >= 0 => n, short n when n >= 0 => n, int n when n >= 0 => n, long n when n >= 0 => n, _ => double.PositiveInfinity };
            }
        }

        BTreeRangeCount? Probe(SqlCatalogTable table, SqlIndexSeekPath? seek)
        {
            // DROP detaches the directory entry; statement/snapshot reclamation fences retain
            // its pages. CountRange owns the tree read latch, including against rollback/pruning,
            // so this immutable key shape needs no transaction lock. Validate identities around
            // the observation instead. A changed definition invalidates only the estimate;
            // readers can still use their bound shape, and writes recheck during execution.
            if (!HasCurrentDefinition(table)) { return null; }
            var description = seek?.Index ?? _catalog.GetIndexes(table.ObjectId)
                .OrderBy(index => index.IsPrimaryKey ? 0 : index.IsUnique ? 1 : 2)
                .ThenBy(index => index.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(index => index.Predicate is null);
            if (description is null || !TryResolveSeekTree(table, description, out var tree)) { return null; }
            var range = seek is null ? IndexKeyRange.All : BuildSeekRange(table, seek,
                _definitions.Get(table, cancellationToken).GetKey(description.ColumnNames).Ordinals);
            var count = tree.CountRange(statement.Snapshot, range, cancellationToken: cancellationToken);
            CostProbeCompleted?.Invoke(count);
            if (!HasCurrentDefinition(table)) { return null; }
            return TryResolveSeekTree(table, description, out var current) && ReferenceEquals(tree, current) ? count : null;
        }

        bool HasCurrentDefinition(SqlCatalogTable table)
        {
            if (definitionChanged) { return false; }
            if (_catalog.TryGetTable(table.Schema, table.Name, out var current) && ReferenceEquals(table, current)) { return true; }
            definitionChanged = true;
            return false;
        }

        SqlAccessPath Choose(SqlCatalogTable table, SqlAccessPath path)
        {
            if (path is not SqlIndexSeekPath original) { return path; }
            var alternatives = original.Cost?.Alternatives;
            // A complete unique equality has at most one candidate, without I/O or new metadata.
            if (alternatives is null && original.Index.IsUnique && original.EqualityValues.Count == original.Index.ColumnNames.Count)
            { return path; }
            SqlIndexSeekPath best = original;
            BTreeRangeCount? bestCount = Probe(table, original);
            foreach (var candidate in alternatives ?? [original])
            {
                if (ReferenceEquals(candidate, original)) { continue; }
                var count = Probe(table, candidate);
                if (count is null) { continue; }
                if (bestCount is null || Estimate(count) < Estimate(bestCount)) { best = candidate; bestCount = count; }
            }
            if (bestCount is null) { return original; }
            // Never mutate metadata on a cached view plan. A fresh seek owns this statement's evidence.
            best = best with { Cost = new([]) { Estimate = bestCount } };
            // Random row fetches cost more than a sequential read. Only an exact base count may
            // justify abandoning a seek; an incomplete probe is explicitly just a lower bound.
            if (bestCount.Value.Count > 16)
            {
                var total = Probe(table, null);
                if (total is { IsComplete: true } exact && bestCount.Value.Count * 4 >= exact.Count)
                { return new SqlScanPath { Cost = new([]) { Estimate = total } }; }
            }
            return best;
        }

        SqlJoinPlan CostJoin(SqlJoinPlan join, bool pinFirst = false)
        {
            join = join with { Bindings = join.Bindings.Select(binding => binding.Source.Derived is { } derived
                ? binding with { Source = new SqlRelationSource(RewriteRelation(derived)) } : binding).ToArray() };
            if (join.Steps.Count == 0) { return join; }
            // Preserve the allocation/probe contract of one- and two-relation queries.
            if (join.Bindings.Count >= 3 && join.Bindings.Count <= SqlJoinSearch.ExactRelationLimit + (pinFirst ? 1 : 0) &&
                SearchJoinOrder(join, pinFirst, Choose, Probe, cancellationToken) is { } searched)
            {
                // An earlier input can change while a later input's count is observed.
                // Invalidate the entire optional rewrite, including all memoized subplans.
                foreach (var binding in join.Bindings)
                { if (binding.Source.Table is { } table) { _ = HasCurrentDefinition(table); } }
                return searched;
            }
            if (join.Tree is { } root)
            {
                var normalized = new Dictionary<SqlJoinStep, SqlJoinStep>(ReferenceEqualityComparer.Instance);
                var pending = new Stack<SqlJoinTree>();
                pending.Push(root);
                while (pending.TryPop(out var node))
                {
                    if (node.Step is not { } step) { continue; }
                    pending.Push(node.Left!);
                    pending.Push(node.Right!);
                    var leaves = node.Right!.Leaves().ToArray();
                    int firstInner = leaves.Min(), lastInner = leaves.Max();
                    var keys = step.Hash ?? (step.Access is null
                        ? HashKeys(step.Condition, join, firstInner, lastInner, fullFrame: true) : null);
                    normalized.Add(step, step with { Hash = keys });
                }
                var normalizedSteps = join.Steps.Select(step => normalized[step]).ToArray();
                join = join with { Steps = normalizedSteps };
                return join with { Tree = root.ReplaceSteps(normalized) };
            }
            var inputs = join.Inputs.Select((input, i) => join.Bindings[i].Source.Table is { } table
                ? input with { Access = Choose(table, input.Access) } : input).ToArray();
            BTreeRangeCount?[]? estimates = null;
            bool[]? estimated = null;
            var steps = join.Steps.ToArray();
            int first = join.FirstBinding;
            if (!pinFirst && steps[0] is { Kind: SqlJoinType.Inner, Access.Alternatives: { Count: > 1 } alternatives } initial &&
                !UniqueProbe(initial.Access) && !UniqueInput(first) && GetEstimate(0) is not null && GetEstimate(1) is not null)
            {
                var chosen = initial.Access!;
                double bestCost = ProbeCost(chosen);
                foreach (var candidate in alternatives)
                {
                    double cost = ProbeCost(candidate);
                    if (cost < bestCost) { chosen = candidate; bestCost = cost; }
                }
                first = chosen.InnerBinding == 0 ? 1 : 0;
                steps[0] = initial with { Access = chosen };

                double ProbeCost(SqlJoinIndexPath candidate)
                    => Estimate(GetEstimate(candidate.InnerBinding == 0 ? 1 : 0)) *
                        (1 + Math.Log2(Estimate(GetEstimate(candidate.InnerBinding)) + 1));
            }
            for (int i = 0; i < steps.Length; i++)
            {
                var step = steps[i];
                // These steps cannot benefit from cardinality estimates or a hash build.
                if (UniqueProbe(step.Access) || step.Access is not null && i > 0 || i == 0 && UniqueInput(first)) { continue; }
                int inner = i == 0 && first == 1 ? 0 : step.Binding;
                var keys = HashKeys(step.Condition, join, inner);
                if (keys is null) { continue; }
                var outerCount = i == 0 ? GetEstimate(first) : null;
                if (step.Access is not null && outerCount is null) { continue; }
                var innerCount = GetEstimate(inner);
                double outerRows = i == 0 ? Estimate(outerCount) : 4096;
                double innerRows = Estimate(innerCount);
                // A unique per-row seek wins; broad/unindexed equijoins compare build+probe work
                // with nested candidates. Keep tiny pairs on their allocation-free loop path.
                double nestedWork = step.Access is null ? outerRows * innerRows
                    : outerRows * (1 + Math.Log2(innerRows + 1));
                if (step.Access is not null && innerCount is { IsComplete: true, Count: <= 16 } ||
                    nestedWork <= 4 * (outerRows + innerRows)) { continue; }
                if (!pinFirst && i == 0 && step.Kind == SqlJoinType.Inner && outerRows < innerRows)
                {
                    first = inner;
                    inner = inner == 0 ? 1 : 0;
                    keys = HashKeys(step.Condition, join, inner)!;
                }
                steps[i] = step with { Access = null, Hash = keys };
            }
            return join with { Inputs = inputs, Steps = steps, FirstBinding = first, InputEstimates = estimates };

            BTreeRangeCount? GetEstimate(int binding)
            {
                estimates ??= new BTreeRangeCount?[join.Bindings.Count];
                estimated ??= new bool[join.Bindings.Count];
                if (!estimated[binding])
                {
                    estimated[binding] = true;
                    estimates[binding] = join.Bindings[binding].Source.Table is { } table
                        ? inputs[binding].Access switch
                        {
                            SqlIndexSeekPath { Cost.Estimate: { } count } => count,
                            SqlScanPath { Cost.Estimate: { } count } => count,
                            _ => Probe(table, inputs[binding].Access as SqlIndexSeekPath),
                        } : null;
                }
                return estimates[binding];
            }

            static bool UniqueProbe(SqlJoinIndexPath? path) => path is { Index.IsUnique: true } access &&
                access.OuterOrdinals.Count == access.Index.ColumnNames.Count;

            bool UniqueInput(int binding) => inputs[binding].Access is SqlIndexSeekPath seek &&
                seek.Index.IsUnique && seek.EqualityValues.Count == seek.Index.ColumnNames.Count;
        }
    }

    private static double Estimate(BTreeRangeCount? count) => count is { } value
        ? value.IsComplete ? value.Count : Math.Max(1024, value.Count * 2) : 4096;

    private static SqlHashJoinPath? HashKeys(SqlBoundExpression? condition, SqlJoinPlan plan, int inner,
        int? lastInner = null, bool fullFrame = false)
        => SqlPlanner.HashKeys(condition, plan.Bindings, plan.Columns, inner, lastInner, fullFrame);
}
