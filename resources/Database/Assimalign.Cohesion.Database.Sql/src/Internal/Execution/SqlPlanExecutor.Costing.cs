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
        return plan switch
        {
            SqlSelectPlan p => CostSelect(p),
            SqlUpdatePlan p => p with { Access = Choose(p.Table, p.Access) },
            SqlDeletePlan p => p with { Access = Choose(p.Table, p.Access) },
            SqlJoinPlan p => CostJoin(p),
            SqlGroupPlan p => p with { Input = Rewrite(p.Input) },
            SqlSubqueryPlan p => p with { Input = Rewrite(p.Input), Queries = p.Queries.Select(q => q with { Query = Rewrite(q.Query) }).ToArray() },
            SqlCorrelatedSubqueryPlan p => p with { Input = Rewrite(p.Input), Queries = p.Queries.Select(q => q with { Query = Rewrite(q.Query) }).ToArray() },
            SqlSetOperationPlan p => p with { Operands = p.Operands.Select(q => q with { Query = Rewrite(q.Query) }).ToList() },
            SqlWithPlan p => p with { Input = Rewrite(p.Input), Relations = p.Relations.Select(RewriteRelation).ToArray() },
            SqlInsertSelectPlan p => p with { Source = Rewrite(p.Source) },
            _ => plan,
        };

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
            // the observation instead.
            EnsureCurrentDefinition(table);
            var description = seek?.Index ?? _catalog.GetIndexes(table.ObjectId)
                .OrderBy(index => index.IsPrimaryKey ? 0 : index.IsUnique ? 1 : 2)
                .ThenBy(index => index.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (description is null || !TryResolveSeekTree(table, description, out var tree)) { return null; }
            var range = seek is null ? IndexKeyRange.All : BuildSeekRange(table, seek,
                _definitions.Get(table, cancellationToken).GetKey(description.ColumnNames).Ordinals);
            var count = tree.CountRange(statement.Snapshot, range, cancellationToken: cancellationToken);
            CostProbeCompleted?.Invoke(count);
            EnsureCurrentDefinition(table);
            return TryResolveSeekTree(table, description, out var current) && ReferenceEquals(tree, current) ? count : null;
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

        SqlJoinPlan CostJoin(SqlJoinPlan join)
        {
            join = join with { Bindings = join.Bindings.Select(binding => binding.Source.Derived is { } derived
                ? binding with { Source = new SqlRelationSource(RewriteRelation(derived)) } : binding).ToArray() };
            if (join.Steps.Count == 0) { return join; }
            var inputs = join.Inputs.Select((input, i) => join.Bindings[i].Source.Table is { } table
                ? input with { Access = Choose(table, input.Access) } : input).ToArray();
            BTreeRangeCount?[]? estimates = null;
            bool[]? estimated = null;
            var steps = join.Steps.ToArray();
            int first = join.FirstBinding;
            if (steps[0] is { Kind: SqlJoinType.Inner, Access.Alternatives: { Count: > 1 } alternatives } initial &&
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
                if (i == 0 && step.Kind == SqlJoinType.Inner && outerRows < innerRows)
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

    private static SqlHashJoinPath? HashKeys(SqlBoundExpression? condition, SqlJoinPlan plan, int inner)
    {
        // Skipping nonmatching pairs must not suppress errors, application calls or lazy scalars.
        var referenced = new HashSet<int>();
        if (condition is null || !SqlPlanner.JoinRestrictionColumns(condition, referenced, plan.Columns)) { return null; }
        // Every ON term is skipped for nonmatching hash keys. Derived text has no stored
        // codec guarantee of valid Unicode, even when it appears only in a residual term.
        if (plan.Bindings.Any(binding => binding.Source.Derived is not null && referenced.Any(ordinal =>
            ordinal >= binding.Offset && ordinal < binding.Offset + binding.Source.Columns.Count &&
            plan.Columns[ordinal].Type.Type is DatabaseType.String or DatabaseType.Json or DatabaseType.Character))) { return null; }
        var binding = plan.Bindings[inner];
        var outer = new List<int>();
        var local = new List<int>();
        var collations = new List<Collation>();
        foreach (var term in Terms(condition))
        {
            if (term is not SqlBoundBinary { Operator: SqlBinaryOperator.Equal, Left: SqlBoundColumn a, Right: SqlBoundColumn b } equality) { continue; }
            bool Inside(int ordinal) => ordinal >= binding.Offset && ordinal < binding.Offset + binding.Source.Columns.Count;
            if (Inside(a.Ordinal) == Inside(b.Ordinal)) { continue; }
            int source = Inside(a.Ordinal) ? a.Ordinal : b.Ordinal;
            int probe = Inside(a.Ordinal) ? b.Ordinal : a.Ordinal;
            // CHAR/text comparison trims a comparand and is not a hash equivalence over text.
            if (plan.Columns[source].Type.Type == DatabaseType.Character || plan.Columns[probe].Type.Type == DatabaseType.Character) { continue; }
            outer.Add(probe);
            local.Add(source - binding.Offset);
            collations.Add(equality.Collation.Resolve(null));
        }
        return outer.Count == 0 ? null : new(outer.ToArray(), local.ToArray(), collations.ToArray());

        static IEnumerable<SqlBoundExpression> Terms(SqlBoundExpression expression)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is SqlBoundLogical { IsOr: false } logical)
            { foreach (var operand in logical.Operands) { foreach (var item in Terms(operand)) { yield return item; } } }
            else { yield return expression; }
        }
    }
}
