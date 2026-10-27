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
        => plan is SqlSelectPlan { Access: SqlScanPath } ||
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
            SqlGroupPlan p => p with { Input = Rewrite(p.Input) },
            SqlSubqueryPlan p => p with { Input = Rewrite(p.Input), Queries = p.Queries.Select(q => q with { Query = Rewrite(q.Query) }).ToArray() },
            SqlCorrelatedSubqueryPlan p => p with { Input = Rewrite(p.Input), Queries = p.Queries.Select(q => q with { Query = Rewrite(q.Query) }).ToArray() },
            SqlSetOperationPlan p => p with { Operands = p.Operands.Select(q => q with { Query = Rewrite(q.Query) }).ToList() },
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
            var chosen = Choose(select.Table, select.Access);
            return ReferenceEquals(chosen, select.Access) ? select : select with { Access = chosen };
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
                .ThenBy(index => index.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
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
