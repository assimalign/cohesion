using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Check the current plan and immutable definition versions. In particular, a
    // CHECK on another table (or on a read-only source) cannot activate a scope.
    private bool PlanUsesJson(SqlPlan plan, CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (plan.ContainsJsonPath) { return true; }
        if (plan is SqlRelationPlan relation && (JsonProjections(relation.OutputProjections, token) || JsonCounts(plan, token))) { return true; }
        switch (plan)
        {
            case SqlSelectPlan select:
                return _definitions.PreparedTable(select.Table)?.ReadsJsonPath == true ||
                    (JsonExpression(select.Where, token) || JsonOrder(select.OrderBy, token));
            case SqlConstantPlan constant:
                return (JsonExpression(constant.Where, token) || JsonOrder(constant.OrderBy, token));
            case SqlSystemViewPlan view:
                return (JsonExpression(view.Where, token) || JsonOrder(view.OrderBy, token));
            case SqlJoinPlan join:
                if (JsonTree(join.Tree, token)) { return true; }
                for (int index = 0; index < join.Inputs.Count; index++)
                { if (JsonExpression(join.Inputs[index].Filter, token)) { return true; } }
                for (int index = 0; index < join.Bindings.Count; index++)
                {
                    var bindingSource = join.Bindings[index].Source;
                    if (bindingSource.Table is { } table && _definitions.PreparedTable(table)?.ReadsJsonPath == true ||
                        bindingSource.Derived is { } derived && PlanUsesJson(derived.Query, token)) { return true; }
                }
                for (int index = 0; index < join.Steps.Count; index++)
                { if (JsonExpression(join.Steps[index].Condition, token)) { return true; } }
                return JsonExpression(join.Where, token) || JsonOrder(join.OrderBy, token);
            case SqlGroupPlan group:
                if (PlanUsesJson(group.Input, token)) { return true; }
                for (int index = 0; index < group.Keys.Count; index++)
                { if (JsonExpression(group.Keys[index].Value, token)) { return true; } }
                for (int index = 0; index < group.Aggregates.Count; index++)
                {
                    var aggregate = group.Aggregates[index];
                    if (JsonExpressions(aggregate.Arguments, token) || JsonExpression(aggregate.Filter, token) || JsonOrder(aggregate.OrderBy, token)) { return true; }
                }
                return JsonExpression(group.Having, token) || JsonOrder(group.OrderBy, token);
            case SqlWindowAggregatePlan window:
                if (PlanUsesJson(window.Input, token)) { return true; }
                for (int index = 0; index < window.Groups.Count; index++)
                {
                    var windowGroup = window.Groups[index];
                    if (JsonOrder(windowGroup.Partition, token) || JsonOrder(windowGroup.Ordering, token)) { return true; }
                    foreach (var function in windowGroup.Functions)
                    {
                        if (JsonExpressions(function.Arguments, token) || JsonExpression(function.Frame?.StartOffset, token) ||
                            JsonExpression(function.Frame?.EndOffset, token) || function.Aggregate is { } aggregate &&
                            (JsonExpressions(aggregate.Arguments, token) || JsonExpression(aggregate.Filter, token) || JsonOrder(aggregate.OrderBy, token))) { return true; }
                    }
                }
                return JsonOrder(window.OrderBy, token);
            case SqlSequenceScopePlan sequence:
                return PlanUsesJson(sequence.Input, token);
            case SqlWithPlan with:
                for (int index = 0; index < with.Relations.Count; index++)
                { if (PlanUsesJson(with.Relations[index].Query, token)) { return true; } }
                return PlanUsesJson(with.Input, token);
            case SqlSubqueryPlan subqueries:
                return JsonQueries(subqueries.Queries, token) || PlanUsesJson(subqueries.Input, token);
            case SqlCorrelatedSubqueryPlan correlated:
                return JsonQueries(correlated.Queries, token) || PlanUsesJson(correlated.Input, token);
            case SqlSetOperationPlan set:
                for (int index = 0; index < set.Operands.Count; index++)
                { if (PlanUsesJson(set.Operands[index].Query, token)) { return true; } }
                return JsonOrder(set.OrderBy, token);
            case SqlRecursiveUnionPlan recursive:
                return PlanUsesJson(recursive.Anchor.Query, token) || PlanUsesJson(recursive.Recursive.Query, token);
            case SqlInsertPlan insert:
                if (WriteUsesJson(insert.Table, token) || JsonInsertDefaults(insert.Table, insert.TargetOrdinals, insert.Rows, token) ||
                    JsonProjections(insert.Returning, token)) { return true; }
                for (int index = 0; index < insert.Rows.Count; index++)
                { if (JsonExpressions(insert.Rows[index], token)) { return true; } }
                return JsonConflict(insert.Table, insert.OnConflict, token);
            case SqlInsertSelectPlan insert:
                return WriteUsesJson(insert.Table, token) || JsonInsertDefaults(insert.Table, insert.TargetOrdinals, null, token) || PlanUsesJson(insert.Source, token) ||
                    JsonConflict(insert.Table, insert.OnConflict, token) || JsonProjections(insert.Returning, token);
            case SqlUpdatePlan update:
                return WriteUsesJson(update.Table, token, updating: true) || JsonAssignedDefaults(update.Table, update.Assignments, token) ||
                    update.Source is { } updateSource && PlanUsesJson(updateSource.Associations, token) ||
                    (JsonAssignments(update.Assignments, token) || JsonExpression(update.Where, token) || JsonProjections(update.Returning, token));
            case SqlDeletePlan delete:
                return WriteUsesJson(delete.Table, token, deleting: true) || delete.Source is { } deleteSource && PlanUsesJson(deleteSource.Associations, token) ||
                    (JsonExpression(delete.Where, token) || JsonProjections(delete.Returning, token));
            case SqlMergePlan merge:
                if (PlanUsesJson(merge.Source.Associations, token)) { return true; }
                for (int index = 0; index < merge.Actions.Count; index++)
                {
                    var action = merge.Actions[index];
                    if (action.Action != Language.SqlMergeAction.DoNothing && WriteUsesJson(merge.Table, token, updating: action.Action == Language.SqlMergeAction.Update,
                            deleting: action.Action == Language.SqlMergeAction.Delete) ||
                        action.Action == Language.SqlMergeAction.Insert && JsonInsertDefaults(merge.Table, action.InsertOrdinals, null, token, action.InsertValues) ||
                        JsonAssignedDefaults(merge.Table, action.Assignments, token) ||
                        (JsonExpression(action.Condition, token) || JsonAssignments(action.Assignments, token) ||
                        JsonExpressions(action.InsertValues, token) || JsonQueries(action.Queries, token))) { return true; }
                }
                return (JsonExpression(merge.Condition, token) || JsonProjections(merge.Returning, token));
            case SqlCreateTableAsPlan { Source: { } source }:
                return PlanUsesJson(source, token);
            case SqlCreateIndexPlan { PartialIndex: { } index } create:
                return JsonFunctionKeys(index.PredicateFunctionCalls, token) || JsonExpression(_definitions.GetIndexPredicate(create.Table, index, token), token);
            case SqlAlterColumnPlan alter:
                return JsonExpression(alter.Conversion, token) || alter.TypeChange && WriteUsesJson(alter.Table, token, updating: true) ||
                    (alter.TypeChange || alter.AllowDefaultChange) && JsonColumn(alter.Column, token);
            case SqlAddConstraintPlan add:
                return JsonSyntax(add.Constraint.CheckExpression, token);
            case SqlAddColumnPlan add:
                return JsonColumn(add.Column, token) || JsonConstraints(add.Constraints, token);
            case SqlCreateTablePlan create:
                for (int index = 0; index < create.Columns.Count; index++)
                { if (JsonColumn(create.Columns[index], token)) { return true; } }
                return JsonConstraints(create.Constraints, token);
            case SqlCreateViewPlan create:
                return JsonFunctionKeys(create.View.FunctionCalls, token);
            case SqlDefinitionPlan { Expression: SqlCreateFunctionExpression function }:
                return JsonSyntax(function.Body, token);
            case SqlDefinitionPlan { Expression: SqlCreateDomainExpression domain }:
                return JsonSyntax(domain.Check, token) || JsonSyntax(domain.DefaultValue, token);
            default:
                return false;
        }
    }

    private bool WriteUsesJson(SqlCatalogTable table, CancellationToken token, bool updating = false, bool deleting = false,
        HashSet<(ulong, bool)>? visited = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var bound = _definitions.PreparedTable(table);
        if (bound is null) { return true; }
        if (deleting ? bound.ReadsJsonPath : bound.WritesJsonPath) { return true; }
        var indexes = _catalog.GetPlanningIndexes(table.ObjectId);
        for (int index = 0; index < indexes.Count; index++)
        {
            if (indexes[index].Predicate is not null && JsonExpression(_definitions.GetIndexPredicate(table, indexes[index], token), token)) { return true; }
        }
        if ((!updating && !deleting) || !_catalog.HasIncomingReferences(table) || !_definitions.MayHaveJsonWrites(token)) { return false; }
        visited ??= [];
        if (!visited.Add((table.ObjectId, deleting))) { return false; }
        foreach (var (child, constraint) in IncomingReferences(table))
        {
            var action = deleting ? constraint.OnDelete : constraint.OnUpdate;
            if (!Changes(action)) { continue; }
            if (action == SqlCatalogReferentialAction.SetDefault)
            {
                var defaults = _definitions.Get(child, token).DefaultValues;
                foreach (string column in constraint.Columns)
                { if (JsonExpression(defaults[FindColumnOrdinal(child, column)], token)) { return true; } }
            }
            bool deleteChild = deleting && action == SqlCatalogReferentialAction.Cascade;
            if (WriteUsesJson(child, token, updating: !deleteChild, deleting: deleteChild, visited)) { return true; }
        }
        return false;

        static bool Changes(SqlCatalogReferentialAction action)
            => action is SqlCatalogReferentialAction.Cascade or SqlCatalogReferentialAction.SetNull or SqlCatalogReferentialAction.SetDefault;
    }

    private bool JsonExpression(SqlBoundExpression? expression, CancellationToken token)
        => SqlJsonUsage.Contains(expression, _catalog.DefaultCollation, token);

    private bool JsonSyntax(SqlExpression? expression, CancellationToken token)
        => SqlJsonUsage.ContainsSyntax(expression, _definitions.Functions, _catalog.DefaultCollation, token);

    private bool JsonConstraints(IReadOnlyList<SqlConstraintDefinition> constraints, CancellationToken token)
    {
        for (int index = 0; index < constraints.Count; index++)
        { if (JsonSyntax(constraints[index].CheckExpression, token)) { return true; } }
        return false;
    }

    private bool JsonColumn(SqlCatalogColumn column, CancellationToken token)
        => column.DomainName is { } name && SqlJsonUsage.FunctionContains(_definitions.Functions.Types.Find(name)?.Check, _catalog.DefaultCollation, token) ||
            JsonFunctionKeys(column.DefaultFunctionCalls, token);

    private bool JsonFunctionKeys(IReadOnlyList<string> keys, CancellationToken token)
    {
        if (keys.Count == 0) { return false; }
        foreach (var function in _definitions.Functions.Catalog)
        {
            if (function is not SqlScalarFunction scalar || !SqlJsonUsage.FunctionContains(scalar, _catalog.DefaultCollation, token)) { continue; }
            string key = SqlDefinitionBinder.Key(function);
            for (int index = 0; index < keys.Count; index++)
            { if (string.Equals(keys[index], key, StringComparison.OrdinalIgnoreCase)) { return true; } }
        }
        return false;
    }

    private bool JsonCounts(SqlPlan plan, CancellationToken token)
    {
        (SqlRowCount? Limit, SqlRowCount? Offset) counts = plan switch
        {
            SqlConstantPlan p => (p.Limit, p.Offset), SqlSelectPlan p => (p.Limit, p.Offset),
            SqlSystemViewPlan p => (p.Limit, p.Offset), SqlJoinPlan p => (p.Limit, p.Offset),
            SqlGroupPlan p => (p.Limit, p.Offset), SqlWindowAggregatePlan p => (p.Limit, p.Offset),
            SqlSetOperationPlan p => (p.Limit, p.Offset), _ => (null, null),
        };
        return JsonExpression(counts.Limit?.Expression, token) || JsonExpression(counts.Offset?.Expression, token);
    }

    private bool JsonTree(SqlJoinTree? tree, CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return tree is not null && (JsonExpression(tree.Step?.Condition, token) || JsonTree(tree.Left, token) || JsonTree(tree.Right, token));
    }

    private bool JsonConflict(SqlCatalogTable table, SqlOnConflictPlan? conflict, CancellationToken token)
        => conflict is not null && (JsonAssignedDefaults(table, conflict.Assignments, token) || (JsonAssignments(conflict.Assignments, token) || JsonExpression(conflict.Where, token)));

    private bool JsonInsertDefaults(SqlCatalogTable table, IReadOnlyList<int> ordinals,
        IReadOnlyList<IReadOnlyList<SqlBoundExpression>>? rows, CancellationToken token, IReadOnlyList<SqlBoundExpression>? singleRow = null)
    {
        var bound = _definitions.Get(table, token);
        if (!bound.DefaultsUseJsonPath) { return false; }
        for (int column = 0; column < table.Columns.Count; column++)
        {
            int position = -1;
            for (int index = 0; index < ordinals.Count; index++) { if (ordinals[index] == column) { position = index; break; } }
            bool usesDefault = position < 0 || singleRow is not null && singleRow[position] is SqlBoundDefaultItem;
            for (int index = 0; !usesDefault && rows is not null && index < rows.Count; index++)
            { usesDefault = rows[index][position] is SqlBoundDefaultItem; }
            if (usesDefault && JsonExpression(bound.DefaultValues[column], token)) { return true; }
        }
        return false;
    }

    private bool JsonAssignedDefaults(SqlCatalogTable table, IReadOnlyList<(int Ordinal, SqlBoundExpression Value)> assignments, CancellationToken token)
    {
        var bound = _definitions.Get(table, token);
        if (!bound.DefaultsUseJsonPath) { return false; }
        for (int index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            if (assignment.Value is SqlBoundDefaultItem && JsonExpression(bound.DefaultValues[assignment.Ordinal], token)) { return true; }
        }
        return false;
    }

    private bool JsonProjections(IReadOnlyList<SqlProjection>? projections, CancellationToken token)
    {
        if (projections is null) { return false; }
        for (int index = 0; index < projections.Count; index++)
        { if (JsonExpression(projections[index].Value, token)) { return true; } }
        return false;
    }

    private bool JsonExpressions(IReadOnlyList<SqlBoundExpression> expressions, CancellationToken token)
    {
        for (int index = 0; index < expressions.Count; index++)
        { if (JsonExpression(expressions[index], token)) { return true; } }
        return false;
    }

    private bool JsonOrder(IReadOnlyList<SqlBoundOrdering> order, CancellationToken token)
    {
        for (int index = 0; index < order.Count; index++)
        { if (JsonExpression(order[index].Key, token)) { return true; } }
        return false;
    }

    private bool JsonQueries(IReadOnlyList<SqlSubqueryBinding> queries, CancellationToken token)
    {
        for (int index = 0; index < queries.Count; index++)
        { if (PlanUsesJson(queries[index].Query, token)) { return true; } }
        return false;
    }

    private bool JsonAssignments(IReadOnlyList<(int Ordinal, SqlBoundExpression Value)> assignments, CancellationToken token)
    {
        for (int index = 0; index < assignments.Count; index++)
        { if (JsonExpression(assignments[index].Value, token)) { return true; } }
        return false;
    }
}
