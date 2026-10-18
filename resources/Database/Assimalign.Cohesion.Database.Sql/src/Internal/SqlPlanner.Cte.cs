using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private SqlPlan PlanConstantCommonTable(SqlSelectExpression select)
    {
        var evaluator = new SqlExpressionEvaluator([], _parameters, defaultCollation: _catalog.DefaultCollation,
            subquerySlots: _subquerySlots, functions: _functions, cancellationToken: _cancellationToken,
            outerScope: OuterScope, outerDependencies: OuterDependencies);
        if (select.Columns.Any(column => column.Expression is SqlStarExpression))
        {
            throw new SqlPlanningException("COHSQLE013", ErrorMessages.CteConstantQuery, select.Location);
        }
        if (ViewDefinitionBinding is not null) { CaptureViewSelect(select, [], null, null, evaluator); }
        if (select.GroupBy.Count > 0 || select.Having is not null || ProjectsOrOrdersByAggregate(select))
        {
            return PlanGroup(select, null, null, [], null, evaluator, []);
        }
        var projections = new List<SqlProjection>();
        foreach (var column in select.Columns)
        {
            ValidateExpression(column.Expression, evaluator, _subqueryTypes);
            var value = evaluator.Bind(column.Expression, out var collation, out var candidate);
            projections.Add(new SqlProjection(column.Alias ?? $"column{projections.Count + 1}", null, column.Expression,
                GroupExpressionType(column.Expression, Array.Empty<SqlCatalogColumn>(), evaluator))
            {
                Value = value, Collation = collation, Metadata = CaptureProjectionMetadata(candidate),
                MaxLength = evaluator.MaximumLengthOf(column.Expression),
            });
        }
        SqlBoundExpression? where = null;
        if (select.Where is not null)
        {
            ValidateExpression(select.Where, evaluator, _subqueryTypes);
            where = evaluator.Bind(select.Where);
        }
        var outputSlots = BindOrderByProjections(select, projections, 0);
        CaptureViewOrdering(select, [], null, evaluator, outputSlots);
        foreach (var order in select.OrderBy) { ValidateExpression(order.Expression, evaluator, _subqueryTypes, outputSlots); }
        return new SqlConstantPlan(projections, where,
            BindOrdering(select.OrderBy, evaluator.ForOrdering(projections, outputSlots, 0)),
            BindCount(select.Limit, LimitClause(select)), BindCount(select.Offset, "OFFSET"), select.IsDistinct, outputSlots);
    }

    private SqlPlan PlanCommonTables(IReadOnlyList<SqlCommonTableExpression> definitions, SqlQueryExpression statement)
    {
        var state = _commonTableState!;
        var previous = (state.Relations, state.Future, state.Dependencies, state.BindingOperands, state.BindingUnused);
        state.Relations = previous.Relations is null
            ? new(StringComparer.OrdinalIgnoreCase) : new(previous.Relations, StringComparer.OrdinalIgnoreCase);
        state.Future = new(StringComparer.OrdinalIgnoreCase);
        var dependencies = new Dictionary<SqlDerivedRelation, HashSet<SqlDerivedRelation>>(ReferenceEqualityComparer.Instance);
        var ordered = new List<SqlDerivedRelation>();
        try
        {
            foreach (var definition in definitions)
            {
                if (!state.Future.Add(definition.Name))
                {
                    throw new SqlPlanningException("COHSQLE026", string.Format(CultureInfo.CurrentCulture,
                        ErrorMessages.CteDuplicateName, definition.Name), definition.Location);
                }
            }
            // PostgreSQL parse_cte.c:214-229 analyzes non-recursive definitions in
            // declaration order and publishes each only after its body is bound.
            foreach (var definition in definitions)
            {
                state.Future.Remove(definition.Name); // Self-reference falls through to the stored relation.
                state.Dependencies = new(ReferenceEqualityComparer.Instance);
                state.BindingOperands = CollectCommonTableOperands(definition.Body);
                state.BindingUnused = !state.Referenced.Contains(definition);
                ValidateFunctionCalls(definition.Body);
                var query = PlanQuery(definition.Body);
                var relation = BindDerivedOutput(query, definition.Name, definition.Columns, definition.Body).Derived! with { IsCommonTable = true };
                dependencies.Add(relation, state.Dependencies);
                state.Relations[definition.Name] = relation;
                ordered.Add(relation);
            }
            state.BindingOperands = null;
            state.BindingUnused = previous.BindingUnused;
            state.Dependencies = new(ReferenceEqualityComparer.Instance);
            var input = statement is SqlQueryBodyExpression body ? PlanQueryBody(body) : PlanStatementCore(statement);
            var reachable = new HashSet<SqlDerivedRelation>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<SqlDerivedRelation>(state.Dependencies);
            while (pending.TryPop(out var relation))
            {
                if (!reachable.Add(relation)) { continue; }
                if (dependencies.TryGetValue(relation, out var children))
                {
                    foreach (var dependency in children) { pending.Push(dependency); }
                }
                else { previous.Dependencies?.Add(relation); }
            }
            return new SqlWithPlan(input, ordered.Where(reachable.Contains).ToArray());
        }
        finally
        {
            (state.Relations, state.Future, state.Dependencies, state.BindingOperands, state.BindingUnused) = previous;
        }
    }

    private HashSet<SqlSelectExpression> CollectCommonTableOperands(SqlQueryBodyExpression query)
    {
        // Only direct SELECT operands receive the CTE's implicit row. Do not descend
        // into scalar subqueries or derived tables, which bind their own query scopes.
        var operands = new HashSet<SqlSelectExpression>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<SqlQueryBodyExpression>();
        pending.Push(query);
        while (pending.TryPop(out var body))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (body is SqlSetOperationExpression set) { pending.Push(set.Right); pending.Push(set.Left); }
            else { operands.Add((SqlSelectExpression)body); }
        }
        return operands;
    }

    // Stored-only planners retain one nullable reference; WITH alone allocates its scope state.
    private sealed class CommonTableState
    {
        internal CommonTableState(HashSet<SqlCommonTableExpression> referenced) { Referenced = referenced; }

        internal HashSet<SqlCommonTableExpression> Referenced { get; }
        internal Dictionary<string, SqlDerivedRelation>? Relations { get; set; }
        internal HashSet<string>? Future { get; set; }
        internal HashSet<SqlDerivedRelation>? Dependencies { get; set; }
        internal HashSet<SqlSelectExpression>? BindingOperands { get; set; }
        internal bool BindingUnused { get; set; }
    }
}
