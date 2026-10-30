using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private static bool HasPartialIndexes(IReadOnlyList<SqlCatalogIndex> indexes)
    {
        for (int i = 0; i < indexes.Count; i++) { if (indexes[i].Predicate is not null) { return true; } }
        return false;
    }

    private bool HasReadPartialIndex(ulong tableObjectId)
        => ViewImageState is { } image ? HasPartialIndexes(image.Snapshot.GetIndexes(tableObjectId))
            : _catalog.ContainsPartialIndex(tableObjectId);

    private string? ProvePartialIndex(SqlCatalogTable table, SqlCatalogIndex index,
        IReadOnlyList<SqlBoundExpression> restrictions, int offset = 0)
    {
        var predicate = SqlPartialIndexPredicate.Bind(table, index, _catalog.DefaultCollation, _functions, _cancellationToken);
        var proof = SqlPredicateImplication.Prove(predicate, restrictions, offset, _cancellationToken);
        return proof is null ? null : SqlPredicateImplication.Describe(proof, table.Columns, offset);
    }

    // Runs only after binding the entire join. In particular, ON at an outer boundary
    // cannot prove that the preserved input contains only matching rows.
    private SqlJoinPlan PlanPartialJoinAccess(SqlJoinPlan plan)
    {
        if (_queryScopes?.DeferQueryEvaluation == true || ViewDefinitionBinding is not null) { return plan; }
        bool partial = false;
        for (int i = 0; i < plan.Bindings.Count; i++)
        {
            if (plan.Bindings[i].Source.Table is { } table && HasReadPartialIndex(table.ObjectId))
            { partial = true; break; }
        }
        if (!partial) { return plan; }

        var nullable = new bool[plan.Bindings.Count];
        HashSet<SqlJoinStep>? probeSteps = plan.Tree is null ? null : new(ReferenceEqualityComparer.Instance);
        if (plan.Tree is { } tree)
        {
            var pending = new Stack<SqlJoinTree>();
            pending.Push(tree);
            while (pending.TryPop(out var node))
            {
                if (node.Step is not { } step) { continue; }
                if (node.Right!.Step is null) { probeSteps!.Add(step); }
                if (step.Kind is SqlJoinType.LeftOuter or SqlJoinType.RightOuter or SqlJoinType.FullOuter)
                { foreach (int input in node.Right!.Leaves()) { nullable[input] = true; } }
                if (step.Kind == SqlJoinType.FullOuter)
                { foreach (int input in node.Left!.Leaves()) { nullable[input] = true; } }
                pending.Push(node.Right!); pending.Push(node.Left!);
            }
        }
        else
        {
            foreach (var step in plan.Steps)
            { if (step.Kind == SqlJoinType.LeftOuter) { nullable[step.Binding] = true; } }
        }
        var restrictions = new IReadOnlyList<SqlBoundExpression>[plan.Bindings.Count];
        for (int i = 0; i < restrictions.Length; i++)
        {
            var terms = new List<SqlBoundExpression>();
            restrictions[i] = terms;
            if (nullable[i]) { continue; }
            var binding = plan.Bindings[i];
            Add(plan.Where);
            foreach (var step in plan.Steps) { if (step.Kind == SqlJoinType.Inner) { Add(step.Condition, fromOn: true); } }

            void Add(SqlBoundExpression? expression, bool fromOn = false)
            {
                if (expression is null) { return; }
                foreach (var term in SqlPredicateImplication.Conjuncts(expression))
                {
                    // Constant-only ON terms can belong to an unrelated subtree that an
                    // outer join preserves this input against. They prove nothing here.
                    if (SqlPredicateImplication.ReferencesOnly(term, binding.Offset, binding.Source.Columns.Count, out bool referencesColumn) &&
                        (!fromOn || referencesColumn)) { terms.Add(term); }
                }
            }
        }
        var inputs = plan.Inputs.ToArray();
        for (int i = 0; i < inputs.Length; i++)
        {
            var binding = plan.Bindings[i];
            if (binding.Source.Table is not { } table || restrictions[i].Count == 0) { continue; }
            // These are original local comparisons, never a transitive equality supplied
            // by another input. Coercion remains the existing seek builder's contract.
            var bounds = new Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>>();
            foreach (var term in restrictions[i])
            {
                if (!SqlPredicateImplication.Comparison(term, out var column, out var op, out var value, out var collation)) { continue; }
                int local = column.Ordinal - binding.Offset;
                var metadata = table.Columns[local];
                if (metadata.Type.Type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json &&
                    (!collation.Resolve(null).IsIndexBacked || collation.Resolve(null) != (metadata.Collation ?? _catalog.DefaultCollation))) { continue; }
                if (op != SqlBinaryOperator.Equal && !IsRangeSargable(metadata.Type.Type)) { continue; }
                if (value is double number && number == 0d || value is float single && single == 0f) { continue; }
                try
                {
                    var stored = SqlPlanExecutor.CoerceForColumn(value, metadata);
                    if (stored is null || SqlExpressionEvaluator.Compare(value, stored) != 0) { continue; }
                    if (!bounds.TryGetValue(local, out var values)) { bounds.Add(local, values = []); }
                    values.Add((op, stored));
                }
                catch (DatabaseException) { /* Retain failures in the original residual. */ }
            }
            var access = SelectAccessPath(table, bounds, restrictions: restrictions[i], offset: binding.Offset);
            if (access is SqlIndexSeekPath) { inputs[i] = inputs[i] with { Access = access }; }
        }
        var steps = plan.Steps.ToArray();
        var replacements = new Dictionary<SqlJoinStep, SqlJoinStep>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var replacement = step;
            if (step.Kind == SqlJoinType.Inner && (probeSteps is null || probeSteps.Contains(step)) && step.Condition is { } condition)
            {
                var equalities = SqlPredicateImplication.Conjuncts(condition).OfType<SqlBoundBinary>()
                    .Where(term => term.Operator == SqlBinaryOperator.Equal && term.Left is SqlBoundColumn && term.Right is SqlBoundColumn && term.Collation.IsFixed)
                    .Select(term => (((SqlBoundColumn)term.Left).Ordinal, ((SqlBoundColumn)term.Right).Ordinal, term.Collation.Resolve(null))).ToList();
                int? driver = plan.Tree is null && i == 0 ? plan.FirstBinding : null;
                var access = SelectJoinAccessPath(plan.Bindings, plan.Columns, equalities, step.Binding, false, driver, restrictions);
                if (access is not null) { replacement = step with { Access = access }; }
            }
            replacements.Add(step, replacement);
            steps[i] = replacement;
        }
        return plan with { Inputs = inputs, Steps = steps, Tree = plan.Tree?.ReplaceSteps(replacements) };
    }
}
